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

/// The postToolUse hook: read the payload, format the touched C# file in place,
/// and emit additionalContext so the model knows the on-disk file was adjusted.
let hook: Program<unit> =
    program {
        let! input = readStdin

        match Payload.parse input with
        | None -> return ()
        | Some info ->
            if not info.Success then
                return ()
            else
                match info.FilePath with
                | None -> return ()
                | Some raw ->
                    let full = Payload.resolvePath info.Cwd raw

                    if not (Payload.isFormattableCSharp full) then
                        return ()
                    else
                        let! (r, wrote) = formatAndWrite full

                        if wrote then
                            do! writeStdout (Payload.buildHookOutput (Payload.buildAdditionalContext r))
                        else
                            return ()
    }
