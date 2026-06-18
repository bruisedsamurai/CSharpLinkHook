module RoslynLspHook.Payload

open System
open System.IO
open System.Text.Json
open RoslynLspHook.Common

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

let private tryProp (el: JsonElement) (name: string) : JsonElement option =
    if el.ValueKind = JsonValueKind.Object then
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    else
        None

let private tryString (el: JsonElement) (name: string) : string option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.String ->
        match v.GetString() with
        | null -> None
        | s -> Some s
    | _ -> None

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

let private cwdOf (root: JsonElement) : string =
    tryString root "cwd" |> Option.defaultWith Directory.GetCurrentDirectory

/// Did the tool succeed? Accept either the camelCase (`toolResult.resultType`)
/// or the VS Code-compatible (`tool_result.result_type`) payload shape.
let private toolSucceeded (root: JsonElement) : bool =
    let fromObj (objName: string) (field: string) =
        match tryProp root objName with
        | Some tr -> tryString tr field = Some "success"
        | None -> false

    fromObj "toolResult" "resultType" || fromObj "tool_result" "result_type"

/// First `pathKeys` string found directly on an object element.
let private pickPath (el: JsonElement) : string option =
    pathKeys |> List.tryPick (tryString el)

/// Extract the affected file path from a tool-args value. Copilot CLI delivers
/// it in one of two shapes: a JSON-encoded *string* (the real postToolUse shape,
/// `toolArgs`/`tool_input` arrives pre-serialized) or a JSON *object* (as the
/// `unknown` type allows). Handle both so the hook works against live payloads.
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
                    pickPath innerDoc.RootElement
                else
                    None
            with _ ->
                None
    | _ -> None

/// First file path found in either `toolArgs` (camelCase) or `tool_input` (VS Code).
let private toolFilePath (root: JsonElement) : string option =
    (tryProp root "toolArgs" |> Option.orElse (tryProp root "tool_input"))
    |> Option.bind pathFromArgs

/// Classify the event when no explicit hint was passed on the command line.
let private inferEvent (root: JsonElement) : HookEvent =
    match tryString root "hook_event_name" with
    | Some "SessionStart" -> SessionStart
    | Some "PostToolUse" -> PostToolUse
    | Some _ -> OtherEvent
    | None ->
        let hasTool =
            (tryProp root "toolResult").IsSome
            || (tryProp root "tool_result").IsSome
            || (tryProp root "toolArgs").IsSome
            || (tryProp root "tool_input").IsSome

        if hasTool then PostToolUse
        elif (tryProp root "source").IsSome then SessionStart
        else OtherEvent

/// The raw JSON of the original tool result object, so we can echo it back as
/// `modifiedResult` with our diagnostics appended. Accepts either the camelCase
/// (`toolResult`) or VS Code-compatible (`tool_result`) shape; `{}` when absent.
let private toolResultRaw (root: JsonElement) : string =
    match tryProp root "toolResult" |> Option.orElse (tryProp root "tool_result") with
    | Some tr -> tr.GetRawText()
    | None -> "{}"

let private parseToolUse (root: JsonElement) : Parsed =
    let cwd = cwdOf root

    let file =
        if not (toolSucceeded root) then
            None
        else
            match toolFilePath root with
            | None -> None
            | Some raw ->
                let full = resolvePath cwd raw
                if isLintableCSharp full then Some full else None

    DoToolUse(cwd, file, toolResultRaw root)

/// Parse a hook payload. `hint` is the event name passed as the program's first
/// argument (from the hook registration); when absent the event is inferred from
/// the payload shape. Pure: parsing performs no IO beyond a cwd fallback.
let parse (hint: HookEvent option) (input: string) : Parsed =
    try
        use doc = JsonDocument.Parse input
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Ignore
        else
            let event = hint |> Option.defaultWith (fun () -> inferEvent root)

            match event with
            | SessionStart -> DoSessionStart(cwdOf root)
            | PostToolUse -> parseToolUse root
            | OtherEvent -> Ignore
    with _ ->
        Ignore
