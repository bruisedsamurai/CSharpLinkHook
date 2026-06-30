module PwshLintHook.Payload

open Thoth.Json.Net

// The preToolUse payload arrives on stdin as JSON in one of two shapes — the Copilot
// CLI camelCase form (`toolName` / `toolArgs`) or the VS Code snake_case form
// (`tool_name` / `tool_input`). Both deliver the shell tool's command under a
// `command` key inside the (possibly JSON-string-encoded) tool-args. The payload is
// decoded into the typed record below with Thoth.Json.Net decoders, and the matching
// deny response is encoded with the same library.

/// A decoded preToolUse payload reduced to what the tool-guard consumes.
type PreToolUse =
    { ToolName: string
      Command: string option }

/// Pull the `command` field out of a tool-args *value*: an object carrying
/// `command`, or a JSON-encoded *string* whose contents are that object. Anything
/// else yields `None`. Always succeeds (never errors the parse).
let private commandDecoder: Decoder<string option> =
    fun path value ->
        match Decode.field "command" Decode.string path value with
        | Ok cmd -> Ok(Some cmd)
        | Error _ ->
            match Decode.string path value with
            | Ok s ->
                match Decode.fromString (Decode.field "command" Decode.string) s with
                | Ok cmd -> Ok(Some cmd)
                | Error _ -> Ok None
            | Error _ -> Ok None

let private decoder: Decoder<PreToolUse> =
    Decode.object (fun get ->
        { ToolName =
            get.Optional.Field "toolName" Decode.string
            |> Option.orElseWith (fun () -> get.Optional.Field "tool_name" Decode.string)
            |> Option.defaultValue ""
          Command =
            get.Optional.Field "toolArgs" commandDecoder
            |> Option.orElseWith (fun () -> get.Optional.Field "tool_input" commandDecoder)
            |> Option.flatten })

/// Decode a preToolUse payload string into the typed record. Returns `None` when the
/// JSON is malformed or is not an object. Pure: no IO.
let decode (input: string) : PreToolUse option =
    try
        match Decode.fromString decoder input with
        | Ok info -> Some info
        | Error _ -> None
    with _ ->
        None

/// The shell tools whose command we inspect. PowerShell is the only one whose command
/// text is a PowerShell pipeline; `bash` commands are never PowerShell, so they are
/// ignored even if the hook's matcher lets them through.
let isShellTool (toolName: string) : bool =
    match toolName.ToLowerInvariant() with
    | "powershell" -> true
    | _ -> false

/// Serialize a preToolUse deny response. A `permissionDecision` of "deny" with a
/// reason blocks the command and tells the model what to run instead; emitting nothing
/// lets the command proceed.
let buildDenyOutput (reason: string) : string =
    Encode.object
        [ "permissionDecision", Encode.string "deny"
          "permissionDecisionReason", Encode.string reason ]
    |> Encode.toString 0
