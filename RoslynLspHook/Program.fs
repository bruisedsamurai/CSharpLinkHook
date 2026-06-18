module RoslynLspHook.Program

open System
open System.IO
open RoslynLspHook.Common

/// `roslyn-language-server --stdio --autoLoadProjects --logLevel Information` is the
/// command the broker spawns. The broker drives it over the child's redirected
/// stdio (and appends `--clientProcessId`), so there is no `--pipe` here — the
/// broker is what hosts the pipe.
let private defaultCommand: string list =
    [ "roslyn-language-server"
      "--stdio"
      "--autoLoadProjects"
      "--logLevel"
      "Information" ]

let private envOr (name: string) (fallback: string) : string =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> fallback
    | v -> v

let private command () : string list =
    match Environment.GetEnvironmentVariable "ROSLYN_LSP_COMMAND" with
    | null
    | "" -> defaultCommand
    | raw -> raw.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private waitMs () : int =
    match Int32.TryParse(envOr "ROSLYN_LSP_WAIT_MS" "8000") with
    | true, n when n > 0 -> n
    | _ -> 8000

let private defaultPipe = "my-roslyn-lsp"

/// The sole top-level `*.sln`/`*.slnx` in `cwd`. Delegates to `LspProcess` so
/// every flow — pipe naming, the broker, the setup worker — agrees on one answer.
let private soleSolution (cwd: string) : string option = LspProcess.soleSolution cwd

/// Keep only characters valid in a named-pipe identifier; empty ⇒ default.
let private sanitizePipe (raw: string) : string =
    let cleaned =
        raw
        |> Seq.filter (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')
        |> Seq.toArray
        |> String

    if cleaned.Length = 0 then defaultPipe else cleaned

/// A workspace-unique pipe name derived from a hash of the absolute cwd, used when
/// there is no sole solution to name the pipe after. This keeps two different
/// workspaces (each with several or no solutions) from cross-wiring to the same
/// broker, which a shared constant default would allow. Best-effort: any failure
/// falls back to the shared default.
let private uniquePipe (cwd: string) : string =
    try
        let full = Path.GetFullPath(cwd).ToLowerInvariant()
        let bytes = System.Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes full)

        let hex =
            bytes |> Array.take 4 |> Array.map (fun b -> b.ToString "x2") |> String.concat ""

        "roslyn-lsp-" + hex
    with _ ->
        defaultPipe

/// The pipe a workspace's broker hosts. Precedence: explicit `ROSLYN_LSP_PIPE`
/// override → the base name of the sole top-level `*.sln`/`*.slnx` (so `aks_dirs.sln`
/// ⇒ `aks_dirs`) → a workspace-unique hash of the cwd. Best-effort throughout.
let private resolvePipe (cwd: string) : string =
    match Environment.GetEnvironmentVariable "ROSLYN_LSP_PIPE" with
    | null
    | "" ->
        match soleSolution cwd with
        | Some sln ->
            match Path.GetFileNameWithoutExtension sln with
            | null
            | "" -> uniquePipe cwd
            | name -> sanitizePipe name
        | None -> uniquePipe cwd
    | v -> v

/// Map the optional event argument (passed by the hook registration) to an event.
let private eventOfArg (s: string) : HookEvent option =
    match s.ToLowerInvariant() with
    | "sessionstart" -> Some SessionStart
    | "posttooluse" -> Some PostToolUse
    | _ -> None

/// Build the full config for a workspace cwd: resolve the pipe, then the launch
/// command (which embeds that pipe) and the diagnostics wait budget.
let private mkConfig (cwd: string) : LspConfig =
    { PipeName = resolvePipe cwd
      Cwd = cwd
      Command = command ()
      WaitMs = waitMs () }

[<EntryPoint>]
let main argv =
    // Hooks must never break the agent loop: swallow everything and exit 0.
    try
        match List.ofArray argv with
        | "open-solution" :: pathArg :: _ ->
            // Scope the running server to a chosen solution. The workspace pipe is
            // resolved from the current directory (the same root the hooks see), so
            // open-solution targets the same server the diagnostics hook talks to.
            let abs = Path.GetFullPath pathArg
            let cwd = Directory.GetCurrentDirectory()
            Interpreter.run (Logic.drive (mkConfig cwd) (OpeningSolution abs))
        | "start" :: _ ->
            // Ensure the server is running without installing anything. Used by the
            // roslyn-start skill (e.g. when there is no solution to open).
            let cwd = Directory.GetCurrentDirectory()
            Interpreter.run (Logic.drive (mkConfig cwd) (StartingLsp None))
        | "setup" :: _ ->
            // The detached background worker the sessionStart hook launches. It is
            // free to be slow: install the server tool if missing, then BECOME the
            // long-lived broker (runBroker blocks until the Roslyn child exits). The
            // broker hosts the pipe and scopes itself to the sole solution. Progress
            // is written to %TEMP%/roslyn-lsp-<pipe>-setup.log.
            let cwd = Directory.GetCurrentDirectory()
            let cfg = mkConfig cwd
            LspProcess.logSetup cfg $"setup worker started (cwd={cwd}, pipe={cfg.PipeName})"
            LspProcess.ensureInstalled cfg
            Broker.runBroker cfg
            LspProcess.logSetup cfg "setup worker done"
        | "broker" :: _ ->
            // Run the broker directly (no install step). Used by the integration
            // test and for manual runs against an already-installed server.
            let cwd = Directory.GetCurrentDirectory()
            Broker.runBroker (mkConfig cwd)
        | args ->
            let hint =
                match args with
                | a :: _ -> eventOfArg a
                | [] -> None

            Interpreter.run (Logic.hook hint mkConfig)
    with _ ->
        ()

    0
