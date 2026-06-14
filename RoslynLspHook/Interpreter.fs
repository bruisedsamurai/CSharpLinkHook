module RoslynLspHook.Interpreter

open System
open System.IO
open RoslynLspHook.Effects

/// The top layer: interpret a pure `Program` by performing its effects for real
/// (stdin/stdout, filesystem, the named-pipe probe/launch, and the LSP session).
/// This is the only module that performs IO.
let rec runAsync (p: Program<'a>) : Async<'a> =
    async {
        match p with
        | Pure x -> return x
        | ReadStdin k ->
            let input = Console.In.ReadToEnd()
            return! runAsync (k input)
        | WriteStdout(s, k) ->
            Console.Out.Write s
            return! runAsync (k ())
        | LogLine(s, k) ->
            eprintfn "%s" s
            return! runAsync (k ())
        | DirExists(path, k) -> return! runAsync (k (Directory.Exists path))
        | ProbeLsp(name, k) -> return! runAsync (k (LspProcess.probe name 300))
        | LaunchLsp(cfg, k) ->
            LspProcess.ensureStarted cfg
            return! runAsync (k ())
        | FetchDiagnostics(cfg, file, k) ->
            let! diags = LspClient.getDiagnostics cfg file
            return! runAsync (k diags)
    }

/// Convenience: interpret a program synchronously.
let run (p: Program<'a>) : 'a = runAsync p |> Async.RunSynchronously
