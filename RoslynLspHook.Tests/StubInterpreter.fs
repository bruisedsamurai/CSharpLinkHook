module RoslynLspHook.Tests.StubInterpreter

open RoslynLspHook.Common
open RoslynLspHook.Effects

/// Canned effect results plus the stdin to feed `readStdin`. The state machine
/// in Logic.fs is pure (it only emits effect descriptions), so we interpret a
/// `Program` against these fixtures and record a transcript of effects performed.
type StubEnv =
    { Stdin: string
      DirExistsResult: bool
      ProbeResult: bool
      Diagnostics: Diagnostic list }

let defaultEnv =
    { Stdin = ""
      DirExistsResult = true
      ProbeResult = true
      Diagnostics = [] }

/// Interpret `p`, returning its result and the ordered list of effect labels:
///   read | log:<msg> | direxists:<path> | probe:<pipe> | launch:<pipe>
///   | fetch:<file> | stdout:<payload>
let run (env: StubEnv) (p: Program<'a>) : 'a * string list =
    let log = ResizeArray<string>()

    let rec go (p: Program<'a>) : 'a =
        match p with
        | Pure x -> x
        | ReadStdin k ->
            log.Add "read"
            go (k env.Stdin)
        | WriteStdout(s, k) ->
            log.Add("stdout:" + s)
            go (k ())
        | LogLine(s, k) ->
            log.Add("log:" + s)
            go (k ())
        | DirExists(path, k) ->
            log.Add("direxists:" + path)
            go (k env.DirExistsResult)
        | ProbeLsp(name, k) ->
            log.Add("probe:" + name)
            go (k env.ProbeResult)
        | LaunchLsp(cfg, k) ->
            log.Add("launch:" + cfg.PipeName)
            go (k ())
        | FetchDiagnostics(_, file, k) ->
            log.Add("fetch:" + file)
            go (k env.Diagnostics)

    let result = go p
    result, List.ofSeq log

/// True if any transcript entry starts with `prefix`.
let has (prefix: string) (log: string list) : bool =
    log |> List.exists (fun e -> e.StartsWith prefix)

/// The first transcript entry starting with `prefix`, sans the prefix.
let value (prefix: string) (log: string list) : string option =
    log
    |> List.tryFind (fun e -> e.StartsWith prefix)
    |> Option.map (fun e -> e.Substring prefix.Length)
