# RoslynLspHook

A **Copilot CLI hook** that surfaces C# **compiler diagnostics** (errors/warnings)
for files the agent just edited, by talking to a running
[Roslyn language server](https://github.com/dotnet/roslyn) over a named pipe.

Where the sibling `CSharpLintHook` *fixes* formatting, `RoslynLspHook` *reports*
problems back to the agent via `additionalContext`, so it can correct them on the
next turn. It is the LSP **client**: a persistent server is started once (on
`sessionStart`), and each `postToolUse` connects to the pipe, opens the edited
file, pulls diagnostics, and prints a concise summary.

## State machine

```
Dispatch ─┬─ SessionStart ─▶ StartingSession (check cwd) ─▶ StartingLsp (launch detached) ─▶ done
          └─ PostToolUse  ─▶ ToolEdited ─▶ CheckingOpen (probe pipe) ─┬─ (closed)  ─▶ done
                                                                      └─ CheckingFile ─▶ emit context
```

`StartingLsp` is fire-and-forget: it launches the server in the background
(idempotent — it first probes the pipe and skips launching if one is already up)
and returns immediately, so `sessionStart` never blocks the session.

## Architecture — free monad + interpreter

As in `CSharpLintHook`, pure programs describe *what* to do via an effect algebra;
a single interpreter performs the real effects at the **top layer**. All business
logic is therefore pure and testable against a stub interpreter.

| File             | Role                                                                       |
|------------------|----------------------------------------------------------------------------|
| `Common.fs`      | Domain types: `HookEvent`, `Severity`, `Diagnostic`, `LspConfig`, `HookState`. |
| `Effects.fs`     | `Program<'a>` free monad + smart constructors + `program { }` builder.      |
| `Payload.fs`     | Pure parse of `sessionStart` / `postToolUse` payloads (camelCase + VS Code). |
| `Lsp.fs`         | Pure protocol: `Content-Length` framing, request JSON, diagnostic parse/format. |
| `Logic.fs`       | Pure state machine (`drive`) + `hook` entry.                                |
| `LspProcess.fs`  | Interpreter backend: probe the pipe, start the server detached.             |
| `LspClient.fs`   | Interpreter backend: connect, handshake, `didOpen`, collect diagnostics.    |
| `Interpreter.fs` | `runAsync` — the only module that performs IO.                              |
| `Program.fs`     | Env config + argv dispatch (`sessionStart` / `postToolUse`).                |

Effects in the algebra: `readStdin`, `writeStdout`, `logLine`, `dirExists`,
`probeLsp`, `launchLsp`, `fetchDiagnostics`.

## Requirement: a pipe the hook can connect to

Roslyn's own `--pipe <name>` makes the server a *client* that connects to a pipe
the editor hosts. This hook assumes you run a server that **hosts a connectable
endpoint** on a stable pipe name, e.g.:

```bash
roslyn-language-server \
  --pipe my-roslyn-lsp \
  --autoLoadProjects \
  --logLevel Information
```

If nothing hosts the pipe, the probe fails and the hook silently no-ops — it never
breaks the agent loop. **Every** failure path (bad payload, server down, protocol
hiccup, timeout) is swallowed and the process exits `0`.

## Configuration (environment variables)

| Variable              | Default                                                                          | Meaning                                  |
|-----------------------|----------------------------------------------------------------------------------|------------------------------------------|
| `ROSLYN_LSP_PIPE`     | `my-roslyn-lsp`                                                                   | Named pipe the server hosts.             |
| `ROSLYN_LSP_WAIT_MS`  | `8000`                                                                            | Max time to wait for diagnostics, in ms. |
| `ROSLYN_LSP_COMMAND`  | `roslyn-language-server --pipe <pipe> --autoLoadProjects --logLevel Information`  | Background launch command.               |

## Build & test

```bash
dotnet build -c Release RoslynLspHook/RoslynLspHook.fsproj
dotnet test  -c Release RoslynLspHook.Tests/RoslynLspHook.Tests.fsproj
```

Target framework `net10.0`, nullable reference types enabled (most nullness
warnings are errors). Only dependency is `FSharp.Core`.

## CLI usage

The program reads a hook JSON payload from **stdin**; the first argument names the
event (`sessionStart` or `postToolUse`). When the argument is omitted, the event is
inferred from the payload shape.

```bash
# sessionStart: start the language server in the background.
echo '{"cwd":"'"$PWD"'","source":"startup"}' \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll sessionStart

# postToolUse: lint the file the agent just edited and print additionalContext.
printf '{"cwd":"%s","toolName":"edit","toolResult":{"resultType":"success"},"toolArgs":"{\\"path\\":\\"src/Thing.cs\\"}"}' "$PWD" \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll postToolUse
```

On a `postToolUse` for a C# file with problems, it emits a compact report, e.g.:

```json
{"additionalContext":"RoslynLspHook: 1 error(s), 0 warning(s) in Thing.cs (from the Roslyn language server):\n  L3:5 error CS1002: ; expected"}
```

Only `Error` and `Warning` diagnostics are reported (info/hint are dropped), the
list is capped at 25 with a `(+N more)` note, and positions are rendered 1-based.
Generated files (`*.g.cs`, `*.Designer.cs`, …) and anything under `bin/`/`obj/` are
ignored.

> Wiring this as an actual Copilot hook (a `.github/hooks/*.json` entry) is left to
> the consumer; the program is a plain stdin/stdout filter so it slots into a
> command hook for `sessionStart` + `postToolUse` whenever you want it.
