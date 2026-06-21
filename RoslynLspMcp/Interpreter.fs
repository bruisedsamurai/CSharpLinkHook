module RoslynLspMcp.Interpreter

open System.IO
open System.Threading
open RoslynLspMcp.Models
open RoslynLspMcp.LspJson
open RoslynLspMcp.Effect
open RoslynLspHook

// The real interpreter: it runs a `Free` program against the warm, per-workspace
// Roslyn broker (the same one the RoslynLsp plugin hosts). Every LSP effect here
// builds its request `params` (LspJson), forwards a passthrough exchange through
// the broker, and parses the reply — so programs deal only in parsed domain values
// and never see a pipe, a config, or JSON. We reuse the broker; we never spawn or
// own a Roslyn child.

/// Everything the interpreter needs to reach a workspace's broker. This is the
/// reader-style environment a program is run against; programs themselves never
/// see it. Replaces the old object: dependencies live here, behaviour is functions.
type Env = { Cwd: string; Config: Common.LspConfig }

module Env =
    /// Resolve the broker config (pipe name, command, wait budget) for a cwd.
    let create (cwd: string) : Env = { Cwd = cwd; Config = Config.mkConfig cwd }

/// Make sure the broker is reachable, spawning the daemon if needed. Non-blocking
/// with respect to workspace LOAD: it only ensures pipe connectivity. The
/// "still loading" case is surfaced per request as `Retry`, never waited on here.
let ensureReady (env: Env) : bool =
    LspProcess.ensureBroker env.Config

    if LspProcess.probe env.Config.PipeName 300 then
        true
    else
        // The daemon may have just been spawned; give the pipe a brief moment to
        // come up (connection readiness, not workspace load).
        let rec wait n =
            if LspProcess.probe env.Config.PipeName 250 then
                true
            elif n <= 0 then
                false
            else
                Thread.Sleep 250
                wait (n - 1)

        wait 20

/// One raw passthrough exchange, mapped from the broker reply to `Result<_, ToolError>`.
let private exchange (env: Env) (method: string) (paramsJson: string) (openPaths: string list) : Result<string, ToolError> =
    match LspClient.lspExchange env.Config method paramsJson openPaths |> Async.RunSynchronously with
    | LspClient.LspResult result -> Ok result
    | LspClient.LspNotReady msg -> Error(Retry msg)
    | LspClient.LspNoResult -> Error(NotFound "The language server returned no result.")
    | LspClient.LspUnavailable -> Error Unavailable

/// didOpen only real files that exist; the broker opens them before the request.
let private openFor (path: string) : string list =
    if path.Length > 0 && File.Exists path then [ path ] else []

/// Perform a program against the real broker. Each branch is in tail position, so
/// a long sequence of effects (e.g. a namespace scan) runs in constant stack.
let rec run (env: Env) (program: Free<'a>) : 'a =
    match program with
    | Pure a -> a
    | Impure op ->
        match op with
        | GetCwd k -> run env (k env.Cwd)
        | ReadFile(path, k) ->
            let text =
                try
                    if File.Exists path then Some(File.ReadAllText path) else None
                with _ ->
                    None

            run env (k text)
        | FileExists(path, k) ->
            let ok =
                try
                    path.Length > 0 && File.Exists path
                with _ ->
                    false

            run env (k ok)
        | EnumerateCs k ->
            let files =
                try
                    Directory.EnumerateFiles(env.Cwd, "*.cs", SearchOption.AllDirectories)
                    |> Seq.filter (fun p ->
                        let n = p.Replace('/', '\\')
                        not (n.Contains "\\obj\\") && not (n.Contains "\\bin\\"))
                    |> List.ofSeq
                with _ ->
                    []

            run env (k files)
        | Definition(uri, line, character, k) ->
            let r =
                exchange env "textDocument/definition" (positionParams uri line character) (openFor (uriToPath uri))
                |> Result.map parseDefinition

            run env (k r)
        | DocumentSymbols(uri, k) ->
            let r =
                exchange env "textDocument/documentSymbol" (documentSymbolParams uri) (openFor (uriToPath uri))
                |> Result.map parseDocumentSymbols

            run env (k r)
        | Hover(uri, line, character, k) ->
            let r =
                exchange env "textDocument/hover" (positionParams uri line character) (openFor (uriToPath uri))
                |> Result.map parseHover

            run env (k r)
        | PrepareHierarchy(uri, line, character, k) ->
            let r =
                exchange env "textDocument/prepareTypeHierarchy" (positionParams uri line character) (openFor (uriToPath uri))
                |> Result.map parseHierarchyItems

            run env (k r)
        | Supertypes(itemRaw, itemUri, k) ->
            let r =
                exchange env "typeHierarchy/supertypes" (supertypesParams itemRaw) (openFor (uriToPath itemUri))
                |> Result.map parseHierarchyItems

            run env (k r)
