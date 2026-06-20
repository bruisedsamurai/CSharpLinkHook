module RoslynLspHook.Lsp

open System
open System.Text
open System.Text.Json.Nodes
open System.Text.Json
open RoslynLspHook.Common

// ---------------------------------------------------------------------------
// Pure JSON-RPC / LSP protocol helpers. No IO lives here: these build request
// strings, frame/deframe the Content-Length wire format, parse diagnostics out
// of server messages, and render them for the agent. LspClient.fs performs the
// actual pipe IO using these pieces.
// ---------------------------------------------------------------------------

let severityOfInt (n: int) : Severity =
    match n with
    | 1 -> Error
    | 2 -> Warning
    | 3 -> Information
    | 4 -> Hint
    | _ -> Information

/// Inverse of `severityOfInt`: the LSP numeric severity for a `Severity`.
let intOfSeverity (s: Severity) : int =
    match s with
    | Error -> 1
    | Warning -> 2
    | Information -> 3
    | Hint -> 4

// Tiny JSON-building DSL so request payloads escape their dynamic parts safely.
let private jstr (s: string) : JsonNode = nonNull (JsonValue.Create s) :> JsonNode
let private jint (i: int) : JsonNode = JsonValue.Create i :> JsonNode
let private jbool (b: bool) : JsonNode = JsonValue.Create b :> JsonNode

let private jobj (pairs: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()
    for (k, v) in pairs do
        o[k] <- v

    o :> JsonNode

let private jarr (items: JsonNode list) : JsonNode =
    let a = JsonArray()
    for i in items do
        a.Add i

    a :> JsonNode

/// The `file://` URI for an absolute filesystem path.
let fileUri (path: string) : string = Uri(path).AbsoluteUri

/// Wrap a JSON payload in the LSP `Content-Length` frame.
let frame (payload: string) : byte[] =
    let body = Encoding.UTF8.GetBytes payload
    let header = Encoding.ASCII.GetBytes $"Content-Length: {body.Length}\r\n\r\n"
    Array.append header body

/// Extract the `Content-Length` value from a raw header block.
let parseContentLength (headers: string) : int option =
    headers.Split('\n')
    |> Array.tryPick (fun line ->
        let t = line.TrimEnd('\r').Trim()

        if t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
            match Int32.TryParse(t.Substring("Content-Length:".Length).Trim()) with
            | true, n -> Some n
            | _ -> None
        else
            None)

// --- Outgoing requests / notifications -------------------------------------

/// The `initialize` request. `--autoLoadProjects` makes the server discover and
/// load the workspace's projects itself, so we only advertise capabilities.
let initializeRequest (id: int) (processId: int) (cwd: string) : string =
    let uri = fileUri cwd

    let caps =
        jobj
            [ "window", jobj [ "workDoneProgress", jbool true ]
              "textDocument",
              jobj
                  [ "synchronization", jobj [ "dynamicRegistration", jbool true; "didSave", jbool true ]
                    "publishDiagnostics", jobj [ "relatedInformation", jbool true ]
                    "diagnostic", jobj [ "dynamicRegistration", jbool true ] ]
              "workspace", jobj [ "configuration", jbool true; "workspaceFolders", jbool true ] ]

    let prms =
        jobj
            [ "processId", jint processId
              "clientInfo", jobj [ "name", jstr "RoslynLspHook"; "version", jstr "1.0" ]
              "rootUri", jstr uri
              "rootPath", jstr cwd
              "capabilities", caps
              "workspaceFolders", jarr [ jobj [ "uri", jstr uri; "name", jstr "workspace" ] ] ]

    (jobj [ "jsonrpc", jstr "2.0"; "id", jint id; "method", jstr "initialize"; "params", prms ])
        .ToJsonString()

let initializedNotification () : string =
    (jobj [ "jsonrpc", jstr "2.0"; "method", jstr "initialized"; "params", jobj [] ])
        .ToJsonString()

let didOpenNotification (uri: string) (text: string) : string =
    (jobj
        [ "jsonrpc", jstr "2.0"
          "method", jstr "textDocument/didOpen"
          "params",
          jobj
              [ "textDocument",
                jobj
                    [ "uri", jstr uri
                      "languageId", jstr "csharp"
                      "version", jint 1
                      "text", jstr text ] ] ])
        .ToJsonString()

let diagnosticRequest (id: int) (uri: string) : string =
    (jobj
        [ "jsonrpc", jstr "2.0"
          "id", jint id
          "method", jstr "textDocument/diagnostic"
          "params", jobj [ "textDocument", jobj [ "uri", jstr uri ] ] ])
        .ToJsonString()

/// `textDocument/didClose`: tell the server we are done with this document, so a
/// long-lived server does not accumulate open documents across broker requests.
let didCloseNotification (uri: string) : string =
    (jobj
        [ "jsonrpc", jstr "2.0"
          "method", jstr "textDocument/didClose"
          "params", jobj [ "textDocument", jobj [ "uri", jstr uri ] ] ])
        .ToJsonString()

/// Roslyn custom notification: load `uri` (a .sln file URI) into the server's
/// in-process workspace so diagnostics resolve against that solution.
let solutionOpenNotification (uri: string) : string =
    (jobj
        [ "jsonrpc", jstr "2.0"
          "method", jstr "solution/open"
          "params", jobj [ "solution", jstr uri ] ])
        .ToJsonString()

/// A stub response echoing the server's request id. `resultJson` is raw JSON
/// (e.g. "null" or "[null,null]"), matching what Roslyn expects during init.
let stubResponse (idRawJson: string) (resultJson: string) : string =
    $"{{\"jsonrpc\":\"2.0\",\"id\":{idRawJson},\"result\":{resultJson}}}"

// --- Incoming message classification ---------------------------------------

/// A server→client message, classified just enough to drive the handshake.
type Incoming =
    /// Reply to one of our requests (id is ours; carries the raw message json).
    | Response of id: int option * json: string
    /// A server-initiated request we must answer (idRaw is the raw id json).
    | ServerRequest of idRaw: string * method: string * json: string
    /// A server notification (e.g. textDocument/publishDiagnostics).
    | Notification of method: string * json: string
    /// Anything we do not care about.
    | Other

let private prop (o: JsonObject) (name: string) : JsonNode option =
    let mutable v: JsonNode | null = null
    if o.TryGetPropertyValue(name, &v) then Option.ofObj v else None

let private asObj (n: JsonNode) : JsonObject option =
    match n with
    | :? JsonObject as o -> Some o
    | _ -> None

let private asArr (n: JsonNode) : JsonArray option =
    match n with
    | :? JsonArray as a -> Some a
    | _ -> None

let private tryGetString (n: JsonNode) : string option =
    try
        match n.GetValueKind() with
        | JsonValueKind.String -> Some(n.GetValue<string>())
        | _ -> None
    with _ ->
        None

let private tryGetInt (n: JsonNode) : int option =
    try
        match n.GetValueKind() with
        | JsonValueKind.Number -> Some(n.GetValue<int>())
        | _ -> None
    with _ ->
        None

let private parseObj (json: string) : JsonObject option =
    try
        match JsonNode.Parse json with
        | null -> None
        | node -> asObj node
    with _ ->
        None

/// Classify a raw server message. Re-parsing for the body later keeps the
/// returned value free of live JsonNode references.
let classify (json: string) : Incoming =
    match parseObj json with
    | None -> Other
    | Some o ->
        let methodOpt = prop o "method" |> Option.bind tryGetString
        let idOpt = prop o "id"

        match methodOpt, idOpt with
        | Some m, Some idn -> ServerRequest(idn.ToJsonString(), m, json)
        | Some m, None -> Notification(m, json)
        | None, Some idn -> Response(tryGetInt idn, json)
        | None, None -> Other

/// Number of `items` in a `workspace/configuration` request (≥ 1), so we can
/// answer with one `null` per requested item.
let configurationItemCount (json: string) : int =
    match parseObj json with
    | Some o ->
        match prop o "params" |> Option.bind asObj |> Option.bind (fun p -> prop p "items") |> Option.bind asArr with
        | Some a -> max 1 a.Count
        | None -> 1
    | None -> 1

// --- Diagnostic parsing -----------------------------------------------------

let private parseCode (o: JsonObject) : string option =
    match prop o "code" with
    | None -> None
    | Some n ->
        match n with
        | :? JsonObject as co ->
            prop co "value"
            |> Option.bind (fun v -> tryGetString v |> Option.orElse (tryGetInt v |> Option.map string))
        | _ -> tryGetString n |> Option.orElse (tryGetInt n |> Option.map string)

let private parseDiagnostic (n: JsonNode) : Diagnostic option =
    match n with
    | :? JsonObject as o ->
        let startObj =
            prop o "range"
            |> Option.bind asObj
            |> Option.bind (fun r -> prop r "start")
            |> Option.bind asObj

        let line =
            startObj |> Option.bind (fun s -> prop s "line" |> Option.bind tryGetInt) |> Option.defaultValue 0

        let ch =
            startObj
            |> Option.bind (fun s -> prop s "character" |> Option.bind tryGetInt)
            |> Option.defaultValue 0

        let sev =
            prop o "severity"
            |> Option.bind tryGetInt
            |> Option.map severityOfInt
            |> Option.defaultValue Warning

        let msg = prop o "message" |> Option.bind tryGetString |> Option.defaultValue ""

        Some
            { Severity = sev
              Line = line
              Character = ch
              Message = msg
              Code = parseCode o
              Source = prop o "source" |> Option.bind tryGetString }
    | _ -> None

let private diagsFromArray (n: JsonNode option) : Diagnostic list =
    match n |> Option.bind asArr with
    | Some arr -> arr |> Seq.choose (fun item -> item |> Option.ofObj |> Option.bind parseDiagnostic) |> List.ofSeq
    | None -> []

/// Parse a `textDocument/publishDiagnostics` notification: (its uri, diagnostics).
let diagnosticsFromPublish (json: string) : string option * Diagnostic list =
    match parseObj json |> Option.bind (fun o -> prop o "params") |> Option.bind asObj with
    | Some p -> (prop p "uri" |> Option.bind tryGetString, diagsFromArray (prop p "diagnostics"))
    | None -> (None, [])

/// Parse a `textDocument/diagnostic` pull response (`result.items`).
let diagnosticsFromPull (json: string) : Diagnostic list =
    match parseObj json |> Option.bind (fun o -> prop o "result") |> Option.bind asObj with
    | Some r -> diagsFromArray (prop r "items")
    | None -> []

// --- Rendering --------------------------------------------------------------

let private severityWord (s: Severity) : string =
    match s with
    | Error -> "error"
    | Warning -> "warning"
    | Information -> "info"
    | Hint -> "hint"

/// Render errors and warnings as a concise `additionalContext` block, or None
/// when the file is clean (so the hook stays silent). Positions are shown 1-based.
let formatDiagnostics (file: string) (diags: Diagnostic list) : string option =
    let relevant =
        diags |> List.filter (fun d -> d.Severity = Error || d.Severity = Warning)

    match relevant with
    | [] -> None
    | _ ->
        let name = IO.Path.GetFileName file
        let errors = relevant |> List.filter (fun d -> d.Severity = Error) |> List.length
        let warnings = relevant |> List.filter (fun d -> d.Severity = Warning) |> List.length
        let cap = 25
        let sorted = relevant |> List.sortBy (fun d -> (d.Line, d.Character))

        let render (d: Diagnostic) =
            let code =
                match d.Code with
                | Some c -> " " + c
                | None -> ""

            let msg = d.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
            $"  L{d.Line + 1}:{d.Character + 1} {severityWord d.Severity}{code}: {msg}"

        let body = sorted |> List.truncate cap |> List.map render |> String.concat "\n"

        let more =
            if List.length sorted > cap then
                $"\n  (+{List.length sorted - cap} more)"
            else
                ""

        let header =
            $"RoslynLspHook: {errors} error(s), {warnings} warning(s) in {name} (from the Roslyn language server):"

        Some(header + "\n" + body + more)

/// Serialize a postToolUse hook response that appends our diagnostics `report`
/// to the tool result the CLI shows the model. Copilot CLI 1.0.64 reads
/// `modifiedResult` and uses it in place of the original tool result (a hook's
/// stdout is otherwise ignored), so we clone the original result and append our
/// report to `textResultForLlm`, preserving `resultType` and every other field so
/// nothing the tool produced is lost. `toolResultJson` is the raw original result
/// (`{}` when the payload carried none).
let buildModifiedResult (toolResultJson: string) (report: string) : string =
    let result =
        match parseObj toolResultJson with
        | Some o -> o
        | None -> JsonObject()

    // What the model would have seen for a success result: textResultForLlm, else
    // sessionLog. Append our report to it rather than replacing it.
    let existing =
        [ "textResultForLlm"; "sessionLog" ]
        |> List.tryPick (fun key ->
            prop result key
            |> Option.bind tryGetString
            |> Option.filter (fun s -> s.Trim().Length > 0))
        |> Option.defaultValue ""

    let combined =
        if existing.Length = 0 then report else existing + "\n\n" + report

    result["textResultForLlm"] <- JsonValue.Create combined

    let root = JsonObject()
    root["modifiedResult"] <- result
    root.ToJsonString()

// --- Broker protocol (hook ⇄ broker over the pipe) --------------------------
// The short-lived postToolUse hook is a thin client of the long-lived broker.
// Messages are the same `Content-Length`-framed JSON, but the bodies are these
// tiny commands/replies rather than raw LSP. All helpers are pure so they unit
// test without any IO.

/// A command the hook sends the broker over the pipe.
type BrokerCommand =
    /// Fetch diagnostics for an absolute file path.
    | Diag of path: string
    /// Scope the warm server to an absolute solution path (.sln/.slnx).
    | Open of path: string
    /// Unrecognized / malformed request.
    | Unknown

let brokerDiagRequest (path: string) : string =
    (jobj [ "cmd", jstr "diag"; "path", jstr path ]).ToJsonString()

let brokerOpenRequest (path: string) : string =
    (jobj [ "cmd", jstr "open"; "path", jstr path ]).ToJsonString()

/// Parse a framed broker request body into a `BrokerCommand`.
let parseBrokerCommand (json: string) : BrokerCommand =
    match parseObj json with
    | None -> Unknown
    | Some o ->
        let cmd = prop o "cmd" |> Option.bind tryGetString
        let path = prop o "path" |> Option.bind tryGetString

        match cmd, path with
        | Some "diag", Some p -> Diag p
        | Some "open", Some p -> Open p
        | _ -> Unknown

/// Serialize diagnostics for the broker's `diag` reply. Positions stay 0-based
/// (as on the LSP wire and in `Diagnostic`), so the client deserializes them
/// straight back into `Diagnostic` for the existing formatter.
let serializeDiagnostics (diags: Diagnostic list) : string =
    let arr =
        diags
        |> List.map (fun d ->
            let baseFields =
                [ "severity", jint (intOfSeverity d.Severity)
                  "line", jint d.Line
                  "character", jint d.Character
                  "message", jstr d.Message ]

            let withCode =
                match d.Code with
                | Some c -> baseFields @ [ "code", jstr c ]
                | None -> baseFields

            let withSource =
                match d.Source with
                | Some s -> withCode @ [ "source", jstr s ]
                | None -> withCode

            jobj withSource)

    (jobj [ "diagnostics", jarr arr ]).ToJsonString()

/// Parse a broker `diag` reply back into diagnostics. [] on any failure.
let deserializeDiagnostics (json: string) : Diagnostic list =
    match parseObj json |> Option.bind (fun o -> prop o "diagnostics") |> Option.bind asArr with
    | None -> []
    | Some arr ->
        arr
        |> Seq.choose (fun item ->
            item
            |> Option.ofObj
            |> Option.bind asObj
            |> Option.map (fun o ->
                { Severity =
                    prop o "severity"
                    |> Option.bind tryGetInt
                    |> Option.map severityOfInt
                    |> Option.defaultValue Warning
                  Line = prop o "line" |> Option.bind tryGetInt |> Option.defaultValue 0
                  Character = prop o "character" |> Option.bind tryGetInt |> Option.defaultValue 0
                  Message = prop o "message" |> Option.bind tryGetString |> Option.defaultValue ""
                  Code = prop o "code" |> Option.bind tryGetString
                  Source = prop o "source" |> Option.bind tryGetString }))
        |> List.ofSeq

/// The broker's `open` reply.
let brokerOkReply () : string =
    (jobj [ "ok", jbool true ]).ToJsonString()

/// Read the `ok` flag from a broker `open` reply. False on any failure.
let parseOk (json: string) : bool =
    match parseObj json |> Option.bind (fun o -> prop o "ok") with
    | Some n ->
        try
            n.GetValueKind() = JsonValueKind.True
        with _ ->
            false
    | None -> false
