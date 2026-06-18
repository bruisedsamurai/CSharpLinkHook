module RoslynLspHook.Tests.PayloadTests

open Xunit
open RoslynLspHook.Common
open RoslynLspHook.Payload

let private jsonStr (s: string) : string =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Real Copilot CLI shape: `toolArgs` arrives as a JSON-encoded *string*.
let private payloadStringArgs (cwd: string) (resultType: string) (relPath: string) : string =
    let innerArgs = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"toolName":"edit","toolResult":{"resultType":%s},"toolArgs":%s}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr innerArgs)

/// Spec-typed shape: `toolArgs` is a nested object.
let private payloadObjectArgs (cwd: string) (resultType: string) (relPath: string) : string =
    sprintf
        """{"cwd":%s,"toolName":"edit","toolResult":{"resultType":%s},"toolArgs":{"path":%s}}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr relPath)

/// VS Code compatible shape: snake_case keys, `tool_input` as a JSON string.
let private payloadSnakeCase (cwd: string) (resultType: string) (relPath: string) : string =
    let innerArgs = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"tool_name":"edit","tool_result":{"result_type":%s},"tool_input":%s}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr innerArgs)

let private fileOf (p: Parsed) : string option =
    match p with
    | DoToolUse(_, f, _) -> f
    | _ -> None

[<Fact>]
let ``sessionStart is inferred from the source field`` () =
    let input = """{"cwd":"/work","source":"startup"}"""

    match parse None input with
    | DoSessionStart cwd -> Assert.Equal("/work", cwd)
    | other -> Assert.Fail(sprintf "expected DoSessionStart, got %A" other)

[<Fact>]
let ``sessionStart is recognized from hook_event_name`` () =
    let input = """{"cwd":"/work","hook_event_name":"SessionStart"}"""

    match parse None input with
    | DoSessionStart _ -> ()
    | other -> Assert.Fail(sprintf "expected DoSessionStart, got %A" other)

[<Fact>]
let ``an explicit hint overrides payload inference`` () =
    // Payload looks like a tool use, but the hint says sessionStart.
    let input = """{"cwd":"/work","toolArgs":{"path":"A.cs"},"toolResult":{"resultType":"success"}}"""

    match parse (Some SessionStart) input with
    | DoSessionStart _ -> ()
    | other -> Assert.Fail(sprintf "expected DoSessionStart, got %A" other)

[<Fact>]
let ``postToolUse extracts the path from a string-encoded toolArgs`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work" "success" "src/A.cs")

    match fileOf parsed with
    | Some f -> Assert.EndsWith("A.cs", f)
    | None -> Assert.Fail "expected a resolved C# file"

[<Fact>]
let ``postToolUse extracts the path from an object toolArgs`` () =
    let parsed = parse (Some PostToolUse) (payloadObjectArgs "/work" "success" "src/A.cs")
    Assert.True((fileOf parsed).IsSome)

[<Fact>]
let ``postToolUse extracts the path from a VS Code snake_case payload`` () =
    let parsed = parse (Some PostToolUse) (payloadSnakeCase "/work" "success" "A.cs")
    Assert.True((fileOf parsed).IsSome)

[<Fact>]
let ``the resolved path is absolute and rooted at cwd`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work/proj" "success" "src/A.cs")

    match fileOf parsed with
    | Some f ->
        Assert.True(System.IO.Path.IsPathRooted f)
        Assert.Contains("proj", f)
    | None -> Assert.Fail "expected a resolved C# file"

[<Fact>]
let ``a failed tool yields no file to check`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work" "error" "A.cs")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``non-C# files are ignored`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work" "success" "notes.txt")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``generated C# files are ignored`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work" "success" "Form.Designer.cs")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``files under obj are ignored`` () =
    let parsed = parse (Some PostToolUse) (payloadStringArgs "/work" "success" "obj/Debug/A.cs")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``malformed JSON is ignored`` () =
    Assert.Equal(Ignore, parse None "this is not json")

[<Fact>]
let ``a non-object payload is ignored`` () =
    Assert.Equal(Ignore, parse None "[1,2,3]")

[<Fact>]
let ``unrelated events are ignored`` () =
    let input = """{"cwd":"/work","hook_event_name":"PreCompact"}"""
    Assert.Equal(Ignore, parse None input)

[<Fact>]
let ``isLintableCSharp accepts a plain .cs file`` () =
    Assert.True(isLintableCSharp "/work/src/Thing.cs")

[<Fact>]
let ``isLintableCSharp rejects a bin path`` () =
    Assert.False(isLintableCSharp "/work/bin/Release/Thing.cs")
