module RoslynLspHook.Tests.PayloadTests

open Xunit
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

// --- event classification (the payload's shape selects the event) -----------

[<Fact>]
let ``sessionStart is recognized from a camelCase payload`` () =
    let input = """{"cwd":"/work","source":"startup"}"""

    match parse input with
    | DoSessionStart cwd -> Assert.Equal("/work", cwd)
    | other -> Assert.Fail(sprintf "expected DoSessionStart, got %A" other)

[<Fact>]
let ``sessionStart is recognized from a VS Code payload`` () =
    let input = """{"cwd":"/work","hook_event_name":"SessionStart","source":"resume"}"""

    match parse input with
    | DoSessionStart _ -> ()
    | other -> Assert.Fail(sprintf "expected DoSessionStart, got %A" other)

[<Fact>]
let ``postToolUse is recognized from its tool fields`` () =
    let input = """{"cwd":"/work","toolArgs":{"path":"A.cs"},"toolResult":{"resultType":"success"}}"""

    match parse input with
    | DoToolUse(cwd, _, _) -> Assert.Equal("/work", cwd)
    | other -> Assert.Fail(sprintf "expected DoToolUse, got %A" other)

// --- the postToolUse file path (camelCase, object, and VS Code shapes) -------

[<Fact>]
let ``postToolUse extracts the path from a string-encoded toolArgs`` () =
    let parsed = parse (payloadStringArgs "/work" "success" "src/A.cs")

    match fileOf parsed with
    | Some f -> Assert.EndsWith("A.cs", f)
    | None -> Assert.Fail "expected a resolved C# file"

[<Fact>]
let ``postToolUse extracts the path from an object toolArgs`` () =
    let parsed = parse (payloadObjectArgs "/work" "success" "src/A.cs")
    Assert.True((fileOf parsed).IsSome)

[<Fact>]
let ``postToolUse extracts the path from a VS Code snake_case payload`` () =
    let parsed = parse (payloadSnakeCase "/work" "success" "A.cs")
    Assert.True((fileOf parsed).IsSome)

[<Fact>]
let ``the resolved path is absolute and rooted at cwd`` () =
    let parsed = parse (payloadStringArgs "/work/proj" "success" "src/A.cs")

    match fileOf parsed with
    | Some f ->
        Assert.True(System.IO.Path.IsPathRooted f)
        Assert.Contains("proj", f)
    | None -> Assert.Fail "expected a resolved C# file"

[<Fact>]
let ``a failed tool yields no file to check`` () =
    let parsed = parse (payloadStringArgs "/work" "error" "A.cs")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``non-C# files are ignored`` () =
    let parsed = parse (payloadStringArgs "/work" "success" "notes.txt")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``generated C# files are ignored`` () =
    let parsed = parse (payloadStringArgs "/work" "success" "Form.Designer.cs")
    Assert.Equal(None, fileOf parsed)

[<Fact>]
let ``files under obj are ignored`` () =
    let parsed = parse (payloadStringArgs "/work" "success" "obj/Debug/A.cs")
    Assert.Equal(None, fileOf parsed)

// --- payloads we ignore ------------------------------------------------------

[<Fact>]
let ``malformed JSON is ignored`` () =
    Assert.Equal(Ignore, parse "this is not json")

[<Fact>]
let ``a non-object payload is ignored`` () =
    Assert.Equal(Ignore, parse "[1,2,3]")

[<Fact>]
let ``an event with neither source nor tool fields is ignored`` () =
    let input = """{"cwd":"/work","hook_event_name":"PreCompact"}"""
    Assert.Equal(Ignore, parse input)

// --- the lintability filter --------------------------------------------------

[<Fact>]
let ``isLintableCSharp accepts a plain .cs file`` () =
    Assert.True(isLintableCSharp "/work/src/Thing.cs")

[<Fact>]
let ``isLintableCSharp rejects a bin path`` () =
    Assert.False(isLintableCSharp "/work/bin/Release/Thing.cs")
