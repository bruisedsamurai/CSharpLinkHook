module RoslynLspHook.Config

open System
open System.IO
open RoslynLspHook.Common

// Resolve the full `LspConfig` for a workspace cwd from environment variables. This
// lives in the shared project so every executable that talks to the broker — the
// hook (`RoslynLspHook`), the daemon (`RoslynLsp`), and any future client — derives
// the SAME pipe name for a given workspace and therefore connects to the same broker.

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
        match LspProcess.soleSolution cwd with
        | Some sln ->
            match Path.GetFileNameWithoutExtension sln with
            | null
            | "" -> uniquePipe cwd
            | name -> sanitizePipe name
        | None -> uniquePipe cwd
    | v -> v

/// Build the full config for a workspace cwd: resolve the pipe, then the launch
/// command (which embeds that pipe) and the diagnostics wait budget.
let mkConfig (cwd: string) : LspConfig =
    { PipeName = resolvePipe cwd
      Cwd = cwd
      Command = command ()
      WaitMs = waitMs () }
