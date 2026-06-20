module RoslynLspHook.Payload

open System
open System.IO
open Thoth.Json.Net

// The hook payload arrives on stdin as JSON in one of four shapes — the Copilot
// CLI camelCase form or the VS Code snake_case form, each for `sessionStart` or
// `postToolUse`. Rather than poke at a loosely-typed JSON tree, we decode the
// payload into the typed records below with Thoth.Json.Net `Decoder`s (the
// decoders accept both key casings), then derive everything the state machine
// needs — most importantly the workspace `cwd` — from those records.

/// The tool's result block of a `postToolUse` payload, unified across the
/// camelCase (`resultType` / `textResultForLlm`) and VS Code snake_case
/// (`result_type` / `text_result_for_llm`) shapes.
type ToolResult =
    { ResultType: string
      TextResultForLlm: string option }

/// A decoded `postToolUse` payload. `FilePath` is the affected file pulled out of
/// the `unknown` `toolArgs` / `tool_input` value (which is either a JSON object or
/// a JSON-encoded string), still raw/unresolved. `ToolResultRaw` is the original
/// result object's JSON, echoed back verbatim as `modifiedResult`.
type PostToolUsePayload =
    { Cwd: string
      FilePath: string option
      ToolResult: ToolResult option
      ToolResultRaw: string }

/// A decoded `sessionStart` payload.
type SessionStartPayload =
    { Cwd: string
      Source: string
      InitialPrompt: string option }

/// The hook payload after decoding: one of the events we act on, or one we ignore
/// (wrong event, malformed JSON, …).
type HookPayload =
    | PostToolUseEvent of PostToolUsePayload
    | SessionStartEvent of SessionStartPayload
    | IgnoredEvent

/// Outcome of parsing a hook payload into the action the hook should take. All
/// fields are already resolved/guarded so Logic.fs stays free of IO and policy.
type Parsed =
    /// sessionStart for the given workspace cwd: start the language server.
    | DoSessionStart of cwd: string
    /// postToolUse on a C# file to lint (file None ⇒ nothing to do this turn). The
    /// `toolResult` is the raw JSON of the tool's original result, echoed back with
    /// diagnostics appended via `modifiedResult`.
    | DoToolUse of cwd: string * file: string option * toolResult: string
    /// Event we do not act on (wrong event, malformed JSON, failed tool, …).
    | Ignore

/// Keys a file-editing tool might use to name the affected file.
let private pathKeys =
    [ "path"; "file_path"; "filePath"; "filename"; "file"; "target_file" ]

/// Resolve a possibly relative tool path against the payload's cwd.
let resolvePath (cwd: string) (rawPath: string) : string =
    if Path.IsPathRooted rawPath then
        Path.GetFullPath rawPath
    else
        Path.GetFullPath(Path.Combine(cwd, rawPath))

/// A C# file we should lint: ends in .cs, not generated, not under bin/obj.
let isLintableCSharp (fullPath: string) : bool =
    let isCs = fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)

    let isGenerated =
        [ ".g.cs"; ".g.i.cs"; ".designer.cs"; ".generated.cs"; ".feature.cs" ]
        |> List.exists (fun suffix -> fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))

    let inBuildDir =
        fullPath.Replace('\\', '/').Split('/')
        |> Array.exists (fun seg -> seg = "obj" || seg = "bin")

    isCs && not isGenerated && not inBuildDir

// --- Thoth decoders ---------------------------------------------------------

/// Find the affected file path inside an object value, trying each `pathKeys`
/// field in order.
let private pathFieldDecoder: Decoder<string> =
    pathKeys |> List.map (fun key -> Decode.field key Decode.string) |> Decode.oneOf

/// Decode the `unknown` tool-args value into the affected file path. Copilot CLI
/// delivers it in one of two shapes: a JSON *object* (`{ "path": "…" }`) or a
/// JSON-encoded *string* whose contents are that object (`toolArgs` arrives
/// pre-serialized). Handle both, and fall back to `None` for anything else.
let private argsPathDecoder: Decoder<string option> =
    Decode.oneOf
        [ pathFieldDecoder |> Decode.map Some
          Decode.string
          |> Decode.andThen (fun inner ->
              match Decode.fromString pathFieldDecoder inner with
              | Ok path -> Decode.succeed (Some path)
              | Error _ -> Decode.succeed None)
          Decode.succeed (None: string option) ]

let private toolResultDecoder: Decoder<ToolResult> =
    Decode.object (fun get ->
        { ResultType =
            (get.Optional.Field "resultType" Decode.string
             |> Option.orElseWith (fun () -> get.Optional.Field "result_type" Decode.string))
            |> Option.defaultValue ""
          TextResultForLlm =
            get.Optional.Field "textResultForLlm" Decode.string
            |> Option.orElseWith (fun () -> get.Optional.Field "text_result_for_llm" Decode.string) })

/// A `sessionStart` payload. `source` is *required*: it is what structurally
/// distinguishes a sessionStart from a postToolUse, so this decoder fails (and the
/// `oneOf` below falls through) on a payload that lacks it.
let private sessionStartDecoder: Decoder<SessionStartPayload> =
    Decode.object (fun get ->
        { Cwd =
            get.Optional.Field "cwd" Decode.string
            |> Option.defaultWith Directory.GetCurrentDirectory
          Source = get.Required.Field "source" Decode.string
          InitialPrompt =
            get.Optional.Field "initialPrompt" Decode.string
            |> Option.orElseWith (fun () -> get.Optional.Field "initial_prompt" Decode.string) })

/// True when at least one of `names` is present.
let private hasAnyField (names: string list) : Decoder<bool> =
    Decode.object (fun get -> names |> List.exists (fun name -> (get.Optional.Field name Decode.value).IsSome))

let private postToolUsePayloadDecoder: Decoder<PostToolUsePayload> =
    Decode.object (fun get ->
        { Cwd =
            get.Optional.Field "cwd" Decode.string
            |> Option.defaultWith Directory.GetCurrentDirectory
          FilePath =
            (get.Optional.Field "toolArgs" argsPathDecoder
             |> Option.orElseWith (fun () -> get.Optional.Field "tool_input" argsPathDecoder))
            |> Option.flatten
          ToolResult =
            get.Optional.Field "toolResult" toolResultDecoder
            |> Option.orElseWith (fun () -> get.Optional.Field "tool_result" toolResultDecoder)
          ToolResultRaw =
            (get.Optional.Field "toolResult" Decode.value
             |> Option.orElseWith (fun () -> get.Optional.Field "tool_result" Decode.value))
            |> Option.map (Encode.toString 0)
            |> Option.defaultValue "{}" })

/// A `postToolUse` payload must carry at least one tool field; otherwise this fails
/// so an unrelated event is not misread as a tool use.
let private postToolUseDecoder: Decoder<PostToolUsePayload> =
    hasAnyField [ "toolArgs"; "tool_input"; "toolResult"; "tool_result" ]
    |> Decode.andThen (fun hasTool ->
        if hasTool then
            postToolUsePayloadDecoder
        else
            Decode.fail "not a postToolUse payload (no tool fields)")

/// Decode the hook payload into the typed `HookPayload` model. The payload's *shape*
/// selects the event: try to decode a `sessionStart` record first, then a
/// `postToolUse` record — whichever record decodes defines the event, so there is no
/// `hook_event_name` string to read or trust.
let private hookPayloadDecoder: Decoder<HookPayload> =
    Decode.oneOf
        [ sessionStartDecoder |> Decode.map SessionStartEvent
          postToolUseDecoder |> Decode.map PostToolUseEvent ]

/// Decode a hook payload string into the typed `HookPayload` model. The event is
/// inferred from the payload's own shape (the schema is the hint). Every failure
/// shape (malformed JSON, non-object payload, unexpected field types) collapses to
/// `IgnoredEvent`, so callers never have to handle decode errors.
let decode (input: string) : HookPayload =
    try
        match Decode.fromString hookPayloadDecoder input with
        | Ok payload -> payload
        | Error _ -> IgnoredEvent
    with _ ->
        IgnoredEvent

/// The C# file a `postToolUse` edited that we should lint, or `None` when the tool
/// failed, named no file, or named one we skip (non-C#, generated, or under
/// bin/obj). The raw path is resolved against the payload's cwd.
let lintTarget (payload: PostToolUsePayload) : string option =
    if payload.ToolResult |> Option.exists (fun result -> result.ResultType = "success") then
        payload.FilePath
        |> Option.map (resolvePath payload.Cwd)
        |> Option.filter isLintableCSharp
    else
        None

/// Parse a hook payload into the action the hook should take. Thin adapter over
/// `decode` + `lintTarget`; kept so callers (and tests) that only need the
/// resolved action don't have to map the typed model themselves.
let parse (input: string) : Parsed =
    match decode input with
    | SessionStartEvent payload -> DoSessionStart payload.Cwd
    | PostToolUseEvent payload -> DoToolUse(payload.Cwd, lintTarget payload, payload.ToolResultRaw)
    | IgnoredEvent -> Ignore
