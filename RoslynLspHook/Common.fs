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

/// States of the hook's control flow. The flow is intentionally a tiny explicit
/// state machine so it maps 1:1 onto the documented design:
///
///   Dispatch ─┬─ SessionStart ─▶ StartingSession ─▶ StartingLsp ─▶ (done)
///             └─ PostToolUse  ─▶ ToolEdited ─▶ CheckingOpen ─┬─ (not open) ─▶ done
///                                                            └─ CheckingFile ─▶ emit
type HookState =
    /// Pure/Empty: decide which event we are handling.
    | Dispatch
    /// SessionStart: validate the cwd, then start the LSP.
    | StartingSession of cwd: string
    /// (Post)ToolUse carrying the resolved C# file to check (None ⇒ nothing to do).
    | ToolEdited of file: string option
    /// StartLSP: launch the server in the background; optionally continue to a file.
    | StartingLsp of pending: string option
    /// CheckLSPOpen: probe the pipe; exit if closed, else check the file.
    | CheckingOpen of file: string
    /// CheckFile: fetch diagnostics for the file and return them to the agent.
    | CheckingFile of file: string
