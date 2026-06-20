# RoslynLspHook

A **Copilot CLI hook** that surfaces C# **compiler diagnostics** (errors/warnings)
for files the agent just edited, by talking to a running
[Roslyn language server](https://github.com/dotnet/roslyn) over a named pipe.

Where the sibling `CSharpLintHook` *fixes* formatting, `RoslynLspHook` *reports*
problems back to the agent, so it can correct them on the next turn. It is built
around a **long-lived broker**: one warm Roslyn server is owned by a broker daemon
that **hosts** the workspace's named pipe, and each short-lived `postToolUse` hook
is a thin **broker client** — it connects to the pipe, asks for the edited file's
diagnostics, and appends a concise summary to the tool result the model sees.

On `sessionStart` the hook **spawns the broker daemon** (`RoslynLsp`, a separate
executable) detached and returns at once, so it never blocks startup. It hands the
daemon the workspace path as an explicit argument. The daemon installs the
`roslyn-language-server` tool if missing and then *becomes* the broker: it launches
the Roslyn server as a `--stdio` child, drives the `initialize` handshake once, and
— when the workspace has exactly one `*.sln`/`*.slnx` — scopes it to that solution
via `solution/open`. Because the daemon is its own program it never re-enters the
hook's stdin path, so there is no re-entrancy and no spawn loop. If a `postToolUse`
ever finds the pipe down it respawns the daemon detached and skips that turn, never
waiting for the server to come up.

> **Copilot CLI 1.0.64 hook contract.** A `sessionStart` hook's stdout is discarded
> — only process side effects survive — so the server is started by spawning the
> separate `RoslynLsp` daemon, a detached process that outlives the hook (it can't
> emit context or auto-run a skill). `postToolUse` is different: the CLI ignores
> `additionalContext` but **does** read a returned `modifiedResult` and uses it in
> place of the original tool result. So this hook echoes the original result back
> with the diagnostics appended to `textResultForLlm` (preserving `resultType` and
> the other fields). The daemon traces its progress to
> `%TEMP%/roslyn-lsp-<pipe>-setup.log` (`/tmp` on Unix).

## State machine

The hook (`RoslynLspHook`) reads the payload from stdin and runs one of two flows;
the broker daemon (`RoslynLsp`) is a separate executable it spawns:

```
hook  ─┬─ SessionStart ─▶ StartingSession (check cwd) ─▶ SpawnSetup (detached `RoslynLsp` daemon) ─▶ done
       └─ PostToolUse  ─▶ ToolEdited ─▶ CheckingOpen (probe pipe) ─┬─ (down) ─▶ SpawnSetup (self-heal) ─▶ done
                                                                   └─ (up)   ─▶ CheckingFile ─▶ emit modifiedResult

daemon ─▶ ensureInstalled ─▶ runBroker ── becomes the broker (hosts pipe, opens sole sln) ─▶ blocks
```

`sessionStart` is fire-and-forget: the hook spawns the `RoslynLsp` daemon detached
(handing it the workspace cwd) and returns immediately, so it never blocks the
session. The daemon *becomes* the broker after installing the tool if needed. The
broker is single-instance — on startup it probes its own pipe and exits at once if
one is already hosting it — so a daemon spawned when one is already up is harmless.

## Architecture — two projects, free monad + interpreter

The code is split across two executables that share a namespace (`RoslynLspHook.*`):

* **`RoslynLsp`** — the broker **daemon** plus the shared protocol/process/client
  library every client links against. It owns all the LSP and named-pipe machinery.
* **`RoslynLspHook`** — the thin **hook** the plugin runs on each event. It reads the
  payload from stdin and drives a pure state machine; the heavy lifting is delegated
  to the daemon it spawns.

As in `CSharpLintHook`, pure programs describe *what* to do via an effect algebra; a
single interpreter performs the real effects at the **top layer**. All business logic
is therefore pure and testable against a stub interpreter.

| Project         | File             | Role                                                                       |
|-----------------|------------------|----------------------------------------------------------------------------|
| `RoslynLsp`     | `Common.fs`      | Domain types: `HookEvent`, `Severity`, `Diagnostic`, `LspConfig`, `HookState`. |
| `RoslynLsp`     | `Lsp.fs`         | Pure protocol: `Content-Length` framing, request/response JSON, broker request/reply encode + decode, diagnostic parse/format. |
| `RoslynLsp`     | `Wire.fs`        | The byte-level `Content-Length` read/write loop over any duplex stream, shared by the broker (its Roslyn child's stdio) and the broker client (the pipe). |
| `RoslynLsp`     | `LspProcess.fs`  | Interpreter backend: probe the pipe, install the tool, `ensureBroker`, spawn the detached `RoslynLsp` daemon. |
| `RoslynLsp`     | `LspClient.fs`   | A thin **broker client** — connect to the pipe, send one framed request, decode the reply. |
| `RoslynLsp`     | `Config.fs`      | Resolve `LspConfig` (pipe / command / wait) from env + cwd, so every executable derives the same pipe name. |
| `RoslynLsp`     | `Broker.fs`      | The long-lived **broker**: owns one warm `--stdio` Roslyn server, hosts the workspace pipe (single instance), pumps the server's stdout, and answers broker clients' diagnostic/open requests. |
| `RoslynLsp`     | `Program.fs`     | Daemon entry: take the cwd argument → `ensureInstalled` → `runBroker`.       |
| `RoslynLspHook` | `Effects.fs`     | `Program<'a>` free monad + smart constructors + `program { }` builder.      |
| `RoslynLspHook` | `Payload.fs`     | Decode `sessionStart` / `postToolUse` payloads (camelCase + VS Code) into typed records with Thoth.Json.Net; the event is whichever record decodes. |
| `RoslynLspHook` | `Logic.fs`       | Pure state machine (`drive`) + `hook` entry (read stdin → parse → drive).    |
| `RoslynLspHook` | `Interpreter.fs` | `runAsync` — the only module that performs IO.                              |
| `RoslynLspHook` | `Program.fs`     | Stdin-driven entry: run the `hook` program (no argv verbs).                 |

Effects in the algebra: `readStdin`, `writeStdout`, `logLine`, `dirExists`,
`probeLsp`, `launchLsp`, `fetchDiagnostics`, `openSolution`, `spawnSetup`.

## How the pipe gets hosted

Roslyn's own `--pipe <name>` makes the *server* a client that connects out to a pipe
its editor hosts — so the server never hosts a pipe itself. The **broker** closes
that gap: it launches the server as a `--stdio` child (driving it over the child's
redirected stdio) and **hosts the workspace's named pipe itself** with a
`NamedPipeServerStream`. Each `postToolUse` hook then connects to that pipe as a thin
broker client, asks for one file's diagnostics, and disconnects.

The pipe name is per-workspace (the sole `*.sln`/`*.slnx` base name, else a hash of
the cwd — see below), so each workspace gets its own broker and they never cross-wire.
The broker is single-instance: on startup it probes its own pipe and exits if one is
already hosting it.

If no broker is up, the probe fails and the hook silently no-ops — it never breaks the
agent loop. **Every** failure path (bad payload, broker down, protocol hiccup,
timeout) is swallowed and the process exits `0`.

## Configuration (environment variables)

| Variable              | Default                                                                          | Meaning                                  |
|-----------------------|----------------------------------------------------------------------------------|------------------------------------------|
| `ROSLYN_LSP_PIPE`     | sole `*.sln`/`*.slnx` base name, else `roslyn-lsp-<hash>` (hash of the cwd)       | Named pipe the broker hosts.             |
| `ROSLYN_LSP_WAIT_MS`  | `8000`                                                                            | Max time to wait for diagnostics, in ms. |
| `ROSLYN_LSP_COMMAND`  | `roslyn-language-server --stdio --autoLoadProjects --logLevel Information`        | Server command the broker drives over stdio. |

## Build & test

```bash
dotnet build -c Release RoslynLspHook/RoslynLspHook.fsproj
dotnet test  -c Release RoslynLspHook.Tests/RoslynLspHook.Tests.fsproj
```

Target framework `net10.0`, nullable reference types enabled (most nullness
warnings are errors). Dependencies are `FSharp.Core` and `Thoth.Json.Net` (which
brings in `Newtonsoft.Json`), used to decode the stdin payload into typed records.
The decoders are hand-written Thoth `Decoder`s over LINQ-to-JSON (no reflection-based
`Decode.Auto`), so the published Native-AOT binary parses both payload casings at
runtime — verified by publishing the AOT exe and exercising it against real
`sessionStart` / `postToolUse` payloads.

The hook ships as a **Native-AOT** executable (`build.cmd Plugin`). AOT cannot create
value-type generic instantiations at runtime, which the F# `printf`/`sprintf` family
relies on, so the source uses string interpolation (`$"…{x}…"`) instead — a stray
`sprintf "…%d…"` compiles fine but crashes the *published* binary at runtime
(`MakeGenericMethod is not compatible with AOT compilation`). The JIT-based tests
can't catch that; the packaged exe has to be exercised directly.

## CLI usage

The hook (`RoslynLspHook`) reads a hook JSON payload from **stdin** and ignores its
arguments; the event (`sessionStart` / `postToolUse`) is inferred from the payload
shape. The broker daemon (`RoslynLsp`) is a separate executable that takes the
workspace directory as its single argument, reads no stdin, installs the server tool
if missing, and *becomes* the broker.

The language server binary is the `roslyn-language-server` .NET global tool. The
daemon installs it automatically; to do it yourself:

```bash
dotnet tool install --global roslyn-language-server --prerelease
```

```bash
# sessionStart: the hook spawns the detached `RoslynLsp` daemon and exits at once. Its
# stdout is discarded by Copilot CLI, so all work happens as a process side effect.
echo '{"cwd":"'"$PWD"'","source":"startup"}' \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll

# the daemon directly: install the server tool if missing, become the broker, host the
# pipe, and open the sole .sln (or rely on --autoLoadProjects). Blocks until the server
# exits. Traced to $TMPDIR/roslyn-lsp-<pipe>-setup.log.
dotnet RoslynLsp/bin/Release/net10.0/RoslynLsp.dll "$PWD"

# postToolUse: lint the file the agent just edited and emit a modifiedResult that
# carries the diagnostics back to the model.
printf '{"cwd":"%s","toolName":"edit","toolResult":{"resultType":"success","textResultForLlm":"Edited src/Thing.cs"},"toolArgs":"{\\"path\\":\\"src/Thing.cs\\"}"}' "$PWD" \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll
```

On a `postToolUse` for a C# file with problems, it echoes the original tool result
back under `modifiedResult` with the report appended to `textResultForLlm`, e.g.:

```json
{"modifiedResult":{"resultType":"success","textResultForLlm":"Edited src/Thing.cs\n\nRoslynLspHook: 1 error(s), 0 warning(s) in Thing.cs (from the Roslyn language server):\n  L3:5 error CS1002: ; expected"}}
```

Only `Error` and `Warning` diagnostics are reported (info/hint are dropped), the
list is capped at 25 with a `(+N more)` note, and positions are rendered 1-based.
Generated files (`*.g.cs`, `*.Designer.cs`, …) and anything under `bin/`/`obj/` are
ignored.

> Wiring this as an actual Copilot hook (a `.github/hooks/*.json` entry) is left to
> the consumer; the program is a plain stdin/stdout filter so it slots into a
> command hook for `sessionStart` + `postToolUse` whenever you want it.
