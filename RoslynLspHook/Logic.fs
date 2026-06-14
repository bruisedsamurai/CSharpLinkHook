module RoslynLspHook.Logic

open RoslynLspHook.Common
open RoslynLspHook.Effects

/// The hook's control flow as a tiny state machine. Each transition is a pure
/// `Program`; the interpreter supplies the real effects (probe/launch/fetch), so
/// the whole machine is exercisable against a stub interpreter in tests.
///
/// SessionStart ─▶ StartingSession ─▶ StartingLsp ─▶ done
/// PostToolUse  ─▶ ToolEdited ─▶ CheckingOpen ─┬─ (closed) ─▶ done
///                                             └─ CheckingFile ─▶ emit context
let rec drive (cfg: LspConfig) (state: HookState) : Program<unit> =
    match state with
    | Dispatch -> Pure() // resolved in `hook` before driving; never reached here.

    | StartingSession cwd ->
        program {
            let! ok = dirExists cwd

            if not ok then
                return ()
            else
                return! drive cfg (StartingLsp None)
        }

    | StartingLsp pending ->
        program {
            do! launchLsp cfg

            match pending with
            | Some file -> return! drive cfg (CheckingFile file)
            | None -> return ()
        }

    | ToolEdited None -> Pure()
    | ToolEdited(Some file) -> drive cfg (CheckingOpen file)

    | CheckingOpen file ->
        program {
            let! isOpen = probeLsp cfg.PipeName

            if not isOpen then
                return ()
            else
                return! drive cfg (CheckingFile file)
        }

    | CheckingFile file ->
        program {
            let! diags = fetchDiagnostics cfg file

            match Lsp.formatDiagnostics file diags with
            | None -> return ()
            | Some ctx -> do! writeStdout (Lsp.buildHookOutput ctx)
        }

/// Top-level hook program: read the payload, decide the event, and drive the
/// state machine. `mkConfig` turns the payload's cwd into a full `LspConfig`
/// (built from environment at the composition root, so this stays pure).
let hook (hint: HookEvent option) (mkConfig: string -> LspConfig) : Program<unit> =
    program {
        let! input = readStdin

        match Payload.parse hint input with
        | Payload.DoSessionStart cwd -> return! drive (mkConfig cwd) (StartingSession cwd)
        | Payload.DoToolUse(cwd, fileOpt) -> return! drive (mkConfig cwd) (ToolEdited fileOpt)
        | Payload.Ignore -> return ()
    }
