module RoslynLspHook.Program

// The hook is a thin stdin filter. Copilot CLI (and VS Code) deliver the hook
// event as a JSON payload on stdin; `Logic.hook` reads that payload, decides the
// event, and drives the state machine. There are NO argv verbs any more: the
// broker daemon is now a separate executable (`RoslynLsp`) that the state machine
// spawns, so this entry point only ever runs the hook itself and can never
// re-enter as the daemon. The per-workspace config (pipe name, server command,
// wait budget) is built by `Config.mkConfig` in the shared `RoslynLsp` project, so
// the hook and the daemon derive the SAME pipe name and talk to one broker.

[<EntryPoint>]
let main _argv =
    // Hooks must never break the agent loop: swallow everything and exit 0. argv is
    // ignored; everything the hook needs comes from the stdin payload.
    try
        Interpreter.run (Logic.hook None Config.mkConfig)
    with _ ->
        ()

    0
