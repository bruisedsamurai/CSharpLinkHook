module AstGrepOutline.Program

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

// AOT-published postToolUse hook. It reads the hook payload from stdin, and when a
// `view`/`grep` tool has just succeeded against existing files/folders, runs
// `ast-grep outline <target>` for each (capped, deduped) target and emits the combined
// outline back as `additionalContext` JSON on stdout. It is a direct port of the
// former `plugin/scripts/ast_grep_outline.py`, so all the behavioural rules — which
// tools qualify, path extraction, resolution, dedupe, the 5-target cap, the 10s
// per-target timeout, and the context size cap — live in `Payload` and mirror it
// exactly. Like every hook it must never break the agent loop: all failures are
// swallowed and it exits 0 with no output.

let private exeName =
    if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
        "ast-grep.exe"
    else
        "ast-grep"

/// Search PATH for the ast-grep executable, mirroring `shutil.which`.
let private whichAstGrep () : string option =
    match Environment.GetEnvironmentVariable "PATH" with
    | null -> None
    | pathVar ->
        pathVar.Split(Path.PathSeparator)
        |> Array.tryPick (fun dir ->
            if String.IsNullOrWhiteSpace dir then
                None
            else
                let candidate = Path.Combine(dir, exeName)
                if File.Exists candidate then Some candidate else None)

/// Prefer the binary bundled beside this executable (the plugin root for the
/// single-file AOT build), otherwise fall back to PATH.
let private resolveBinary () : string option =
    let bundled = Path.Combine(AppContext.BaseDirectory, exeName)
    if File.Exists bundled then Some bundled else whichAstGrep ()

let private targetExists (path: string) : bool =
    File.Exists path || Directory.Exists path

/// Run `<binary> outline <target>` from `cwd`. Returns the trimmed stdout on a clean
/// exit, or None on nonzero exit, a 10s timeout, or any launch failure.
let private runOutline (binary: string) (cwd: string) (target: string) : string option =
    try
        let psi =
            ProcessStartInfo(
                binary,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = cwd
            )

        psi.ArgumentList.Add "outline"
        psi.ArgumentList.Add target

        use p = new Process(StartInfo = psi)
        p.Start() |> ignore
        let outTask = p.StandardOutput.ReadToEndAsync()
        p.StandardError.ReadToEndAsync() |> ignore

        if not (p.WaitForExit 10000) then
            (try p.Kill true with _ -> ())
            None
        elif p.ExitCode <> 0 then
            None
        else
            let outline = outTask.Result.Trim()
            if outline.Length > 0 then Some outline else None
    with _ ->
        None

/// Decode the payload, apply the tool/result guards, then resolve, filter, dedupe and
/// cap targets, outline each, and combine the sections into the context payload.
let private run (input: string) : string option =
    match Payload.decode input with
    | None -> None
    | Some payload ->
        if (payload.ToolName <> "view" && payload.ToolName <> "grep") then
            None
        elif payload.ResultType <> Some "success" then
            None
        else
            let cwd =
                match payload.Cwd with
                | Some c when not (String.IsNullOrWhiteSpace c) -> Path.GetFullPath c
                | _ -> Path.GetFullPath(Directory.GetCurrentDirectory())

            let rawPaths =
                if payload.ToolName = "grep" && List.isEmpty payload.RawPaths then
                    [ cwd ]
                else
                    payload.RawPaths

            let seen = HashSet<string>(Payload.pathComparer)

            let targets =
                rawPaths
                |> List.map (Payload.resolvePath cwd)
                |> List.filter targetExists
                |> List.filter seen.Add
                |> List.truncate Payload.maxTargets

            if List.isEmpty targets then
                None
            else
                match resolveBinary () with
                | None -> None
                | Some binary ->
                    let sections =
                        targets
                        |> List.choose (fun target ->
                            runOutline binary cwd target
                            |> Option.map (Payload.buildSection target))

                    if List.isEmpty sections then
                        None
                    else
                        sections |> String.concat "\n\n" |> Payload.truncateContext |> Some

[<EntryPoint>]
let main _argv =
    // Hooks must never break the agent loop: swallow everything and exit 0.
    try
        let input = Console.In.ReadToEnd()

        match run input with
        | Some context -> Console.Out.Write(Payload.encodeOutput context)
        | None -> ()
    with _ ->
        ()

    0
