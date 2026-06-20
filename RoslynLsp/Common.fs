module RoslynLspHook.Common

/// Which Copilot lifecycle event invoked the hook. The hook is registered for
/// `sessionStart` (start the language server) and `postToolUse` (lint the file
/// the agent just wrote); anything else is ignored.
type HookEvent =
    | SessionStart
    | PostToolUse
    | OtherEvent

/// LSP diagnostic severity (mirrors the LSP spec's numeric `severity`).
type Severity =
    | Error
    | Warning
    | Information
    | Hint

/// A single diagnostic the language server reported for a file. Positions are
/// 0-based (as in LSP); they are rendered 1-based for humans when formatted.
type Diagnostic =
    { Severity: Severity
      Line: int
      Character: int
      Message: string
      Code: string option
      Source: string option }

/// Resolved configuration for locating and talking to the Roslyn LSP. Built once
/// at the composition root from environment variables plus the payload's cwd.
type LspConfig =
    {
      /// Named pipe the persistent language server hosts (e.g. "my-roslyn-lsp").
      PipeName: string
      /// Working directory reported by the hook payload (the workspace root).
      Cwd: string
      /// Launch command + args used to start the server in the background.
      Command: string list
      /// Upper bound (ms) on how long a single diagnostics fetch may wait.
      WaitMs: int }

/// States of the hook's control flow. The hook's `main` parses the stdin payload
/// and enters at `StartingSession` (sessionStart) or `ToolEdited` (postToolUse);
/// from there the flow is a tiny explicit state machine that maps 1:1 onto the
/// documented design:
///
///   StartingSession ─▶ SpawnSetup (detached `RoslynLsp` daemon) ─▶ (done)
///   ToolEdited ─▶ CheckingOpen ─┬─ (not open) ─▶ SpawnSetup (self-heal) ─▶ done
///                               └─ CheckingFile ─▶ emit modifiedResult
type HookState =
    /// SessionStart: validate the cwd, then start the LSP.
    | StartingSession of cwd: string
    /// (Post)ToolUse carrying the resolved C# file to check (None ⇒ nothing to do)
    /// plus the raw JSON of the tool's original result, which we echo back (with
    /// our diagnostics appended) as `modifiedResult`.
    | ToolEdited of file: string option * toolResult: string
    /// StartLSP: launch the server in the background; optionally continue to a file.
    | StartingLsp of pending: string option
    /// CheckLSPOpen: probe the pipe; exit if closed, else check the file.
    | CheckingOpen of file: string * toolResult: string
    /// CheckFile: fetch diagnostics for the file and return them to the agent.
    | CheckingFile of file: string * toolResult: string
    /// OpenSolution: scope the running server to a chosen .sln via `solution/open`.
    | OpeningSolution of path: string
