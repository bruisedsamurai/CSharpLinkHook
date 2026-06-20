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
    | StartingSession cwd ->
        program {
            let! ok = dirExists cwd

            if not ok then
                return ()
            else
                // The CLI discards a sessionStart hook's stdout (only its side effects
                // survive), so we can't ask the agent to run a skill from here. Instead
                // fire off a detached worker that installs/starts the server and opens
                // the solution in the background, and return at once.
                do! spawnSetup cfg
        }

    | StartingLsp pending ->
        program {
            do! launchLsp cfg

            match pending with
            | Some file -> return! drive cfg (CheckingFile(file, "{}"))
            | None -> return ()
        }

    | OpeningSolution path ->
        program {
            do! launchLsp cfg
            let! _ = openSolution cfg path
            return ()
        }

    | ToolEdited(None, _) -> Pure()
    | ToolEdited(Some file, toolResult) -> drive cfg (CheckingOpen(file, toolResult))

    | CheckingOpen(file, toolResult) ->
        program {
            let! isOpen = probeLsp cfg.PipeName

            if not isOpen then
                // The broker isn't up yet (cold session, or it died). Kick off a
                // detached setup worker to (re)start it so the *next* edit has
                // diagnostics, then return silently — we never block or annotate
                // this edit on a missing broker.
                do! spawnSetup cfg
                return ()
            else
                return! drive cfg (CheckingFile(file, toolResult))
        }

    | CheckingFile(file, toolResult) ->
        program {
            let! diags = fetchDiagnostics cfg file

            match Lsp.formatDiagnostics file diags with
            | None -> return ()
            | Some report -> do! writeStdout (Lsp.buildModifiedResult toolResult report)
        }

/// Top-level hook program: read the stdin payload, decode it into a typed
/// `Payload.HookPayload` model (via Thoth.Json.Net) — the payload's own shape
/// selects the event — and drive the state machine off that model, most notably
/// its `Cwd`. `mkConfig` turns the payload's cwd into a full `LspConfig` (built
/// from the environment at the composition root, so this stays pure).
let hook (mkConfig: string -> LspConfig) : Program<unit> =
    program {
        let! input = readStdin

        match Payload.decode input with
        | Payload.SessionStartEvent payload -> return! drive (mkConfig payload.Cwd) (StartingSession payload.Cwd)
        | Payload.PostToolUseEvent payload ->
            return! drive (mkConfig payload.Cwd) (ToolEdited(Payload.lintTarget payload, payload.ToolResultRaw))
        | Payload.IgnoredEvent -> return ()
    }
