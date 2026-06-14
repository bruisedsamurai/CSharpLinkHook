module RoslynLspHook.Program

open System
open RoslynLspHook.Common

/// `roslyn-language-server --pipe <name> --autoLoadProjects --logLevel Information`
/// is the documented launch command; the pipe name is woven in so the probe and
/// the server agree on the endpoint.
let private defaultCommand (pipeName: string) : string list =
    [ "roslyn-language-server"
      "--pipe"
      pipeName
      "--autoLoadProjects"
      "--logLevel"
      "Information" ]

let private envOr (name: string) (fallback: string) : string =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> fallback
    | v -> v

let private command (pipeName: string) : string list =
    match Environment.GetEnvironmentVariable "ROSLYN_LSP_COMMAND" with
    | null
    | "" -> defaultCommand pipeName
    | raw -> raw.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private waitMs () : int =
    match Int32.TryParse(envOr "ROSLYN_LSP_WAIT_MS" "8000") with
    | true, n when n > 0 -> n
    | _ -> 8000

/// Map the optional event argument (passed by the hook registration) to an event.
let private eventOfArg (s: string) : HookEvent option =
    match s.ToLowerInvariant() with
    | "sessionstart" -> Some SessionStart
    | "posttooluse" -> Some PostToolUse
    | _ -> None

[<EntryPoint>]
let main argv =
    let pipeName = envOr "ROSLYN_LSP_PIPE" "my-roslyn-lsp"
    let cmd = command pipeName
    let wait = waitMs ()

    let mkConfig (cwd: string) : LspConfig =
        { PipeName = pipeName
          Cwd = cwd
          Command = cmd
          WaitMs = wait }

    let hint =
        match List.ofArray argv with
        | a :: _ -> eventOfArg a
        | [] -> None

    // Hooks must never break the agent loop: swallow everything and exit 0.
    try
        Interpreter.run (Logic.hook hint mkConfig)
    with _ ->
        ()

    0
