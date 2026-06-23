module AstGrepOutline.Payload

open System
open System.IO
open System.Runtime.InteropServices
open Thoth.Json.Net

// The hook payload arrives on stdin as JSON in one of the postToolUse shapes — the
// Copilot CLI camelCase form or the VS Code snake_case form. Rather than poke at a
// loosely-typed JSON tree, the payload is decoded into the typed record below with
// Thoth.Json.Net `Decoder`s (accepting both key casings), then everything the outline
// flow needs is derived from it. The matching response is encoded with the same library.

/// Keys a read/search tool might use in its args to name an affected file or folder.
let pathKeys =
    [ "path"
      "paths"
      "file_path"
      "filePath"
      "filename"
      "file"
      "target_file"
      "targetFile" ]

/// Cap on how many distinct targets we outline per tool call.
let maxTargets = 5

/// Cap on the additionalContext payload size; longer output is truncated.
let maxContextChars = 9500

/// A decoded postToolUse payload, reduced to what the outline flow consumes.
/// `RawPaths` is the flattened list of path-like strings pulled out of the
/// `unknown` tool-args (`toolArgs` / `tool_input`), still raw/unresolved.
type Payload =
    { ToolName: string
      ResultType: string option
      Cwd: string option
      RawPaths: string list }

let private isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

/// Case-folding comparer matching `os.path.normcase` semantics: case-insensitive on
/// Windows, case-sensitive elsewhere. Used to dedupe resolved targets.
let pathComparer: StringComparer =
    if isWindows then StringComparer.OrdinalIgnoreCase else StringComparer.Ordinal

// --- Thoth decoders ---------------------------------------------------------

/// Flatten a value into the path-like strings it carries: a non-blank string yields
/// itself, a list yields the concatenation of its elements flattened, anything else
/// yields nothing. Recursion mirrors the Python helper so nested arrays are handled.
let rec private asStrings: Decoder<string list> =
    fun path value ->
        Decode.oneOf
            [ Decode.string
              |> Decode.map (fun s -> if String.IsNullOrWhiteSpace s then [] else [ s ])
              Decode.list asStrings |> Decode.map List.concat
              Decode.succeed [] ]
            path value

/// Gather path-like strings from an object's known path keys, in key order.
let private objectPaths: Decoder<string list> =
    Decode.object (fun get ->
        pathKeys
        |> List.collect (fun key -> get.Optional.Field key asStrings |> Option.defaultValue []))

/// Path-like strings from a tool-args *value*: an object contributes its path keys,
/// any other shape (array, bare string) is flattened directly.
let private argsObjectOrArray: Decoder<string list> =
    Decode.oneOf [ objectPaths; asStrings ]

/// Path-like strings from the `unknown` tool-args. Copilot CLI delivers it either as a
/// JSON *object* or as a JSON-encoded *string* whose contents are that object; a string
/// is re-parsed, and a string that is not JSON is itself treated as a single candidate.
let private argsDecoder: Decoder<string list> =
    fun path value ->
        match Decode.string path value with
        | Ok s ->
            match Decode.fromString argsObjectOrArray s with
            | Ok paths -> Ok paths
            | Error _ -> Ok(if String.IsNullOrWhiteSpace s then [] else [ s ])
        | Error _ -> argsObjectOrArray path value

let private decoder: Decoder<Payload> =
    Decode.object (fun get ->
        { ToolName =
            get.Optional.Field "toolName" Decode.string
            |> Option.orElseWith (fun () -> get.Optional.Field "tool_name" Decode.string)
            |> Option.defaultValue ""
          ResultType =
            get.Optional.At [ "toolResult"; "resultType" ] Decode.string
            |> Option.orElseWith (fun () -> get.Optional.At [ "tool_result"; "result_type" ] Decode.string)
          Cwd = get.Optional.Field "cwd" Decode.string
          RawPaths =
            match get.Optional.Field "toolArgs" argsDecoder with
            | Some paths -> paths
            | None -> get.Optional.Field "tool_input" argsDecoder |> Option.defaultValue [] })

/// Decode a postToolUse payload string into the typed record, or `None` when the JSON
/// is malformed or not an object. Pure: no IO.
let decode (input: string) : Payload option =
    try
        match Decode.fromString decoder input with
        | Ok payload -> Some payload
        | Error _ -> None
    with _ ->
        None

// --- Pure helpers -----------------------------------------------------------

/// Resolve a possibly-relative, possibly `~`-prefixed tool path against `cwd`.
let resolvePath (cwd: string) (raw: string) : string =
    let expanded =
        if raw = "~" then
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        elif raw.StartsWith "~/" || raw.StartsWith "~\\" then
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, raw.Substring 2)
        else
            raw

    if Path.IsPathRooted expanded then
        Path.GetFullPath expanded
    else
        Path.GetFullPath(Path.Combine(cwd, expanded))

/// One fenced outline section for a resolved target.
let buildSection (label: string) (outline: string) : string =
    sprintf "ast-grep outline for %s:\n\n```text\n%s\n```" label outline

/// Truncate combined context to the size cap, leaving room for a marker.
let truncateContext (context: string) : string =
    if context.Length > maxContextChars then
        context.Substring(0, maxContextChars - 80).TrimEnd() + "\n\n[ast-grep outline truncated]"
    else
        context

/// Serialize the postToolUse hook response carrying additionalContext.
let encodeOutput (context: string) : string =
    Encode.object [ "additionalContext", Encode.string context ] |> Encode.toString 0
