module CSharpLintHook.Tests.PayloadTests

open Xunit
open CSharpLintHook
open CSharpLintHook.Common

[<Fact>]
let ``parse extracts cwd, tool, success and file path`` () =
    let json =
        """{"cwd":"/work","toolName":"editor","toolResult":{"resultType":"success"},"toolArgs":{"path":"src/A.cs"}}"""

    match Payload.PostToolUse.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info ->
        Assert.Equal("/work", info.Cwd)
        Assert.Equal("editor", info.ToolName)
        Assert.True(Payload.PostToolUse.isSuccess info)
        Assert.Equal(Some "src/A.cs", Payload.PostToolUse.filePath info)

[<Fact>]
let ``parse extracts the file path when toolArgs is a JSON-encoded string`` () =
    // The real Copilot CLI postToolUse payload delivers toolArgs pre-serialized
    // as a JSON string, not a nested object. This is the production shape.
    let json =
        """{"cwd":"/work","toolName":"create","toolResult":{"resultType":"success"},"toolArgs":"{\"path\":\"src/A.cs\",\"file_text\":\"class A{}\"}"}"""

    match Payload.PostToolUse.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info ->
        Assert.True(Payload.PostToolUse.isSuccess info)
        Assert.Equal(Some "src/A.cs", Payload.PostToolUse.filePath info)

[<Fact>]
let ``parse accepts the VS Code snake_case payload with a string tool_input`` () =
    let json =
        """{"cwd":"/work","tool_name":"create","tool_result":{"result_type":"success"},"tool_input":"{\"path\":\"src/A.cs\"}"}"""

    match Payload.PostToolUse.decode json with
    | None -> failwith "expected a parsed payload"
    | Some info ->
        Assert.Equal("create", info.ToolName)
        Assert.True(Payload.PostToolUse.isSuccess info)
        Assert.Equal(Some "src/A.cs", Payload.PostToolUse.filePath info)

[<Fact>]
let ``parse treats non-success resultType as failure`` () =
    let json =
        """{"cwd":"/w","toolResult":{"resultType":"error"},"toolArgs":{"path":"A.cs"}}"""

    match Payload.PostToolUse.decode json with
    | Some info -> Assert.False(Payload.PostToolUse.isSuccess info)
    | None -> failwith "expected a parsed payload"

[<Theory>]
[<InlineData("path")>]
[<InlineData("file_path")>]
[<InlineData("filePath")>]
[<InlineData("filename")>]
[<InlineData("file")>]
[<InlineData("target_file")>]
let ``parse recognises every supported path key`` (key: string) =
    let json =
        $"""{{"toolResult":{{"resultType":"success"}},"toolArgs":{{"%s{key}":"X.cs"}}}}"""

    match Payload.PostToolUse.decode json with
    | Some info -> Assert.Equal(Some "X.cs", Payload.PostToolUse.filePath info)
    | None -> failwith "expected a parsed payload"

[<Theory>]
[<InlineData("path")>]
[<InlineData("file_path")>]
[<InlineData("filePath")>]
[<InlineData("filename")>]
[<InlineData("file")>]
[<InlineData("target_file")>]
let ``parse recognises every supported path key inside a string toolArgs`` (key: string) =
    let json =
        $"""{{"toolResult":{{"resultType":"success"}},"toolArgs":"{{\"%s{key}\":\"X.cs\"}}"}}"""

    match Payload.PostToolUse.decode json with
    | Some info -> Assert.Equal(Some "X.cs", Payload.PostToolUse.filePath info)
    | None -> failwith "expected a parsed payload"

[<Fact>]
let ``parse returns None for malformed json`` () =
    Assert.Equal(None, Payload.PostToolUse.decode "this is not json")

[<Fact>]
let ``parse returns None for a non-object root`` () =
    Assert.Equal(None, Payload.PostToolUse.decode "[1,2,3]")

[<Fact>]
let ``parse yields no file path when toolArgs lacks a known key`` () =
    let json =
        """{"toolResult":{"resultType":"success"},"toolArgs":{"unrelated":"x"}}"""

    match Payload.PostToolUse.decode json with
    | Some info -> Assert.Equal(None, Payload.PostToolUse.filePath info)
    | None -> failwith "expected a parsed payload"

[<Theory>]
[<InlineData("/repo/src/Foo.cs", true)>]
[<InlineData("/repo/src/Foo.CS", true)>]
[<InlineData("/repo/src/Foo.csx", true)>]
[<InlineData("/repo/src/Foo.fs", false)>]
[<InlineData("/repo/src/Foo.FSI", false)>]
[<InlineData("/repo/src/Foo.fsx", false)>]
[<InlineData("/repo/notes.txt", false)>]
[<InlineData("/repo/README.md", false)>]
[<InlineData("/repo/App.fsproj", false)>]
[<InlineData("/repo/Foo.g.cs", false)>]
[<InlineData("/repo/Foo.designer.cs", false)>]
[<InlineData("/repo/Foo.generated.cs", false)>]
[<InlineData("/repo/Foo.g.fs", false)>]
[<InlineData("/repo/Foo.generated.fs", false)>]
[<InlineData("/repo/obj/Debug/Foo.cs", false)>]
[<InlineData("/repo/obj/Debug/Foo.fs", false)>]
[<InlineData("/repo/bin/Release/Foo.cs", false)>]
let ``isFormattableCSharp guards source extensions, generated files and build dirs`` (path: string) (expected: bool) =
    Assert.Equal(expected, Payload.isFormattableCSharp path)

[<Fact>]
let ``buildHookOutput is valid json carrying additionalContext`` () =
    let r =
        { Path = "/repo/A.cs"
          Found = true
          Original = "x"
          Formatted = "y"
          Regions = 2
          WholeFile = false }

    let ctx = Payload.buildAdditionalContext r
    let out = Payload.buildHookOutput ctx

    Assert.Contains("\"additionalContext\"", out)
    Assert.Contains("2 changed region(s) in A.cs", out)

[<Fact>]
let ``buildAdditionalContext distinguishes whole-file from regions`` () =
    let whole =
        { Path = "/r/A.cs"
          Found = true
          Original = "x"
          Formatted = "y"
          Regions = 0
          WholeFile = true }

    Assert.Contains("reformatted A.cs", Payload.buildAdditionalContext whole)
    Assert.DoesNotContain("region", Payload.buildAdditionalContext whole)

[<Fact>]
let ``decode captures the textual toolArgs for object-shaped args`` () =
    let json =
        """{"toolResult":{"resultType":"success"},"toolArgs":{"command":"dotnet build src/A.cs"}}"""

    match Payload.PostToolUse.decode json with
    | Some info ->
        Assert.True info.ToolArgs.IsSome
        Assert.Contains("src/A.cs", info.ToolArgs.Value)
    | None -> failwith "expected a parsed payload"

[<Fact>]
let ``decode captures the textual toolArgs for string-encoded args`` () =
    let json =
        """{"toolResult":{"resultType":"success"},"toolArgs":"{\"command\":\"type Bar.cs\"}"}"""

    match Payload.PostToolUse.decode json with
    | Some info ->
        Assert.True info.ToolArgs.IsSome
        Assert.Contains("Bar.cs", info.ToolArgs.Value)
    | None -> failwith "expected a parsed payload"

[<Theory>]
[<InlineData("dotnet build src/Foo.cs", true)>]
[<InlineData("dotnet script scripts/Foo.csx", true)>]
[<InlineData("dotnet build src/Foo.fs", false)>]
[<InlineData("dotnet fsi scripts/Foo.fsx", false)>]
[<InlineData("type C:\\src\\Foo.fsi", false)>]
[<InlineData("type C:\\src\\Foo.cs", true)>]
[<InlineData("grep class ./A.cs ./B.cs", true)>]
[<InlineData("dotnet build App.csproj", false)>]
[<InlineData("dotnet build App.fsproj", false)>]
[<InlineData("link styles.css", false)>]
[<InlineData("render View.cshtml", false)>]
[<InlineData("cat README.md", false)>]
[<InlineData("echo hello world", false)>]
[<InlineData("cat obj/Debug/Gen.cs", false)>]
[<InlineData("cat obj/Debug/Gen.fs", false)>]
[<InlineData("edit Foo.g.cs", false)>]
let ``referencesCSharpFile spots a supported source filename anywhere in the args text`` (text: string) (expected: bool) =
    Assert.Equal(expected, Payload.referencesCSharpFile text)
