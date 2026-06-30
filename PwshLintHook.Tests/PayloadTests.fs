module PwshLintHook.Tests.PayloadTests

open Xunit
open PwshLintHook

[<Fact>]
let ``decode extracts toolName and command from a camelCase object payload`` () =
    let json =
        """{"cwd":"C:\\repo","toolName":"powershell","toolArgs":{"command":"Get-ChildItem","description":"list"}}"""

    match Payload.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info ->
        Assert.Equal("powershell", info.ToolName)
        Assert.Equal(Some "Get-ChildItem", info.Command)

[<Fact>]
let ``decode extracts the command when toolArgs is a JSON-encoded string`` () =
    // The real Copilot CLI delivers toolArgs pre-serialized as a JSON string.
    let json =
        """{"cwd":"C:\\repo","toolName":"powershell","toolArgs":"{\"command\":\"Get-ChildItem -Recurse\"}"}"""

    match Payload.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info -> Assert.Equal(Some "Get-ChildItem -Recurse", info.Command)

[<Fact>]
let ``decode accepts the VS Code snake_case payload`` () =
    let json =
        """{"cwd":"C:\\repo","tool_name":"powershell","tool_input":{"command":"Get-Content a.txt"}}"""

    match Payload.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info ->
        Assert.Equal("powershell", info.ToolName)
        Assert.Equal(Some "Get-Content a.txt", info.Command)

[<Fact>]
let ``decode yields no command when tool-args carry none`` () =
    match Payload.decode """{"toolName":"powershell","toolArgs":{"description":"x"}}""" with
    | None -> failwith "expected a parsed payload"
    | Some info -> Assert.Equal(None, info.Command)

[<Fact>]
let ``decode returns None for malformed json`` () =
    Assert.Equal(None, Payload.decode "not json")

[<Theory>]
[<InlineData("powershell", true)>]
[<InlineData("bash", false)>]
[<InlineData("view", false)>]
let ``isShellTool only matches powershell`` (tool: string, expected: bool) =
    Assert.Equal(expected, Payload.isShellTool tool)

[<Fact>]
let ``buildDenyOutput emits a deny decision with the reason`` () =
    let json = Payload.buildDenyOutput "use fd"
    Assert.Contains("\"permissionDecision\":\"deny\"", json)
    Assert.Contains("use fd", json)
