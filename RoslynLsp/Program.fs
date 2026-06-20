module RoslynLspHook.DaemonHost

open System
open System.IO

// The broker daemon as its own executable (`RoslynLsp`). The hook spawns this binary
// detached when it finds the workspace's broker is down; because it is a SEPARATE
// program it never re-enters the hook's stdin-driven entry point, so there is no
// re-entrancy and no risk of a spawn loop. It reads no stdin: the workspace cwd is
// passed EXPLICITLY as the first argument (`RoslynLsp <cwd>`), and it resolves the
// same pipe name the hook does via `Config.mkConfig`, so both connect to one broker.
//
// On startup it installs the `roslyn-language-server` tool if missing, then BECOMES
// the broker: `runBroker` blocks until the Roslyn child exits. The broker is
// single-instance (it probes its own pipe and exits at once if one is already
// hosting it), so a stray second daemon is harmless. Progress is traced to
// %TEMP%/roslyn-lsp-<pipe>-setup.log (/tmp on Unix).

[<EntryPoint>]
let main argv =
    // The daemon must never throw back at whoever spawned it: swallow and exit 0.
    try
        // cwd is supplied explicitly as argv[0]; fall back to the working directory.
        let cwd =
            match List.ofArray argv with
            | arg :: _ when not (String.IsNullOrWhiteSpace arg) -> arg
            | _ -> Directory.GetCurrentDirectory()

        let cfg = Config.mkConfig cwd
        LspProcess.logSetup cfg $"daemon started (cwd={cwd}, pipe={cfg.PipeName})"
        LspProcess.ensureInstalled cfg
        Broker.runBroker cfg
        LspProcess.logSetup cfg "daemon exiting"
    with _ ->
        ()

    0
