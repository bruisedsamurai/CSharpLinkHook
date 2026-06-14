module CSharpLintHook.Interpreter

open System
open System.IO
open System.Threading
open Microsoft.CodeAnalysis.Text
open CSharpLintHook.Effects

/// The top layer: interpret a pure Program by performing its effects for real
/// (stdin/stdout, filesystem, git, Roslyn). This is the only module that does IO.
let rec runAsync (p: Program<'a>) : Async<'a> =
    async {
        match p with
        | Pure x -> return x
        | ReadStdin k ->
            let input = Console.In.ReadToEnd()
            return! runAsync (k input)
        | WriteStdout(s, k) ->
            Console.Out.Write(s)
            return! runAsync (k ())
        | ReadFile(path, k) ->
            let content = File.ReadAllText path
            return! runAsync (k content)
        | WriteFile(path, content, k) ->
            File.WriteAllText(path, content)
            return! runAsync (k ())
        | FileExists(path, k) -> return! runAsync (k (File.Exists path))
        | ClassifyDiff(path, k) -> return! runAsync (k (Git.classify path))
        | FmtWhole(content, k) ->
            let! formatted = Formatting.formatAllAsync (SourceText.From content) CancellationToken.None
            return! runAsync (k (formatted.ToString()))
        | FmtRanges(content, ranges, k) ->
            let! (formatted, regions) =
                Formatting.formatRangesAsync (SourceText.From content) ranges CancellationToken.None

            return! runAsync (k (formatted.ToString(), regions))
        | LogLine(s, k) ->
            eprintfn "%s" s
            return! runAsync (k ())
    }

/// Convenience: interpret a program synchronously.
let run (p: Program<'a>) : 'a = runAsync p |> Async.RunSynchronously
