module CSharpLintHook.Payload

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open CSharpLintHook.Common

/// The fields we care about from a Copilot CLI postToolUse payload.
type PayloadInfo =
    { Cwd: string
      ToolName: string
      Success: bool
      FilePath: string option }

/// Keys a file-editing tool might use in toolArgs to name the affected file.
let private pathKeys =
    [ "path"; "file_path"; "filePath"; "filename"; "file"; "target_file" ]

let private tryStringProp (el: JsonElement) (name: string) : string option =
    if el.ValueKind = JsonValueKind.Object then
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> None
            | s -> Some s
        | _ -> None
    else
        None

/// First string property in `el` (an object) matching one of `pathKeys`.
let private pickPath (el: JsonElement) : string option =
    pathKeys |> List.tryPick (tryStringProp el)

/// Extract the affected file path from a tool-args value, which Copilot CLI
/// delivers in one of two shapes:
///   * a JSON-encoded *string*, e.g. "{\"path\":\"C.cs\",...}" (the real CLI
///     postToolUse shape — `toolArgs`/`tool_input` arrives pre-serialized), or
///   * a JSON *object*, e.g. { "path": "C.cs" } (as the type `unknown` allows).
/// Handle both so the hook works against live payloads, not just object-shaped
/// test fixtures.
let private pathFromArgs (args: JsonElement) : string option =
    match args.ValueKind with
    | JsonValueKind.Object -> pickPath args
    | JsonValueKind.String ->
        match args.GetString() with
        | null -> None
        | inner ->
            try
                use innerDoc = JsonDocument.Parse inner

                if innerDoc.RootElement.ValueKind = JsonValueKind.Object then
                    // GetString() returns an independent managed copy, so the
                    // path survives disposal of innerDoc below.
                    pickPath innerDoc.RootElement
                else
                    None
            with _ ->
                None
    | _ -> None

/// Look up a property by its camelCase name first, then its VS Code snake_case
/// alias, returning the matching element if present.
let private tryProp (root: JsonElement) (camel: string) (snake: string) : JsonElement option =
    match root.TryGetProperty camel with
    | true, v -> Some v
    | _ ->
        match root.TryGetProperty snake with
        | true, v -> Some v
        | _ -> None

/// Parse a postToolUse payload. Returns None when the JSON is malformed or not
/// the expected object shape. Accepts both the camelCase event payload
/// (`toolName`/`toolArgs`/`toolResult.resultType`) and the VS Code compatible
/// snake_case payload (`tool_name`/`tool_input`/`tool_result.result_type`).
/// Pure: parsing performs no IO.
let parse (input: string) : PayloadInfo option =
    try
        use doc = JsonDocument.Parse input
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            None
        else
            let cwd =
                tryStringProp root "cwd"
                |> Option.defaultWith Directory.GetCurrentDirectory

            let toolName =
                tryStringProp root "toolName"
                |> Option.orElse (tryStringProp root "tool_name")
                |> Option.defaultValue ""

            let success =
                match tryProp root "toolResult" "tool_result" with
                | Some tr ->
                    (tryStringProp tr "resultType" |> Option.orElse (tryStringProp tr "result_type")) = Some
                        "success"
                | None -> false

            let filePath =
                tryProp root "toolArgs" "tool_input" |> Option.bind pathFromArgs

            Some
                { Cwd = cwd
                  ToolName = toolName
                  Success = success
                  FilePath = filePath }
    with _ ->
        None

/// Resolve a possibly-relative tool path against the payload's cwd.
let resolvePath (cwd: string) (rawPath: string) : string =
    if Path.IsPathRooted rawPath then
        Path.GetFullPath rawPath
    else
        Path.GetFullPath(Path.Combine(cwd, rawPath))

/// A C# file we should format: ends in .cs, not generated, not under bin/obj.
let isFormattableCSharp (fullPath: string) : bool =
    let isCs = fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)

    let isGenerated =
        [ ".g.cs"; ".g.i.cs"; ".designer.cs"; ".generated.cs"; ".feature.cs" ]
        |> List.exists (fun suffix -> fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))

    let inBuildDir =
        fullPath.Replace('\\', '/').Split('/')
        |> Array.exists (fun seg -> seg = "obj" || seg = "bin")

    isCs && not isGenerated && not inBuildDir

/// Human-readable note describing what the hook changed.
let buildAdditionalContext (r: FormatResult) : string =
    let name = Path.GetFileName r.Path

    let detail =
        if r.WholeFile then
            sprintf "reformatted %s" name
        else
            sprintf "reformatted %d changed region(s) in %s" r.Regions name

    sprintf
        "CSharpLintHook %s (whitespace/formatting only). The on-disk file now reflects the formatted version."
        detail

/// Serialize the postToolUse hook response carrying additionalContext.
let buildHookOutput (additionalContext: string) : string =
    let o = JsonObject()
    o["additionalContext"] <- JsonValue.Create additionalContext
    o.ToJsonString()
