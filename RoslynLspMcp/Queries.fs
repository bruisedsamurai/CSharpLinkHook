module RoslynLspMcp.Queries

open System.IO
open RoslynLspMcp.Models
open RoslynLspMcp.LspJson
open RoslynLspMcp.Find
open RoslynLspMcp.Effect
open RoslynLspHook

// The three tools, expressed as pure programs in the LSP effect algebra (`Free`).
// Each is a sequence of effect descriptions — definition, documentSymbol, hover,
// typeHierarchy, filesystem reads — threaded with `ToolError` so the
// workspace-still-loading and unreachable cases surface as clear messages rather
// than empty output. Documentation is the server's hover content, verbatim. These
// values DESCRIBE the work; `Interpreter.run` (or a stub) performs it.

/// Hover a document-symbol node, absorbing any failure to "no documentation".
let private hoverNode (uri: string) (node: SymbolNode) : Free<string option> =
    lsp {
        let! r = hover uri node.SelLine node.SelCharacter

        return
            match r with
            | Ok(Some s) -> Some s
            | _ -> None
    }

/// A node rendered as an output member: its `detail` label plus verbatim hover.
let private memberOf (uri: string) (node: SymbolNode) : Free<MemberInfo> =
    lsp {
        let! hv = hoverNode uri node

        return
            { Label = (if node.Detail.Length > 0 then node.Detail else node.Name)
              Hover = hv }
    }

let private isPublicMethod (typeName: string) (c: SymbolNode) : bool =
    c.Kind = Kind.Method && not (isConstructor typeName c) && isPublicMethodGlyph c.Glyph

let rec private findTypeNode (nodes: SymbolNode list) (name: string) : SymbolNode option =
    let simple = simpleTypeName name

    nodes
    |> List.tryPick (fun n ->
        if isTypeKind n.Kind && simpleTypeName n.Name = simple then
            Some n
        else
            findTypeNode n.Children name)

/// Resolve a `(symbol, file, line)` triple to its enclosing type declaration:
/// find the token on the line, ask for its definition, then climb the definition
/// file's document-symbol tree to the type that contains it. Yields the
/// definition's uri and the type node.
let resolveType (file: string) (line: int) (symbol: string) : Free<Result<string * SymbolNode, ToolError>> =
    lsp {
        let! cwd = getCwd
        let path = if Path.IsPathRooted file then file else Path.Combine(cwd, file)
        let! contents = readFile path

        match contents with
        | None -> return Error(NotFound $"File not found: {file}")
        | Some text ->
            match findToken text line symbol with
            | None -> return Error(NotFound $"Could not find '{symbol}' on line {line} of {file}.")
            | Some ch ->
                let uri = Lsp.fileUri path
                let! defR = definition uri (line - 1) ch

                match defR with
                | Error e -> return Error e
                | Ok None -> return Error(NotFound $"No definition found for '{symbol}' (line {line} of {file}).")
                | Ok(Some loc) ->
                    let! dsR = documentSymbols loc.Uri

                    match dsR with
                    | Error e -> return Error e
                    | Ok symbols ->
                        match enclosingType symbols loc.Line loc.Character with
                        | Some node -> return Ok(loc.Uri, node)
                        | None ->
                            return
                                Error(
                                    NotFound
                                        $"Resolved '{symbol}' but could not locate its enclosing type declaration; it may be defined in a referenced assembly or the .NET base class library."
                                )
    }

/// Tool 1: a class's documentation, every constructor signature, and its
/// public/protected/internal properties — each with verbatim documentation.
let classConstructorsAndProperties (file: string) (line: int) (symbol: string) : Free<Result<ClassInfo, ToolError>> =
    lsp {
        let! r = resolveType file line symbol

        match r with
        | Error e -> return Error e
        | Ok(uri, node) ->
            let! ctors = node.Children |> List.filter (isConstructor node.Name) |> mapM (memberOf uri)

            let! props =
                node.Children
                |> List.filter (fun c -> c.Kind = Kind.Property && isVisiblePropertyGlyph c.Glyph)
                |> mapM (memberOf uri)

            let! doc = hoverNode uri node

            return
                Ok
                    { TypeName = node.Name
                      Hover = doc
                      Constructors = ctors
                      Properties = props }
    }

/// Walk the supertype chain (bounded depth), gathering each base type's public
/// methods grouped by base. Threads `visited` (cycle guard) and a `partial` flag
/// set when a base comes from a referenced assembly / the BCL and so can't be read
/// from source.
let rec private collectInherited
    (visited: Set<string>)
    (depth: int)
    (item: HierarchyItem)
    : Free<Set<string> * (string * MemberInfo list) list * bool> =
    if depth <= 0 then
        Pure(visited, [], false)
    else
        lsp {
            let! supR = supertypes item.Raw item.Uri

            match supR with
            | Error _ -> return (visited, [], false)
            | Ok bases ->
                let bases = bases |> List.filter (fun b -> isTypeKind b.Kind)

                return!
                    foldM
                        (fun (vis, groups, partial) (b: HierarchyItem) ->
                            let key = b.Uri + "#" + b.Name

                            if Set.contains key vis then
                                Pure(vis, groups, partial)
                            else
                                let vis = Set.add key vis

                                lsp {
                                    let! exists = fileExists (uriToPath b.Uri)

                                    if exists then
                                        let! dsR = documentSymbols b.Uri

                                        let methodNodes =
                                            match dsR with
                                            | Ok symbols ->
                                                match findTypeNode symbols b.Name with
                                                | Some tn -> tn.Children |> List.filter (isPublicMethod tn.Name)
                                                | None -> []
                                            | Error _ -> []

                                        let! ms = methodNodes |> mapM (memberOf b.Uri)
                                        let here = if List.isEmpty ms then [] else [ (b.Name, ms) ]
                                        let! (vis2, deeper, deeperPartial) = collectInherited vis (depth - 1) b
                                        return (vis2, groups @ here @ deeper, partial || deeperPartial)
                                    else
                                        // Base from a referenced assembly / the BCL: not
                                        // enumerable from source, so flag the result partial.
                                        return (vis, groups, true)
                                })
                        (visited, [], false)
                        bases
        }

/// Tool 2: a type's public methods — those declared on it plus the ones it
/// inherits, grouped by the base type they come from.
let classMethods (file: string) (line: int) (symbol: string) : Free<Result<MethodInfo, ToolError>> =
    lsp {
        let! r = resolveType file line symbol

        match r with
        | Error e -> return Error e
        | Ok(uri, node) ->
            let! own = node.Children |> List.filter (isPublicMethod node.Name) |> mapM (memberOf uri)
            let! phR = prepareHierarchy uri node.SelLine node.SelCharacter

            let! (inherited, partial) =
                match phR with
                | Ok items ->
                    match List.tryHead items with
                    | Some selfItem ->
                        lsp {
                            let visited = Set.singleton (selfItem.Uri + "#" + selfItem.Name)
                            let! (_, groups, partial) = collectInherited visited 8 selfItem
                            return (groups, partial)
                        }
                    | None -> Pure([], false)
                | Error _ -> Pure([], false)

            return
                Ok
                    { TypeName = node.Name
                      OwnMethods = own
                      Inherited = inherited
                      Partial = partial }
    }

/// Collect every type declared DIRECTLY in `target` (non-recursive: a type whose
/// immediate container namespace equals `target`). Pure over a document-symbol tree.
let rec private collectNs
    (prefix: string)
    (uri: string)
    (target: string)
    (nodes: SymbolNode list)
    (acc: (string * SymbolNode) list)
    : (string * SymbolNode) list =
    nodes
    |> List.fold
        (fun acc n ->
            if n.Kind = Kind.Namespace then
                let full = if prefix = "" then n.Name else prefix + "." + n.Name

                let acc =
                    if full = target then
                        n.Children
                        |> List.filter (fun c -> isTypeKind c.Kind)
                        |> List.fold (fun a c -> (uri, c) :: a) acc
                    else
                        acc

                collectNs full uri target n.Children acc
            else
                acc)
        acc

/// Tool 3: every top-level type declared directly in the given namespace
/// (non-recursive — nested namespaces are excluded), each with its documentation.
let namespaceDeclarations (ns: string) : Free<Result<NamespaceDecl list, ToolError>> =
    lsp {
        let! files = enumerateCs

        // Gather (uri, node) pairs across files; a still-loading / unreachable reply
        // aborts the whole scan, a per-file NotFound just skips that file.
        let! gathered =
            foldM
                (fun (acc: Result<(string * SymbolNode) list, ToolError>) (f: string) ->
                    match acc with
                    | Error e -> Pure(Error e)
                    | Ok pairs ->
                        let uri = Lsp.fileUri f

                        lsp {
                            let! dsR = documentSymbols uri

                            return
                                match dsR with
                                | Error(Retry m) -> Error(Retry m)
                                | Error Unavailable -> Error Unavailable
                                | Error(NotFound _) -> Ok pairs
                                | Ok symbols -> Ok(pairs @ collectNs "" uri ns symbols [])
                        })
                (Ok [])
                files

        match gathered with
        | Error e -> return Error e
        | Ok pairs ->
            // Dedup by simple name, first occurrence wins, original order preserved.
            let _, deduped =
                pairs
                |> List.fold
                    (fun (seen: Set<string>, acc) (uri, node) ->
                        if Set.contains node.Name seen then
                            (seen, acc)
                        else
                            (Set.add node.Name seen, acc @ [ (uri, node) ]))
                    (Set.empty, [])

            let! decls =
                deduped
                |> mapM (fun (uri, node) ->
                    lsp {
                        let! hv = hoverNode uri node

                        return
                            ({ Label = (if node.Detail.Length > 0 then node.Detail else node.Name)
                               Hover = hv }
                            : NamespaceDecl)
                    })

            return Ok decls
    }
