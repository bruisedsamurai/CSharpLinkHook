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

/// Run the real hook program with `stdin` on Console.In, capturing Console.Out.
/// Console is process-global, so these tests share one class (xUnit runs facts
/// in a class sequentially) and always restore the original streams.
let private runHook (stdin: string) : string =
    let originalIn = Console.In
    let originalOut = Console.Out
    use reader = new StringReader(stdin)
    use writer = new StringWriter()

    try
        Console.SetIn reader
        Console.SetOut writer
        Interpreter.run Logic.hook
        writer.ToString()
    finally
        Console.SetIn originalIn
        Console.SetOut originalOut

[<Fact>]
let ``hook formats the changed region in place and emits additionalContext`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runHook (payload repo.Dir "success" "C.cs")

    // stdout is the hook response carrying additionalContext.
    Assert.Contains("\"additionalContext\"", stdout)
    Assert.Contains("changed region(s) in C.cs", stdout)

    // On disk: method A formatted, method B still messy.
    let onDisk = repo.Read "C.cs"
    Assert.Contains("int x = 1;", onDisk)
    Assert.Contains("int z = 3;", onDisk)
    Assert.Contains("int y=2;", onDisk)

[<Fact>]
let ``hook formats from an object-shaped toolArgs`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runHook (payloadObjectArgs repo.Dir "success" "C.cs")

    Assert.Contains("changed region(s) in C.cs", stdout)
    Assert.Contains("int x = 1;", repo.Read "C.cs")

[<Fact>]
let ``hook formats from a VS Code snake_case payload`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runHook (payloadSnakeCase repo.Dir "success" "C.cs")

    Assert.Contains("changed region(s) in C.cs", stdout)
    Assert.Contains("int x = 1;", repo.Read "C.cs")

[<Fact>]
let ``hook reformats an untracked file whole and says so`` () =
    use repo = new ScratchRepo()
    repo.Write "Seed.cs" "class S { }\n"
    repo.Commit "seed"
    repo.Write "New.cs" (messyTwoMethodClass "")

    let stdout = runHook (payload repo.Dir "success" "New.cs")

    Assert.Contains("reformatted New.cs", stdout)
    Assert.DoesNotContain("region", stdout)
    Assert.Contains("int y = 2;", repo.Read "New.cs") // whole-file: B formatted too

[<Fact>]
let ``hook is a no-op when the tool result was not a success`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let stdout = runHook (payload repo.Dir "error" "C.cs")

    Assert.Equal("", stdout)
    // File left exactly as written (unformatted).
    Assert.Contains("int x=1;int z=3;", repo.Read "C.cs")

[<Fact>]
let ``hook ignores non-C# files`` () =
    use repo = new ScratchRepo()
    repo.Write "notes.txt" "hello"
    repo.Commit "baseline"
    repo.Write "notes.txt" "hello world"

    let stdout = runHook (payload repo.Dir "success" "notes.txt")
    Assert.Equal("", stdout)

[<Fact>]
let ``hook swallows malformed payloads without output`` () =
    let stdout = runHook "this is not json"
    Assert.Equal("", stdout)

[<Fact>]
let ``hook emits nothing when the changed file needs no formatting`` () =
    use repo = new ScratchRepo()
    // Already-clean file; a whitespace-free change keeps it canonical.
    repo.Write "C.cs" "class C\n{\n    void A()\n    {\n        int x = 1;\n    }\n}\n"
    repo.Commit "baseline"
    repo.Write "C.cs" "class C\n{\n    void A()\n    {\n        int x = 2;\n    }\n}\n"

    let stdout = runHook (payload repo.Dir "success" "C.cs")
    Assert.Equal("", stdout)
