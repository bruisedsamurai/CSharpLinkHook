module RoslynLspMcp.Tools

open System.ComponentModel
open System.Threading.Tasks
open ModelContextProtocol.Server
open RoslynLspMcp.Models
open RoslynLspMcp.Effect
open RoslynLspMcp.Interpreter
open RoslynLspMcp.Queries

// The MCP tool surface. This is the one unavoidable object: the SDK discovers
// tools by reflecting over a `[<McpServerToolType>]` and constructs it via DI. It
// is a thin shell — each tool builds a pure `Free` program (Queries) and runs it
// through the interpreter against the injected `Env`. It first ensures the broker
// is reachable; every outcome (still loading, unavailable, nothing resolved) comes
// back as clear text, never an exception, so the agent can retry or move on.

/// Roslyn navigation tools exposed over the Model Context Protocol.
[<McpServerToolType>]
type RoslynTools(env: Env) =

    /// Ensure the broker is up, run the program, and render its result to a string.
    let run (program: Free<Result<'T, ToolError>>) (render: 'T -> string) : Task<string> =
        Task.Run(fun () ->
            if not (ensureReady env) then
                formatError Unavailable
            else
                match run env program with
                | Ok value -> render value
                | Error e -> formatError e)

    [<McpServerTool>]
    [<Description("Look up a C# class, struct, or interface by a symbol on a given line and return its documentation, the full signature of every constructor, and all of its public, protected, and internal properties (each with documentation). Use this to learn how to construct a type or what an argument's type looks like: pass the type-name token, the file it appears in, and the line it appears on.")>]
    member _.GetClassConstructorsAndProperties
        (
            [<Description("The type-name token to resolve as it appears in the file, e.g. \"Foo\".")>] symbol: string,
            [<Description("Workspace-relative (or absolute) path to the file containing the token, e.g. \"src/foo/usage.cs\".")>] file: string,
            [<Description("1-based line number in the file where the token appears.")>] line: int
        ) : Task<string> =
        run (classConstructorsAndProperties file line symbol) formatClass

    [<McpServerTool>]
    [<Description("Look up a C# class, struct, or interface by a symbol on a given line and return its public methods — both those declared on the type and those it inherits from base types/interfaces, grouped by the declaring type, each with documentation. Methods inherited from referenced assemblies or the .NET base class library are best-effort and the result says so when the list may be partial.")>]
    member _.GetClassMethods
        (
            [<Description("The type-name token to resolve as it appears in the file, e.g. \"Foo\".")>] symbol: string,
            [<Description("Workspace-relative (or absolute) path to the file containing the token, e.g. \"src/foo/usage.cs\".")>] file: string,
            [<Description("1-based line number in the file where the token appears.")>] line: int
        ) : Task<string> =
        run (classMethods file line symbol) formatMethods

    [<McpServerTool>]
    [<Description("Return every top-level type declared directly in an exact namespace (non-recursive: nested/child namespaces are excluded), each with its documentation. Pass the fully qualified namespace name, e.g. \"My.Project.Models\".")>]
    member _.GetNamespaceDeclarations
        (
            [<Description("The exact, fully qualified namespace name, e.g. \"My.Project.Models\".")>] ``namespace``: string
        ) : Task<string> =
        run (namespaceDeclarations ``namespace``) (formatNamespace ``namespace``)
