module RoslynLspHook.Tests.StateMachineTests

open Xunit
open RoslynLspHook.Common
open RoslynLspHook.Tests.StubInterpreter

let private cfgFor (cwd: string) : LspConfig =
    { PipeName = "test-pipe"
      Cwd = cwd
      Command = [ "roslyn-language-server" ]
      WaitMs = 1000 }

let private jsonStr (s: string) : string =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

let private toolUsePayload (cwd: string) (relPath: string) : string =
    let inner = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"toolName":"edit","toolResult":{"resultType":"success"},"toolArgs":%s}"""
        (jsonStr cwd)
        (jsonStr (inner))

let private sessionStartPayload (cwd: string) : string =
    sprintf """{"cwd":%s,"source":"startup"}""" (jsonStr cwd)

let private oneError: Diagnostic list =
    [ { Severity = Error
        Line = 2
        Character = 4
        Message = "missing semicolon"
        Code = Some "CS1002"
        Source = Some "Roslyn" } ]

let private hook (env: StubEnv) =
    run env (RoslynLspHook.Logic.hook None cfgFor)

// --- sessionStart -----------------------------------------------------------

[<Fact>]
let ``sessionStart checks the cwd then launches the server in the background`` () =
    let env = { defaultEnv with Stdin = sessionStartPayload "/work" }
    let (_, log) = hook env

    Assert.True(has "direxists:" log)
    Assert.True(has "launch:" log)
    // The LSP launch happens; we never probe or fetch on sessionStart.
    Assert.False(has "fetch:" log)
    Assert.False(has "stdout:" log)

[<Fact>]
let ``sessionStart with a missing cwd does not launch`` () =
    let env =
        { defaultEnv with
            Stdin = sessionStartPayload "/nope"
            DirExistsResult = false }

    let (_, log) = hook env
    Assert.True(has "direxists:" log)
    Assert.False(has "launch:" log)

// --- postToolUse ------------------------------------------------------------

[<Fact>]
let ``postToolUse probes the pipe, fetches diagnostics, and emits context`` () =
    let env =
        { defaultEnv with
            Stdin = toolUsePayload "/work" "A.cs"
            ProbeResult = true
            Diagnostics = oneError }

    let (_, log) = hook env

    Assert.True(has "probe:" log)
    Assert.True(has "fetch:" log)

    match value "stdout:" log with
    | Some out ->
        Assert.Contains("additionalContext", out)
        Assert.Contains("missing semicolon", out)
        Assert.Contains("CS1002", out)
    | None -> Assert.Fail "expected the hook to emit additionalContext"

[<Fact>]
let ``postToolUse exits quietly when the pipe is not open`` () =
    let env =
        { defaultEnv with
            Stdin = toolUsePayload "/work" "A.cs"
            ProbeResult = false
            Diagnostics = oneError }

    let (_, log) = hook env

    Assert.True(has "probe:" log)
    // Pipe closed: we must not fetch or emit anything.
    Assert.False(has "fetch:" log)
    Assert.False(has "stdout:" log)

[<Fact>]
let ``postToolUse stays silent when the file is clean`` () =
    let env =
        { defaultEnv with
            Stdin = toolUsePayload "/work" "A.cs"
            ProbeResult = true
            Diagnostics = [] }

    let (_, log) = hook env

    Assert.True(has "fetch:" log)
    Assert.False(has "stdout:" log)

[<Fact>]
let ``postToolUse on a non-C# file never touches the server`` () =
    let env =
        { defaultEnv with
            Stdin = toolUsePayload "/work" "notes.txt"
            ProbeResult = true
            Diagnostics = oneError }

    let (_, log) = hook env

    Assert.False(has "probe:" log)
    Assert.False(has "fetch:" log)
    Assert.False(has "stdout:" log)

[<Fact>]
let ``a malformed payload only reads stdin`` () =
    let env = { defaultEnv with Stdin = "not json" }
    let (_, log) = hook env
    Assert.Equal<string list>([ "read" ], log)

// --- the StartLsp -> CheckFile branch (spec fidelity) -----------------------

[<Fact>]
let ``StartingLsp with a pending file launches then checks that file`` () =
    // Directly drive the StartLsp state carrying a file: the spec says it should
    // start the server and then continue to CheckFile.
    let program = RoslynLspHook.Logic.drive (cfgFor "/work") (StartingLsp(Some "/work/A.cs"))

    let env =
        { defaultEnv with
            ProbeResult = true
            Diagnostics = oneError }

    let (_, log) = run env program

    Assert.True(has "launch:" log)
    Assert.True(has "fetch:" log)
    Assert.True(has "stdout:" log)
