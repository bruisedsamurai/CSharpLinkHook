module RoslynLspMcp.Models

// Output records and the single-string formatting each tool returns. The
// documentation embedded here is the language server's hover content, reproduced
// VERBATIM with no trimming (a workspace fact). Headers/labels are added around it
// for readability but the hover text itself is never altered.

/// A class member (constructor, property, or method) for output: a compact label
/// taken from the document symbol (`detail`), plus the verbatim hover (full
/// signature + XML doc) which is preferred for display when present.
type MemberInfo =
    { Label: string
      Hover: string option }

/// A resolved class / interface / struct.
type ClassInfo =
    { TypeName: string
      Hover: string option
      Constructors: MemberInfo list
      Properties: MemberInfo list }

/// Methods of a type: those declared on it, plus inherited ones grouped by the
/// base type they came from. `Partial` is set when some base could not be fully
/// enumerated (e.g. it comes from a referenced assembly / the .NET BCL).
type MethodInfo =
    { TypeName: string
      OwnMethods: MemberInfo list
      Inherited: (string * MemberInfo list) list
      Partial: bool }

/// A top-level declaration found directly inside a namespace.
type NamespaceDecl = { Label: string; Hover: string option }

/// Why a tool could not produce a result. Each maps to a clear, non-crashing
/// message the tool returns to the caller.
type ToolError =
    /// The workspace is still loading; the caller should retry shortly.
    | Retry of string
    /// The broker / language server is unreachable.
    | Unavailable
    /// The request completed but nothing resolved (bad symbol, line, namespace…).
    | NotFound of string

let private partialNote =
    "Note: members inherited from referenced assemblies or the .NET base class library are best-effort and this list may be partial."

let private join (sep: string) (parts: string list) : string =
    parts |> List.filter (fun (s: string) -> s.Length > 0) |> String.concat sep

let private memberText (m: MemberInfo) : string =
    match m.Hover with
    | Some h when h.Length > 0 -> h
    | _ -> m.Label

let private memberList (empty: string) (members: MemberInfo list) : string =
    if List.isEmpty members then
        empty
    else
        members |> List.map memberText |> join "\n\n"

/// Format the message for a tool error.
let formatError (e: ToolError) : string =
    match e with
    | Retry msg -> $"The Roslyn workspace is still loading ({msg}). Please retry in a few seconds."
    | Unavailable ->
        "The Roslyn language server is not available. Make sure the workspace broker is running (the sessionStart hook / roslyn-start skill warms it) and retry."
    | NotFound msg -> msg

/// Tool 1 output: class documentation, every constructor's full signature, and all
/// public/protected/internal properties (each with its documentation).
let formatClass (c: ClassInfo) : string =
    let header = "# " + c.TypeName

    let doc =
        match c.Hover with
        | Some h when h.Length > 0 -> h
        | _ -> "(no documentation available)"

    let ctors = "## Constructors\n\n" + memberList "(no constructors)" c.Constructors
    let props = "## Properties\n\n" + memberList "(no public, protected, or internal properties)" c.Properties
    join "\n\n" [ header; doc; ctors; props ]

/// Tool 2 output: a type's public methods, declared and inherited.
let formatMethods (m: MethodInfo) : string =
    let header = "# " + m.TypeName + " — public methods"
    let own = "## Declared on " + m.TypeName + "\n\n" + memberList "(no public methods declared on this type)" m.OwnMethods

    let inherited =
        m.Inherited
        |> List.map (fun (baseName, methods) -> "## Inherited from " + baseName + "\n\n" + memberList "(none)" methods)

    let note = if m.Partial then partialNote else ""
    join "\n\n" ([ header; own ] @ inherited @ [ note ])

/// Tool 3 output: the top-level declarations in a namespace, each with its docs.
let formatNamespace (ns: string) (decls: NamespaceDecl list) : string =
    let header = "# namespace " + ns

    let body =
        if List.isEmpty decls then
            "(no top-level type declarations found whose container is exactly this namespace)"
        else
            decls
            |> List.map (fun d ->
                match d.Hover with
                | Some h when h.Length > 0 -> h
                | _ -> d.Label)
            |> join "\n\n"

    join "\n\n" [ header; body ]
