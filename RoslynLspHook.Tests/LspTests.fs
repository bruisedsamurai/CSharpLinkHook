module RoslynLspHook.Tests.LspTests

open System.Text
open System.Text.Json
open Xunit
open RoslynLspHook.Common
open RoslynLspHook.Lsp

// --- severity + uri ---------------------------------------------------------

[<Theory>]
[<InlineData(1, "error")>]
[<InlineData(2, "warning")>]
[<InlineData(3, "info")>]
[<InlineData(4, "hint")>]
[<InlineData(99, "info")>]
let ``severityOfInt maps the LSP numeric severities`` (n: int) (expected: string) =
    let word =
        match severityOfInt n with
        | Error -> "error"
        | Warning -> "warning"
        | Information -> "info"
        | Hint -> "hint"

    Assert.Equal(expected, word)

[<Fact>]
let ``fileUri produces a file scheme URI`` () =
    let uri = fileUri "/work/src/A.cs"
    Assert.StartsWith("file://", uri)
    Assert.EndsWith("A.cs", uri)

// --- framing ----------------------------------------------------------------

[<Fact>]
let ``frame prefixes a Content-Length header and the body follows`` () =
    let bytes = frame "{}"
    let text = Encoding.UTF8.GetString bytes
    Assert.Contains("Content-Length: 2\r\n\r\n", text)
    Assert.EndsWith("{}", text)

[<Fact>]
let ``frame counts UTF-8 bytes, not characters`` () =
    // "é" is two UTF-8 bytes, so the body length is 3 for {é}-less payload "é".
    let bytes = frame "é"
    let text = Encoding.ASCII.GetString(bytes[.. (Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n").Length - 1)])
    Assert.Equal("Content-Length: 2\r\n\r\n", text)

[<Fact>]
let ``parseContentLength reads the length from a header block`` () =
    Assert.Equal(Some 42, parseContentLength "Content-Length: 42\r\n\r\n")

[<Fact>]
let ``parseContentLength is case-insensitive and tolerates extra headers`` () =
    let headers = "Content-Type: application/json\r\ncontent-length: 7\r\n\r\n"
    Assert.Equal(Some 7, parseContentLength headers)

[<Fact>]
let ``parseContentLength returns None when absent`` () =
    Assert.Equal(None, parseContentLength "X-Foo: bar\r\n\r\n")

// --- outgoing requests ------------------------------------------------------

let private parseDoc (s: string) = JsonDocument.Parse s

[<Fact>]
let ``initializeRequest is valid JSON-RPC for initialize`` () =
    use doc = parseDoc (initializeRequest 1 1234 "/work")
    let root = doc.RootElement
    Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString())
    Assert.Equal(1, root.GetProperty("id").GetInt32())
    Assert.Equal("initialize", root.GetProperty("method").GetString())
    Assert.Equal(1234, root.GetProperty("params").GetProperty("processId").GetInt32())

[<Fact>]
let ``didOpenNotification carries the uri, language and text`` () =
    use doc = parseDoc (didOpenNotification "file:///x/A.cs" "class C {}")
    let td = doc.RootElement.GetProperty("params").GetProperty("textDocument")
    Assert.Equal("file:///x/A.cs", td.GetProperty("uri").GetString())
    Assert.Equal("csharp", td.GetProperty("languageId").GetString())
    Assert.Equal("class C {}", td.GetProperty("text").GetString())

[<Fact>]
let ``diagnosticRequest targets the document by uri`` () =
    use doc = parseDoc (diagnosticRequest 2 "file:///x/A.cs")
    let root = doc.RootElement
    Assert.Equal("textDocument/diagnostic", root.GetProperty("method").GetString())
    Assert.Equal("file:///x/A.cs", root.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString())

[<Fact>]
let ``stubResponse echoes the raw id and result`` () =
    let s = stubResponse "5" "[null,null]"
    use doc = parseDoc s
    Assert.Equal(5, doc.RootElement.GetProperty("id").GetInt32())
    Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("result").ValueKind)

// --- incoming classification ------------------------------------------------

[<Fact>]
let ``classify recognizes a server request (id + method)`` () =
    let msg = """{"jsonrpc":"2.0","id":7,"method":"workspace/configuration","params":{"items":[{}]}}"""

    match classify msg with
    | ServerRequest(idRaw, method, _) ->
        Assert.Equal("7", idRaw)
        Assert.Equal("workspace/configuration", method)
    | other -> Assert.Fail(sprintf "expected ServerRequest, got %A" other)

[<Fact>]
let ``classify recognizes a notification (method, no id)`` () =
    let msg = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{}}"""

    match classify msg with
    | Notification(method, _) -> Assert.Equal("textDocument/publishDiagnostics", method)
    | other -> Assert.Fail(sprintf "expected Notification, got %A" other)

[<Fact>]
let ``classify recognizes a response (id, no method)`` () =
    match classify """{"jsonrpc":"2.0","id":1,"result":{}}""" with
    | Response(Some 1, _) -> ()
    | other -> Assert.Fail(sprintf "expected Response 1, got %A" other)

[<Fact>]
let ``classify returns Other for malformed input`` () =
    Assert.Equal(Other, classify "not json")

[<Fact>]
let ``configurationItemCount counts requested items`` () =
    let msg = """{"id":1,"method":"workspace/configuration","params":{"items":[{},{},{}]}}"""
    Assert.Equal(3, configurationItemCount msg)

[<Fact>]
let ``configurationItemCount defaults to at least one`` () =
    Assert.Equal(1, configurationItemCount """{"id":1,"method":"workspace/configuration","params":{}}""")

// --- diagnostic parsing -----------------------------------------------------

let private publish (uri: string) (diags: string) : string =
    sprintf
        """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"%s","diagnostics":%s}}"""
        uri
        diags

[<Fact>]
let ``diagnosticsFromPublish returns the uri and parsed diagnostics`` () =
    let d =
        """[{"range":{"start":{"line":3,"character":5},"end":{"line":3,"character":9}},"severity":1,"message":"Bad","code":"CS1002","source":"Roslyn"}]"""

    let (uri, diags) = diagnosticsFromPublish (publish "file:///x/A.cs" d)
    Assert.Equal(Some "file:///x/A.cs", uri)
    let one = List.exactlyOne diags
    Assert.Equal(Error, one.Severity)
    Assert.Equal(3, one.Line) // 0-based as on the wire
    Assert.Equal(5, one.Character)
    Assert.Equal("Bad", one.Message)
    Assert.Equal(Some "CS1002", one.Code)
    Assert.Equal(Some "Roslyn", one.Source)

[<Fact>]
let ``diagnosticsFromPublish reads a structured code object`` () =
    let d =
        """[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":2,"message":"m","code":{"value":"CS0168","target":"http://x"}}]"""

    let (_, diags) = diagnosticsFromPublish (publish "file:///x/A.cs" d)
    Assert.Equal(Some "CS0168", (List.exactlyOne diags).Code)

[<Fact>]
let ``diagnosticsFromPull reads result.items`` () =
    let json =
        """{"jsonrpc":"2.0","id":2,"result":{"kind":"full","items":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":3}},"severity":1,"message":"x"}]}}"""

    let diags = diagnosticsFromPull json
    Assert.Equal(1, List.length diags)
    Assert.Equal(Error, (List.exactlyOne diags).Severity)

[<Fact>]
let ``diagnosticsFromPull is empty for an empty result`` () =
    Assert.Empty(diagnosticsFromPull """{"jsonrpc":"2.0","id":2,"result":{"kind":"full","items":[]}}""")

// --- rendering --------------------------------------------------------------

let private diag sev line ch msg : Diagnostic =
    { Severity = sev
      Line = line
      Character = ch
      Message = msg
      Code = None
      Source = None }

[<Fact>]
let ``formatDiagnostics returns None when there are no errors or warnings`` () =
    let diags = [ diag Information 0 0 "fyi"; diag Hint 1 1 "tip" ]
    Assert.Equal(None, formatDiagnostics "/x/A.cs" diags)

[<Fact>]
let ``formatDiagnostics returns None for an empty list`` () =
    Assert.Equal(None, formatDiagnostics "/x/A.cs" [])

[<Fact>]
let ``formatDiagnostics renders 1-based positions and a count header`` () =
    let diags = [ diag Error 3 5 "boom"; diag Warning 0 0 "meh" ]

    match formatDiagnostics "/work/A.cs" diags with
    | Some text ->
        Assert.Contains("1 error(s), 1 warning(s) in A.cs", text)
        Assert.Contains("L4:6 error: boom", text)
        Assert.Contains("L1:1 warning: meh", text)
    | None -> Assert.Fail "expected formatted diagnostics"

[<Fact>]
let ``formatDiagnostics drops info and hint severities`` () =
    let diags = [ diag Error 0 0 "real"; diag Information 1 0 "noise"; diag Hint 2 0 "noise" ]

    match formatDiagnostics "/x/A.cs" diags with
    | Some text ->
        Assert.Contains("real", text)
        Assert.DoesNotContain("noise", text)
    | None -> Assert.Fail "expected formatted diagnostics"

[<Fact>]
let ``formatDiagnostics caps the list and notes the remainder`` () =
    let diags = [ for i in 0..40 -> diag Error i 0 (sprintf "e%d" i) ]

    match formatDiagnostics "/x/A.cs" diags with
    | Some text ->
        Assert.Contains("(+16 more)", text) // 41 total, cap 25
        Assert.Contains("41 error(s)", text)
    | None -> Assert.Fail "expected formatted diagnostics"

[<Fact>]
let ``buildHookOutput wraps the context as additionalContext JSON`` () =
    let s = buildHookOutput "hello\nworld"
    use doc = parseDoc s
    Assert.Equal("hello\nworld", doc.RootElement.GetProperty("additionalContext").GetString())
