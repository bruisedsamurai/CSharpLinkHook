module CSharpLintHook.Tests.RecordingTests

open System.IO
open Xunit
open CSharpLintHook
open CSharpLintHook.Common
open CSharpLintHook.Tests.Recording

// These tests run the *real* Logic program (`hookFormat`) through the recording
// interpreter, then verify behaviour against the recorded effect transcript.
// No Console redirection, git, Roslyn, or Copilot CLI — just the program's decisions.

let private q (s: string) =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// A camelCase postToolUse payload; `innerArgs` is raw JSON spliced in as `toolArgs`.
let private payload (toolName: string) (cwd: string) (resultType: string) (innerArgs: string) : string =
    $"""{{"cwd":%s{q cwd},"toolName":%s{q toolName},"toolResult":{{"resultType":%s{q resultType}}},"toolArgs":%s{innerArgs}}}"""

// ---- format flow -----------------------------------------------------------------

/// The path Logic resolves for `relPath` under `cwd`, matching `touchedCSharpFile`.
let private resolved (cwd: string) (relPath: string) = Payload.resolvePath cwd relPath

let private repoCwd =
    if System.OperatingSystem.IsWindows() then @"C:\repo" else "/repo"

[<Fact>]
let ``format flow records the file write and the additionalContext for a changed region`` () =
    let full = resolved repoCwd "C.cs"

    let trace =
        run
            { world with
                Stdin = payload "edit" repoCwd "success" """{"path":"C.cs"}"""
                Files = Map [ full, "messy" ]
                Diffs = Map [ full, Changed [ { StartLine = 0; EndLineInclusive = 1 } ] ]
                FormatRanges = fun _ _ -> "formatted", 1 }
            Logic.hookFormat

    // The log shows it wrote the formatted text back to the resolved path, exactly once.
    Assert.Equal<(string * string) list>([ full, "formatted" ], writes trace)
    // And told the model what changed via stdout — a region count, not a whole-file note.
    Assert.Contains("changed region(s) in C.cs", stdout trace)
    Assert.True(trace.Events |> List.exists (function RangesFormatted _ -> true | _ -> false))
    Assert.False(trace.Events |> List.exists (function WholeFormatted _ -> true | _ -> false))

[<Fact>]
let ``format flow reformats whole and says so for an untracked file`` () =
    let full = resolved repoCwd "New.cs"

    let trace =
        run
            { world with
                Stdin = payload "create" repoCwd "success" """{"path":"New.cs"}"""
                Files = Map [ full, "messy" ]
                Diffs = Map [ full, FormatWhole ]
                FormatWhole = fun _ -> "whole-formatted" }
            Logic.hookFormat

    Assert.Equal<(string * string) list>([ full, "whole-formatted" ], writes trace)
    Assert.Contains("reformatted New.cs", stdout trace)
    Assert.DoesNotContain("region", stdout trace)

[<Fact>]
let ``format flow writes nothing and is silent when formatting is a no-op`` () =
    let full = resolved repoCwd "C.cs"

    let trace =
        run
            { world with
                Stdin = payload "edit" repoCwd "success" """{"path":"C.cs"}"""
                Files = Map [ full, "messy" ]
                Diffs = Map [ full, Changed [ { StartLine = 0; EndLineInclusive = 1 } ] ]
                FormatRanges = fun content _ -> content, 0 } // formatter returns input unchanged
            Logic.hookFormat

    Assert.Empty(writes trace)
    Assert.Equal("", stdout trace)

[<Fact>]
let ``format flow is silent for an unsuccessful tool result`` () =
    let trace =
        run
            { world with Stdin = payload "edit" repoCwd "error" """{"path":"C.cs"}""" }
            Logic.hookFormat

    // Decoded, judged not-a-success, stopped before any filesystem effect.
    Assert.Equal<Event list>([ StdinRead ], trace.Events)

// ---- commit-guard flow -----------------------------------------------------------

[<Fact>]
let ``commit-guard denies a commit that adds Copilot as co-author`` () =
    let stdin =
        payload "powershell" "/tmp" "success"
            """{"command":"git commit -m \"Fix\n\nCo-authored-by: Copilot App <1+Copilot@users.noreply.github.com>\""}"""

    let trace = run { world with Stdin = stdin } Logic.commitGuard

    // The transcript: read stdin, then write exactly one deny decision — no filesystem.
    match trace.Events with
    | [ StdinRead; StdoutWritten decision ] ->
        Assert.Contains("\"permissionDecision\"", decision)
        Assert.Contains("deny", decision)
        Assert.Contains("co-author", decision)
    | other -> Assert.Fail(sprintf "unexpected transcript: %A" other)

    Assert.Empty(writes trace)

[<Fact>]
let ``commit-guard stays silent for a commit without the trailer`` () =
    let stdin = payload "powershell" "/tmp" "success" """{"command":"git commit -m \"Fix bug\""}"""
    let trace = run { world with Stdin = stdin } Logic.commitGuard

    // Read stdin, emit nothing — the command is allowed to proceed.
    Assert.Equal<Event list>([ StdinRead ], trace.Events)
