module PwshLintHook.Program

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

// preToolUse command hook (wired to the `powershell` tool). It reads the payload from
// stdin, parses the command with the PowerShell AST parser, and DENIES it when it
// should use a faster tool:
//   * Get-ChildItem (filesystem search)          → deny, use the `fd` CLI
//   * Get-Content / Select-String (content search) → deny, use the `fff` MCP
// Before denying a Get-ChildItem command it makes sure `fd` is installed (so the
// suggestion is actionable), installing it via `npm install -g fd-find` when missing.
//
// Allow-by-default: anything else, or any error, writes nothing and exits 0. preToolUse
// command hooks are fail-closed on a non-zero exit, so every path swallows errors and
// exits 0 — only an explicit deny object on stdout blocks a command.

let private isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
let private fdExeName = if isWindows then "fd.exe" else "fd"

/// Whether `fd` resolves on PATH already.
let private fdOnPath () : bool =
    match Environment.GetEnvironmentVariable "PATH" with
    | null -> false
    | pathVar ->
        pathVar.Split(Path.PathSeparator)
        |> Array.exists (fun dir ->
            not (String.IsNullOrWhiteSpace dir) && File.Exists(Path.Combine(dir, fdExeName)))

/// Build a redirected `ProcessStartInfo` for `npm <args>`. npm ships as `npm.cmd` on
/// Windows, which can't be launched directly, so it is routed through `cmd.exe /c`.
let private npmStartInfo (args: string list) : ProcessStartInfo =
    let psi =
        if isWindows then
            let p = ProcessStartInfo "cmd.exe"
            p.ArgumentList.Add "/c"
            p.ArgumentList.Add "npm"
            p
        else
            ProcessStartInfo "npm"

    for a in args do
        psi.ArgumentList.Add a

    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.CreateNoWindow <- true
    psi

/// Ensure `fd` is available, installing it via `npm install -g fd-find` when missing.
/// Best-effort and time-bounded (the preToolUse budget is ~30s and times out fail-open):
/// a missing npm, no network, or a slow install is swallowed.
let private ensureFdInstalled () : unit =
    if not (fdOnPath ()) then
        try
            use p = new Process(StartInfo = npmStartInfo [ "install"; "--global"; "fd-find" ])
            p.Start() |> ignore
            p.StandardOutput.ReadToEndAsync() |> ignore
            p.StandardError.ReadToEndAsync() |> ignore

            if not (p.WaitForExit 20000) then
                (try p.Kill true with _ -> ())
        with _ ->
            ()

/// Inspect the command and emit a deny decision when it matches a replacement pattern.
let private guard (input: string) : unit =
    match Payload.decode input with
    | None -> ()
    | Some info ->
        if not (Payload.isShellTool info.ToolName) then
            ()
        else
            match info.Command with
            | None -> ()
            | Some command ->
                match Analysis.analyze command with
                | Analysis.Allow -> ()
                | Analysis.DenyFd fdCommand ->
                    ensureFdInstalled ()
                    Console.Out.Write(Payload.buildDenyOutput (Analysis.fdReason fdCommand))
                | Analysis.DenyFf reason -> Console.Out.Write(Payload.buildDenyOutput reason)

let private usage () =
    eprintfn "PwshLintHook — preToolUse PowerShell pipeline guard"
    eprintfn ""
    eprintfn "Usage:"
    eprintfn "  pwshlinthook hook tool-guard   Read a preToolUse payload from stdin and deny a"
    eprintfn "                                 PowerShell command that should use fd / the fff MCP."

[<EntryPoint>]
let main argv =
    // Hooks must never break the agent loop: swallow everything and exit 0.
    try
        match List.ofArray argv with
        | []
        | [ "hook" ]
        | [ "hook"; "tool-guard" ] -> guard (Console.In.ReadToEnd())
        | _ -> usage ()
    with _ ->
        ()

    0
