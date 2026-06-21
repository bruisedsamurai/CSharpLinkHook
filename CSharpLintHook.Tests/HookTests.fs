module CSharpLintHook.Tests.HookTests

open System
open System.IO
open Xunit
open CSharpLintHook
open CSharpLintHook.Tests.TestHelpers

/// Minimal JSON string escaping for embedding filesystem paths in a payload.
let private jsonStr (s: string) : string =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Real Copilot CLI shape: `toolArgs` arrives as a JSON-encoded *string*.
let private payload (cwd: string) (resultType: string) (relPath: string) : string =
    let innerArgs = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"toolName":"create","toolResult":{"resultType":%s},"toolArgs":%s}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr innerArgs)

/// Spec-typed shape: `toolArgs` is a nested object.
let private payloadObjectArgs (cwd: string) (resultType: string) (relPath: string) : string =
    sprintf
        """{"cwd":%s,"toolName":"create","toolResult":{"resultType":%s},"toolArgs":{"path":%s}}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr relPath)

/// VS Code compatible shape: snake_case keys, `tool_input` as a JSON string.
let private payloadSnakeCase (cwd: string) (resultType: string) (relPath: string) : string =
    let innerArgs = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"tool_name":"create","tool_result":{"result_type":%s},"tool_input":%s}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr innerArgs)

/// Run the format flow with `stdin` on Console.In, capturing Console.Out. Console is
/// process-global, so these tests share one class (xUnit runs facts in a class
/// sequentially) and always restore the original streams.
let private runFormatHook (stdin: string) : string =
    let originalIn = Console.In
    let originalOut = Console.Out
    use reader = new StringReader(stdin)
    use writer = new StringWriter()

    try
        Console.SetIn reader
        Console.SetOut writer
        Interpreter.run Logic.hookFormat
        writer.ToString()
    finally
        Console.SetIn originalIn
        Console.SetOut originalOut

/// Run the read flow with `stdin` on Console.In, capturing stdout.
let private runReadHook (stdin: string) : string =
    let originalIn = Console.In
    let originalOut = Console.Out
    use reader = new StringReader(stdin)
    use writer = new StringWriter()

    try
        Console.SetIn reader
        Console.SetOut writer
        Interpreter.run Logic.hookRead
        writer.ToString()
    finally
        Console.SetIn originalIn
        Console.SetOut originalOut

/// A read-tool payload (e.g. `view`) whose toolArgs names `relPath`.
let private readToolPayload (cwd: string) (resultType: string) (relPath: string) : string =
    let innerArgs = sprintf """{"path":%s}""" (jsonStr relPath)

    sprintf
        """{"cwd":%s,"toolName":"view","toolResult":{"resultType":%s},"toolArgs":%s}"""
        (jsonStr cwd)
        (jsonStr resultType)
        (jsonStr innerArgs)

/// A read/search/shell payload whose toolArgs is the given inner JSON object, delivered
/// as a JSON-encoded string (the production shape). Lets a test put the `.cs` name
/// wherever the tool would — a `command`, a `paths` array — not just a path key.
let private readPayloadWithArgs (toolName: string) (resultType: string) (innerArgs: string) : string =
    sprintf
        """{"cwd":"/tmp","toolName":%s,"toolResult":{"resultType":%s},"toolArgs":%s}"""
        (jsonStr toolName)
        (jsonStr resultType)
        (jsonStr innerArgs)

[<Fact>]
let ``format flow formats the changed region in place and emits additionalContext`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runFormatHook (payload repo.Dir "success" "C.cs")

    // stdout is the hook response carrying additionalContext.
    Assert.Contains("\"additionalContext\"", stdout)
    Assert.Contains("changed region(s) in C.cs", stdout)

    // On disk: method A formatted, method B still messy.
    let onDisk = repo.Read "C.cs"
    Assert.Contains("int x = 1;", onDisk)
    Assert.Contains("int z = 3;", onDisk)
    Assert.Contains("int y=2;", onDisk)

[<Fact>]
let ``format flow formats from an object-shaped toolArgs`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runFormatHook (payloadObjectArgs repo.Dir "success" "C.cs")

    Assert.Contains("changed region(s) in C.cs", stdout)
    Assert.Contains("int x = 1;", repo.Read "C.cs")

[<Fact>]
let ``format flow formats from a VS Code snake_case payload`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runFormatHook (payloadSnakeCase repo.Dir "success" "C.cs")

    Assert.Contains("changed region(s) in C.cs", stdout)
    Assert.Contains("int x = 1;", repo.Read "C.cs")

[<Fact>]
let ``format flow reformats an untracked file whole and says so`` () =
    use repo = new ScratchRepo()
    repo.Write "Seed.cs" "class S { }\n"
    repo.Commit "seed"
    repo.Write "New.cs" (messyTwoMethodClass "")

    let stdout = runFormatHook (payload repo.Dir "success" "New.cs")

    Assert.Contains("reformatted New.cs", stdout)
    Assert.DoesNotContain("region", stdout)
    Assert.Contains("int y = 2;", repo.Read "New.cs") // whole-file: B formatted too

[<Fact>]
let ``format flow is a no-op when the tool result was not a success`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runFormatHook (payload repo.Dir "error" "C.cs")

    Assert.Equal("", stdout)
    // File left exactly as written (unformatted).
    Assert.Contains("int x=1;int z=3;", repo.Read "C.cs")

[<Fact>]
let ``format flow ignores unsupported files`` () =
    use repo = new ScratchRepo()
    repo.Write "notes.txt" "hello"
    repo.Commit "baseline"
    repo.Write "notes.txt" "hello world"

    let stdout = runFormatHook (payload repo.Dir "success" "notes.txt")
    Assert.Equal("", stdout)

[<Fact>]
let ``format flow swallows malformed payloads without output`` () =
    let stdout = runFormatHook "this is not json"
    Assert.Equal("", stdout)

[<Fact>]
let ``format flow emits nothing when the changed file needs no formatting`` () =
    use repo = new ScratchRepo()
    // Already-clean file; a whitespace-free change keeps it canonical.
    repo.Write "C.cs" "class C\n{\n    void A()\n    {\n        int x = 1;\n    }\n}\n"
    repo.Commit "baseline"
    repo.Write "C.cs" "class C\n{\n    void A()\n    {\n        int x = 2;\n    }\n}\n"

    let stdout = runFormatHook (payload repo.Dir "success" "C.cs")
    Assert.Equal("", stdout)

[<Fact>]
let ``read flow emits additionalContext naming the RoslynLspMcp methods for a .cs file`` () =
    // A read/search/shell tool that touched a C# file: emit the note pointing the model
    // at the RoslynLspMcp MCP methods. No formatting happens here.
    let stdout = runReadHook (readToolPayload "/tmp" "success" "C.cs")

    Assert.Contains("\"additionalContext\"", stdout)
    Assert.Contains("get_class_constructors_and_properties", stdout)
    Assert.Contains("get_class_methods", stdout)
    Assert.Contains("get_namespace_declarations", stdout)
    Assert.Contains("symbols", stdout)

[<Fact>]
let ``read flow emits additionalContext naming the RoslynLspMcp methods for a .fs file`` () =
    let stdout = runReadHook (readToolPayload "/tmp" "success" "Script.fs")

    Assert.Contains("\"additionalContext\"", stdout)
    Assert.Contains("get_class_methods", stdout)

[<Fact>]
let ``read flow is a no-op when the tool did not touch a supported source file`` () =
    let stdout = runReadHook (readToolPayload "/tmp" "success" "notes.txt")
    Assert.Equal("", stdout)

[<Fact>]
let ``read flow is a no-op when the tool result was not a success`` () =
    let stdout = runReadHook (readToolPayload "/tmp" "error" "C.cs")
    Assert.Equal("", stdout)

[<Fact>]
let ``read flow swallows malformed payloads without output`` () =
    let stdout = runReadHook "this is not json"
    Assert.Equal("", stdout)

[<Fact>]
let ``read flow fires for a bash command that names a .cs file`` () =
    // bash/powershell put the filename inside a `command` string, not a path key — the
    // pathKeys-only read flow would miss it; the toolArgs scan catches it.
    let stdout =
        runReadHook (readPayloadWithArgs "bash" "success" """{"command":"dotnet build src/Foo.cs"}""")

    Assert.Contains("get_class_methods", stdout)

[<Fact>]
let ``read flow fires for a powershell command that names a .cs file`` () =
    let stdout =
        runReadHook (readPayloadWithArgs "powershell" "success" """{"command":"type Bar.cs"}""")

    Assert.Contains("get_namespace_declarations", stdout)

[<Fact>]
let ``read flow fires for a grep over a .cs path`` () =
    let stdout =
        runReadHook (readPayloadWithArgs "grep" "success" """{"pattern":"class","paths":["src/Foo.cs"]}""")

    Assert.Contains("\"additionalContext\"", stdout)

[<Fact>]
let ``read flow ignores a bash command that names only a .csproj`` () =
    let stdout =
        runReadHook (readPayloadWithArgs "bash" "success" """{"command":"dotnet build App.csproj"}""")

    Assert.Equal("", stdout)
