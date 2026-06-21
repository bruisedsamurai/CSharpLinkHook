module CSharpLintHook.Tests.Recording

open System.Collections.Generic
open CSharpLintHook.Common
open CSharpLintHook.Effects

/// One recorded effect — the ordered transcript a program produces as it runs. Tests
/// assert against this "log" instead of performing real IO or driving the Copilot CLI,
/// so the exact stdout, file writes, and decisions are observable and deterministic.
type Event =
    | StdinRead
    | StdoutWritten of string
    | FileReadAt of string
    | FileWritten of string * string
    | ExistenceChecked of string
    | DiffClassified of string
    | WholeFormatted of string
    | RangesFormatted of string * LineRange list
    | LineLogged of string

/// Scripted answers for the effects that read the outside world (stdin, filesystem,
/// git, Roslyn). Unspecified inputs get benign defaults, so a test states only what it
/// cares about and everything else stays inert.
type World =
    { Stdin: string
      Files: Map<string, string>
      Diffs: Map<string, DiffResult>
      FormatWhole: string -> string
      FormatRanges: string -> LineRange list -> string * int }

/// Empty stdin, no files, and formatting that returns its input unchanged. Override
/// fields per test with `{ world with ... }`.
let world: World =
    { Stdin = ""
      Files = Map.empty
      Diffs = Map.empty
      FormatWhole = id
      FormatRanges = fun content _ -> content, 0 }

/// A program's return value paired with the ordered transcript of effects it performed.
type Trace<'a> = { Result: 'a; Events: Event list }

/// Interpret a Program against a scripted World, recording every effect in order and
/// performing no real IO. `FileExists`/`ReadFile` consult `World.Files`; `ClassifyDiff`
/// consults `World.Diffs` (defaulting to whole-file); formatting uses the world's
/// functions. This is the test-side twin of `Interpreter.runAsync`.
let run (w: World) (p: Program<'a>) : Trace<'a> =
    let events = List<Event>()

    let rec go (p: Program<'a>) : 'a =
        match p with
        | Pure x -> x
        | ReadStdin k ->
            events.Add StdinRead
            go (k w.Stdin)
        | WriteStdout(s, k) ->
            events.Add(StdoutWritten s)
            go (k ())
        | ReadFile(path, k) ->
            events.Add(FileReadAt path)
            go (k (w.Files |> Map.tryFind path |> Option.defaultValue ""))
        | WriteFile(path, content, k) ->
            events.Add(FileWritten(path, content))
            go (k ())
        | FileExists(path, k) ->
            events.Add(ExistenceChecked path)
            go (k (w.Files.ContainsKey path))
        | ClassifyDiff(path, k) ->
            events.Add(DiffClassified path)
            go (k (w.Diffs |> Map.tryFind path |> Option.defaultValue FormatWhole))
        | FmtWhole(content, k) ->
            events.Add(WholeFormatted content)
            go (k (w.FormatWhole content))
        | FmtRanges(content, ranges, k) ->
            events.Add(RangesFormatted(content, ranges))
            go (k (w.FormatRanges content ranges))
        | LogLine(s, k) ->
            events.Add(LineLogged s)
            go (k ())

    let result = go p
    { Result = result; Events = List.ofSeq events }

/// The concatenated stdout the program emitted — what the CLI reads back from the hook.
let stdout (t: Trace<'a>) : string =
    t.Events
    |> List.choose (function
        | StdoutWritten s -> Some s
        | _ -> None)
    |> String.concat ""

/// The files the program wrote, in order, as (path, content) pairs.
let writes (t: Trace<'a>) : (string * string) list =
    t.Events
    |> List.choose (function
        | FileWritten(path, content) -> Some(path, content)
        | _ -> None)
