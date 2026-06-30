module CSharpLintHook.Payload

open System
open System.IO
open System.Text.RegularExpressions
open Thoth.Json.Net
open CSharpLintHook.Common

/// The postToolUse event schema, modelled faithfully. The Copilot CLI camelCase
/// payload and the VS Code snake_case payload differ only in field names, so a single
/// record models both; `decode` produces it and the derivations below (`isSuccess`,
/// `filePath`) read it rather than baking computed values into the record.
module PostToolUse =

    /// The tool's result block: `resultType` / `textResultForLlm` (camelCase) or
    /// `result_type` / `text_result_for_llm` (VS Code).
    type ToolResult =
        { ResultType: string
          TextResultForLlm: string option }

    /// A decoded postToolUse payload, mirroring the event schema. `ToolArgs` is the
    /// `unknown` tool-args captured as raw JSON text (the object serialized, or the
    /// JSON-encoded string as delivered) so derivations can both pull out a path and
    /// scan it for a `*.cs` reference.
    type PostToolUse =
        { SessionId: string option
          Timestamp: string option
          Cwd: string
          ToolName: string
          ToolArgs: string option
          ToolResult: ToolResult }

    /// Keys a file-editing tool might use in toolArgs to name the affected file.
    let private pathKeys =
        [ "path"; "file_path"; "filePath"; "filename"; "file"; "target_file" ]

    /// Find the affected file path inside a tool-args object, trying each `pathKeys`
    /// field in order.
    let private pathFieldDecoder: Decoder<string> =
        pathKeys |> List.map (fun key -> Decode.field key Decode.string) |> Decode.oneOf

    /// Capture the `unknown` tool-args as raw JSON text across both delivery shapes:
    /// the JSON-encoded *string* as-is, or the *object* serialized back to JSON.
    let private argsTextDecoder: Decoder<string option> =
        Decode.oneOf
            [ Decode.string |> Decode.map Some
              Decode.value |> Decode.map (Encode.toString 0 >> Some) ]

    /// `timestamp` is a number (camelCase) or an ISO string (VS Code); keep its text.
    let private timestampDecoder: Decoder<string> =
        Decode.oneOf [ Decode.string; Decode.value |> Decode.map (Encode.toString 0) ]

    /// Decode the payload for one field-naming convention. The nested result type is
    /// *required*: its presence structurally identifies the schema, so it discriminates
    /// the two casings inside the `oneOf` below.
    let private decoderFor
        (sessionIdField: string)
        (toolNameField: string)
        (toolArgsField: string)
        (resultField: string)
        (resultTypeField: string)
        (textField: string)
        : Decoder<PostToolUse> =
        Decode.object (fun get ->
            { SessionId = get.Optional.Field sessionIdField Decode.string
              Timestamp = get.Optional.Field "timestamp" timestampDecoder
              Cwd =
                get.Optional.Field "cwd" Decode.string
                |> Option.defaultWith Directory.GetCurrentDirectory
              ToolName = get.Optional.Field toolNameField Decode.string |> Option.defaultValue ""
              ToolArgs = get.Optional.Field toolArgsField argsTextDecoder |> Option.flatten
              ToolResult =
                { ResultType = get.Required.At [ resultField; resultTypeField ] Decode.string
                  TextResultForLlm = get.Optional.At [ resultField; textField ] Decode.string } })

    /// The camelCase `postToolUse` schema.
    let private camelCaseDecoder: Decoder<PostToolUse> =
        decoderFor "sessionId" "toolName" "toolArgs" "toolResult" "resultType" "textResultForLlm"

    /// The VS Code compatible `PostToolUse` schema.
    let private vsCodeDecoder: Decoder<PostToolUse> =
        decoderFor "session_id" "tool_name" "tool_input" "tool_result" "result_type" "text_result_for_llm"

    /// Try the camelCase shape first, then the VS Code shape; the required result type
    /// in each makes the wrong-casing shape fall through.
    let private decoder: Decoder<PostToolUse> =
        Decode.oneOf [ camelCaseDecoder; vsCodeDecoder ]

    /// Decode a postToolUse payload string into the typed record. Returns None when the
    /// JSON is malformed or is not a postToolUse schema. Pure: no IO.
    let decode (input: string) : PostToolUse option =
        try
            match Decode.fromString decoder input with
            | Ok info -> Some info
            | Error _ -> None
        with _ ->
            None

    /// Whether the tool result was a success.
    let isSuccess (info: PostToolUse) : bool =
        info.ToolResult.ResultType = "success"

    /// The affected file named by a known path key in the tool-args, if any. Used by
    /// the format flow, which must target one specific on-disk file.
    let filePath (info: PostToolUse) : string option =
        info.ToolArgs
        |> Option.bind (fun text ->
            match Decode.fromString pathFieldDecoder text with
            | Ok path -> Some path
            | Error _ -> None)

/// Resolve a possibly relative tool path against the payload's cwd.
let resolvePath (cwd: string) (rawPath: string) : string =
    if Path.IsPathRooted rawPath then
        Path.GetFullPath rawPath
    else
        Path.GetFullPath(Path.Combine(cwd, rawPath))

/// A C# source file we should act on: supported extension, not generated,
/// not under bin/obj.
let isFormattableCSharp (fullPath: string) : bool =
    let hasSupportedExtension =
        [ ".cs"; ".csx" ]
        |> List.exists (fun extension -> fullPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))

    let isGenerated =
        [ ".g.cs"
          ".g.i.cs"
          ".designer.cs"
          ".generated.cs"
          ".feature.cs" ]
        |> List.exists (fun suffix -> fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))

    let inBuildDir =
        fullPath.Replace('\\', '/').Split('/')
        |> Array.exists (fun seg -> seg = "obj" || seg = "bin")

    hasSupportedExtension && not isGenerated && not inBuildDir

/// Human-readable note describing what the hook changed.
let buildAdditionalContext (r: FormatResult) : string =
    let name = Path.GetFileName r.Path

    let detail =
        if r.WholeFile then
            $"reformatted %s{name}"
        else
            $"reformatted %d{r.Regions} changed region(s) in %s{name}"

    $"CSharpLintHook %s{detail} (whitespace/formatting only). The on-disk file now reflects the formatted version."

/// Serialize the postToolUse hook response carrying additionalContext.
let buildHookOutput (additionalContext: string) : string =
    Encode.object [ "additionalContext", Encode.string additionalContext ]
    |> Encode.toString 0

/// The Copilot co-author trailer the agent must not introduce. Precompiled and
/// case-insensitive, tolerating extra spaces after the colon, so the preToolUse guard
/// stays cheap on every bash/powershell command. Matching this marker in the raw command
/// text catches it no matter how the commit is made (`git commit`, `jj commit`, …).
let private copilotCoauthorPattern =
    Regex(@"Co-authored-by:\s*Copilot App", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

/// True when `text` carries the Copilot co-author trailer.
let containsCopilotCoauthor (text: string) : bool =
    copilotCoauthorPattern.IsMatch text

/// The preToolUse deny response that asks the agent to drop itself as co-author. A
/// `permissionDecision` of "deny" with a reason blocks the command and tells the model
/// what to fix; emitting nothing instead lets the command proceed.
let buildCoauthorDenyOutput () : string =
    Encode.object
        [ "permissionDecision", Encode.string "deny"
          "permissionDecisionReason",
          Encode.string
              "Do not add Copilot as a co-author. Remove the \"Co-authored-by: Copilot App\" trailer from the commit message, then run the command again." ]
    |> Encode.toString 0
