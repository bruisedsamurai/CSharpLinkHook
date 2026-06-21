module RoslynLspMcp.LspJson

open System
open System.Text.Json
open System.Text.Json.Nodes

// Pure LSP JSON: request `params` builders and response parsers for the four
// methods the tools use (definition, documentSymbol, hover, typeHierarchy). All
// shapes here were captured from the real roslyn-language-server (see the spike
// fixtures); parsing is defensive so a malformed/owise-shaped payload yields an
// empty/None result rather than throwing.

// --- small JSON helpers -----------------------------------------------------

let private asObj (n: JsonNode) : JsonObject option =
    match n with
    | :? JsonObject as o -> Some o
    | _ -> None

let private asArr (n: JsonNode) : JsonArray option =
    match n with
    | :? JsonArray as a -> Some a
    | _ -> None

let private prop (o: JsonObject) (name: string) : JsonNode option =
    let mutable v: JsonNode | null = null
    if o.TryGetPropertyValue(name, &v) then Option.ofObj v else None

let private getStr (n: JsonNode) : string option =
    try
        match n.GetValueKind() with
        | JsonValueKind.String -> Some(n.GetValue<string>())
        | _ -> None
    with _ ->
        None

let private getInt (n: JsonNode) : int option =
    try
        match n.GetValueKind() with
        | JsonValueKind.Number -> Some(n.GetValue<int>())
        | _ -> None
    with _ ->
        None

let private parse (json: string) : JsonNode option =
    try
        match JsonNode.Parse json with
        | null -> None
        | n -> Some n
    with _ ->
        None

let private chooseArr (f: JsonNode -> 'a option) (a: JsonArray) : 'a list =
    a |> Seq.choose (fun e -> e |> Option.ofObj |> Option.bind f) |> List.ofSeq

/// (line, character) of the `which` ("start"/"end") point of the `rangeName`
/// ("range"/"selectionRange") sub-object of `o`. Defaults to (0,0).
let private point (o: JsonObject) (rangeName: string) (which: string) : int * int =
    match
        prop o rangeName
        |> Option.bind asObj
        |> Option.bind (fun r -> prop r which |> Option.bind asObj)
    with
    | Some p ->
        let l = prop p "line" |> Option.bind getInt |> Option.defaultValue 0
        let c = prop p "character" |> Option.bind getInt |> Option.defaultValue 0
        l, c
    | None -> 0, 0

// --- LSP kinds & Roslyn glyphs (accessibility) ------------------------------

/// Standard LSP `SymbolKind` values used by the tools.
module Kind =
    let [<Literal>] Namespace = 3
    let [<Literal>] Class = 5
    let [<Literal>] Method = 6
    let [<Literal>] Property = 7
    let [<Literal>] Enum = 10
    let [<Literal>] Interface = 11
    let [<Literal>] Struct = 23

/// True for the symbol kinds that denote a top-level type declaration.
let isTypeKind (k: int) : bool =
    k = Kind.Class || k = Kind.Interface || k = Kind.Struct || k = Kind.Enum

// Roslyn sends a non-standard `glyph` int encoding accessibility:
//   methods:    public=49 protected=50 private=51 internal=52
//   properties: public=63 protected=64 private=65 internal=66
/// Public method glyph.
let isPublicMethodGlyph (g: int) : bool = g = 49

/// Property visible to API consumers: public(63)/protected(64)/internal(66), not private(65).
let isVisiblePropertyGlyph (g: int) : bool = g = 63 || g = 64 || g = 66

// --- request params ---------------------------------------------------------

let positionParams (fileUri: string) (line: int) (character: int) : string =
    let o = JsonObject()
    let td = JsonObject()
    td["uri"] <- JsonValue.Create fileUri
    o["textDocument"] <- td
    let pos = JsonObject()
    pos["line"] <- JsonValue.Create line
    pos["character"] <- JsonValue.Create character
    o["position"] <- pos
    o.ToJsonString()

let documentSymbolParams (fileUri: string) : string =
    let o = JsonObject()
    let td = JsonObject()
    td["uri"] <- JsonValue.Create fileUri
    o["textDocument"] <- td
    o.ToJsonString()

/// `typeHierarchy/supertypes` params embedding a prepared item verbatim — the
/// item's opaque `data` must be round-tripped back to the server unchanged.
let supertypesParams (itemJson: string) : string =
    let item =
        try
            match JsonNode.Parse itemJson with
            | null -> JsonObject() :> JsonNode
            | n -> n
        with _ ->
            JsonObject() :> JsonNode

    let o = JsonObject()
    o["item"] <- item
    o.ToJsonString()

// --- response parsers -------------------------------------------------------

/// A resolved location (target of go-to-definition).
type Location =
    { Uri: string
      Line: int
      Character: int }

/// A document-symbol tree node (hierarchical documentSymbol).
type SymbolNode =
    { Name: string
      Detail: string
      Kind: int
      Glyph: int
      StartLine: int
      StartCharacter: int
      EndLine: int
      EndCharacter: int
      SelLine: int
      SelCharacter: int
      Children: SymbolNode list }

/// A type-hierarchy item; `Raw` is the verbatim item JSON for the supertypes call.
type HierarchyItem =
    { Name: string
      Kind: int
      Uri: string
      SelLine: int
      SelCharacter: int
      Raw: string }

let private locOf (o: JsonObject) : Location option =
    // Location: {uri, range}; LocationLink: {targetUri, targetSelectionRange/targetRange}.
    let uri =
        prop o "uri" |> Option.orElse (prop o "targetUri") |> Option.bind getStr

    let rangeName =
        if (prop o "range").IsSome then "range"
        elif (prop o "targetSelectionRange").IsSome then "targetSelectionRange"
        else "targetRange"

    match uri with
    | Some u ->
        let line, ch = point o rangeName "start"
        Some { Uri = u; Line = line; Character = ch }
    | None -> None

/// Parse a `textDocument/definition` result (single or array of Location/LocationLink).
let parseDefinition (json: string) : Location option =
    match parse json with
    | Some(:? JsonArray as a) -> a |> chooseArr (fun e -> asObj e |> Option.bind locOf) |> List.tryHead
    | Some(:? JsonObject as o) -> locOf o
    | _ -> None

let rec private toSymbol (o: JsonObject) : SymbolNode =
    let sLine, sChar = point o "range" "start"
    let eLine, eChar = point o "range" "end"
    let selLine, selChar = point o "selectionRange" "start"

    let children =
        prop o "children"
        |> Option.bind asArr
        |> Option.map (chooseArr (fun e -> asObj e |> Option.map toSymbol))
        |> Option.defaultValue []

    { Name = prop o "name" |> Option.bind getStr |> Option.defaultValue ""
      Detail = prop o "detail" |> Option.bind getStr |> Option.defaultValue ""
      Kind = prop o "kind" |> Option.bind getInt |> Option.defaultValue -1
      Glyph = prop o "glyph" |> Option.bind getInt |> Option.defaultValue -1
      StartLine = sLine
      StartCharacter = sChar
      EndLine = eLine
      EndCharacter = eChar
      SelLine = selLine
      SelCharacter = selChar
      Children = children }

/// Parse a hierarchical `textDocument/documentSymbol` result.
let parseDocumentSymbols (json: string) : SymbolNode list =
    match parse json with
    | Some(:? JsonArray as a) -> a |> chooseArr (fun e -> asObj e |> Option.map toSymbol)
    | _ -> []

/// Parse a `textDocument/hover` result's `contents.value` markdown (verbatim).
let parseHover (json: string) : string option =
    match parse json with
    | Some(:? JsonObject as o) ->
        match prop o "contents" with
        | Some(:? JsonObject as co) -> prop co "value" |> Option.bind getStr
        | Some c -> getStr c
        | None -> None
    | _ -> None

let private hierarchyItemOf (o: JsonObject) : HierarchyItem =
    let selLine, selChar = point o "selectionRange" "start"

    { Name = prop o "name" |> Option.bind getStr |> Option.defaultValue ""
      Kind = prop o "kind" |> Option.bind getInt |> Option.defaultValue -1
      Uri = prop o "uri" |> Option.bind getStr |> Option.defaultValue ""
      SelLine = selLine
      SelCharacter = selChar
      Raw = o.ToJsonString() }

/// Parse a `prepareTypeHierarchy` / `typeHierarchy/supertypes` item array.
let parseHierarchyItems (json: string) : HierarchyItem list =
    match parse json with
    | Some(:? JsonArray as a) -> a |> chooseArr (fun e -> asObj e |> Option.map hierarchyItemOf)
    | _ -> []

/// The simple type name of a document-symbol type node (already simple, but strip
/// any generic arity suffix defensively).
let simpleTypeName (name: string) : string =
    match name.IndexOf '<' with
    | i when i > 0 -> name.Substring(0, i)
    | _ -> name

/// True when `node` is a constructor of the type named `typeName` (a method-kind
/// child whose name before `(` equals the simple type name, e.g. `Dog(string)`).
let isConstructor (typeName: string) (node: SymbolNode) : bool =
    node.Kind = Kind.Method
    && (let nm = node.Name

        let beforeParen =
            match nm.IndexOf '(' with
            | i when i >= 0 -> nm.Substring(0, i)
            | _ -> nm

        beforeParen.Trim() = simpleTypeName typeName)

/// Convert an LSP file uri to a local path, or "" if it is not a file uri.
let uriToPath (uri: string) : string =
    try
        let u = Uri uri
        if u.IsFile then u.LocalPath else ""
    with _ ->
        ""
