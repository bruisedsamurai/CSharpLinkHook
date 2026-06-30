module CSharpLintHook.Logic

open CSharpLintHook.Common
open CSharpLintHook.Effects

/// Run the appropriate formatter for a file's diff classification, yielding the
/// formatted text, the number of regions touched, and whether it was whole-file.
let private runFormat (original: string) (diff: DiffResult) : Program<string * int * bool> =
    match diff with
    | FormatWhole -> formatWhole original |> map (fun f -> (f, 0, true))
    | Changed [] -> Pure(original, 0, false)
    | Changed ranges -> formatRanges original ranges |> map (fun (f, n) -> (f, n, false))

/// Pure program: read a file, classify it against git, and compute its formatted
/// form. Does not write anything.
let computeFormat (path: string) : Program<FormatResult> =
    program {
        let! exists = fileExists path

        if not exists then
            return
                { Path = path
                  Found = false
                  Original = ""
                  Formatted = ""
                  Regions = 0
                  WholeFile = false }
        else
            let! original = readFile path
            let! diff = classifyDiff path
            let! (formatted, regions, wholeFile) = runFormat original diff

            return
                { Path = path
                  Found = true
                  Original = original
                  Formatted = formatted
                  Regions = regions
                  WholeFile = wholeFile }
    }

/// Compute the formatted form and write it back when it changed. Returns the
/// result and whether a write occurred.
let formatAndWrite (path: string) : Program<FormatResult * bool> =
    program {
        let! r = computeFormat path

        if r.IsChanged then
            do! writeFile r.Path r.Formatted
            return (r, true)
        else
            return (r, false)
    }

/// The resolved supported source file a successful *edit* touched, or None when
/// there is nothing to act on: the tool failed, named no file, or named one we
/// skip (unsupported extension, generated, or under bin/obj). The format flow
/// needs the exact path, so this reads a known toolArgs key.
let private touchedSourceFile (info: Payload.PostToolUse.PostToolUse) : string option =
    if not (Payload.PostToolUse.isSuccess info) then
        None
    else
        Payload.PostToolUse.filePath info
        |> Option.map (Payload.resolvePath info.Cwd)
        |> Option.filter Payload.isFormattableCSharp

/// The postToolUse *format* flow (wired to edit/create via the hooks.json matcher and
/// selected by the `hook format` arg): when an edit touched a supported source file,
/// reformat it in place and emit additionalContext so the model knows the on-disk
/// file was adjusted.
let hookFormat: Program<unit> =
    program {
        let! input = readStdin

        match Payload.PostToolUse.decode input |> Option.bind touchedSourceFile with
        | None -> return ()
        | Some full ->
            let! r, wrote = formatAndWrite full

            if wrote then
                do! writeStdout (Payload.buildHookOutput (Payload.buildAdditionalContext r))
            else
                return ()
    }

/// The preToolUse *commit-guard* flow (wired to bash/powershell via the hooks.json
/// matcher and selected by the `hook commit-guard` arg): when the command about to run
/// carries the Copilot co-author trailer, deny it and ask the agent to remove itself as
/// co-author; otherwise emit nothing, so the command proceeds untouched. Scanning the raw
/// payload is enough because the marker only ever appears inside the command text.
let commitGuard: Program<unit> =
    program {
        let! input = readStdin

        if Payload.containsCopilotCoauthor input then
            do! writeStdout (Payload.buildCoauthorDenyOutput ())
        else
            return ()
    }
