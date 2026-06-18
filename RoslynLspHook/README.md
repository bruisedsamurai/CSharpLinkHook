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

On `sessionStart` the hook **spawns a detached background worker** (`RoslynLspHook
setup`) and returns at once, so it never blocks startup. That worker installs the
`roslyn-language-server` tool if missing and then *becomes* the broker: it launches
the Roslyn server as a `--stdio` child, drives the `initialize` handshake once, and
— when the workspace has exactly one `*.sln`/`*.slnx` — scopes it to that solution
via `solution/open`. The `start` and `open-solution` verbs let the `roslyn-start`
skill (re)start or (re)scope the broker idempotently, e.g. to choose among several
solutions. If a `postToolUse` ever finds the pipe down it respawns the broker
detached and skips that turn, never waiting for the server to come up.

> **Copilot CLI 1.0.64 hook contract.** A `sessionStart` hook's stdout is discarded
> — only process side effects survive — so the server is started by the hook itself,
> in a detached process that outlives it (it can't emit context or auto-run a skill).
> `postToolUse` is different: the CLI ignores `additionalContext` but **does** read a
> returned `modifiedResult` and uses it in place of the original tool result. So this
> hook echoes the original result back with the diagnostics appended to
> `textResultForLlm` (preserving `resultType` and the other fields). The setup worker
> traces its progress to `%TEMP%/roslyn-lsp-<pipe>-setup.log` (`/tmp` on Unix).

## State machine

```
Dispatch ─┬─ SessionStart ──▶ StartingSession (check cwd) ─▶ SpawnSetup (detached `setup` worker) ─▶ done
          ├─ setup ─────────▶ ensureInstalled ─▶ runBroker ── becomes the broker (hosts pipe, opens sole sln) ─▶ blocks
          ├─ broker ────────▶ runBroker ── becomes the broker (hosts pipe, opens sole sln) ─▶ blocks
          ├─ open-solution ─▶ OpeningSolution (ensureBroker, then solution/open) ─▶ done
          ├─ start ─────────▶ StartingLsp (ensureBroker) ─▶ done
          └─ PostToolUse  ──▶ ToolEdited ─▶ CheckingOpen (probe pipe) ─┬─ (down) ─▶ SpawnSetup (self-heal) ─▶ done
                                                                       └─ (up)   ─▶ CheckingFile ─▶ emit modifiedResult
```

`sessionStart` is fire-and-forget: it re-spawns this same executable as a detached
`setup` worker and returns immediately, so it never blocks the session. That worker
*becomes* the broker (after installing the tool if needed); the `start` /
`open-solution` verbs call `ensureBroker`, which only spawns a new broker when the
pipe isn't already hosted, so a broker that is already up is never duplicated.

## Architecture — free monad + interpreter

As in `CSharpLintHook`, pure programs describe *what* to do via an effect algebra;
a single interpreter performs the real effects at the **top layer**. All business
logic is therefore pure and testable against a stub interpreter.

| File             | Role                                                                       |
|------------------|----------------------------------------------------------------------------|
| `Common.fs`      | Domain types: `HookEvent`, `Severity`, `Diagnostic`, `LspConfig`, `HookState`. |
| `Effects.fs`     | `Program<'a>` free monad + smart constructors + `program { }` builder.      |
| `Payload.fs`     | Pure parse of `sessionStart` / `postToolUse` payloads (camelCase + VS Code). |
| `Lsp.fs`         | Pure protocol: `Content-Length` framing, request/response JSON, broker request/reply encode + decode, diagnostic parse/format. |
| `Wire.fs`        | The byte-level `Content-Length` read/write loop over any duplex stream, shared by the broker (its Roslyn child's stdio) and the broker client (the pipe). |
| `Logic.fs`       | Pure state machine (`drive`) + `hook` entry.                                |
| `LspProcess.fs`  | Interpreter backend: probe the pipe, install the tool, `ensureBroker`, self-spawn the detached `setup` worker. |
| `Broker.fs`      | The long-lived **broker daemon**: owns one warm `--stdio` Roslyn server, hosts the workspace pipe (single instance), pumps the server's stdout, and answers broker clients' diagnostic/open requests. |
| `LspClient.fs`   | Interpreter backend: a thin **broker client** — connect to the pipe, send one framed request, decode the reply. |
| `Interpreter.fs` | `runAsync` — the only module that performs IO.                              |
| `Program.fs`     | Env config + argv dispatch (`sessionStart` / `postToolUse` / `setup` / `broker` / `open-solution` / `start`). |

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
warnings are errors). Only dependency is `FSharp.Core`.

The hook ships as a **Native-AOT** executable (`build.cmd Plugin`). AOT cannot create
value-type generic instantiations at runtime, which the F# `printf`/`sprintf` family
relies on, so the source uses string interpolation (`$"…{x}…"`) instead — a stray
`sprintf "…%d…"` compiles fine but crashes the *published* binary at runtime
(`MakeGenericMethod is not compatible with AOT compilation`). The JIT-based tests
can't catch that; the packaged exe has to be exercised directly.

## CLI usage

The program reads a hook JSON payload from **stdin** for the event hooks; the first
argument names the verb. Hook verbs (`sessionStart`, `postToolUse`) consume stdin;
when omitted the event is inferred from the payload shape. The non-hook verbs
(`setup`, `broker`, `open-solution <path>`, `start`) take no stdin and act on the
current directory. `setup` is the detached worker `sessionStart` launches: it installs
the server tool if missing and then *becomes* the broker. `broker` runs the broker
directly without the install step (used by the integration test and manual runs).

The language server binary is the `roslyn-language-server` .NET global tool. The
`setup` worker installs it automatically; to do it yourself:

```bash
dotnet tool install --global roslyn-language-server --prerelease
```

```bash
# sessionStart: spawn the detached `setup` worker and exit immediately. Its stdout
# is discarded by Copilot CLI, so all work happens as a process side effect.
echo '{"cwd":"'"$PWD"'","source":"startup"}' \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll sessionStart

# setup: install the server tool if missing, become the broker, and open the sole
# .sln (or rely on --autoLoadProjects). Traced to $TMPDIR/roslyn-lsp-<pipe>-setup.log.
dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll setup

# broker: become the broker directly (no install step) — host the pipe and own the
# warm server. Blocks until the server exits.
dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll broker

# open-solution: ensure the server is up and scope it to a solution (used by the skill).
dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll open-solution "$PWD/MySolution.sln"

# start: ensure the server is up for a project-only workspace (no .sln).
dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll start

# postToolUse: lint the file the agent just edited and emit a modifiedResult that
# carries the diagnostics back to the model.
printf '{"cwd":"%s","toolName":"edit","toolResult":{"resultType":"success","textResultForLlm":"Edited src/Thing.cs"},"toolArgs":"{\\"path\\":\\"src/Thing.cs\\"}"}' "$PWD" \
  | dotnet RoslynLspHook/bin/Release/net10.0/RoslynLspHook.dll postToolUse
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
