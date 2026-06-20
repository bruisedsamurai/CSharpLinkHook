---
name: roslyn-start
description: Ensure the Roslyn language server (the roslyn-language-server .NET global tool) is running for the current C# workspace so RoslynLspHook can surface diagnostics. Installs the tool via dotnet if missing and starts the broker daemon (RoslynLsp), which scopes itself to the sole solution or auto-loads all projects. Idempotent and quiet when already running.
---

# roslyn-start

Make sure the Roslyn language server is alive and serving diagnostics for **this**
workspace, then get out of the way. Be quiet and idempotent: if it is already
running and scoped to the right solution, do nothing user-visible.

> **You usually don't need to run this manually.** The plugin's `sessionStart`
> hook already spawns the `RoslynLsp` broker daemon, which installs the server tool
> if missing, starts it, and opens the sole `*.sln` automatically (or relies on
> `--autoLoadProjects` when there are several or none). Run this skill to (re)start
> the daemon yourself — e.g. if the broker isn't up, or after it has exited.

## Locating the plugin and the workspace

- **Plugin directory** — the folder holding the bundled binaries. The broker daemon
  `RoslynLsp` (`RoslynLsp.exe` on Windows) lives there alongside the hook. It is two
  levels up from this skill's base directory (the "Base directory for this skill"
  shown in the skill context is `<plugin>/skills/roslyn-start`), so the daemon lives
  at `<plugin>/RoslynLsp`. Invoke it by that absolute path.
- **Workspace root** — your current working directory; scan it for solutions/projects.

## The server binary

The plugin runs `roslyn-language-server`, the Roslyn LSP distributed as a .NET global
tool, inside a long-lived **broker**: the broker (the `RoslynLsp` daemon) owns one
warm server (driven over `--stdio`) and hosts the workspace's named pipe, and each
`postToolUse` hook connects to that pipe as a thin client. The tool must be on `PATH`.
Launching the `RoslynLsp` daemon (below) ensures the broker is up — it is
single-instance, so it's a no-op when one is already running; if the tool is missing
the client stays silent. Both the `sessionStart` spawn and this skill install it on
demand (`dotnet tool install --global roslyn-language-server --prerelease`).

## Steps

1. **Ensure the server tool is installed.** If `roslyn-language-server` is not on
   `PATH` (e.g. `dotnet tool list --global` does not list it), install it:

   ```bash
   dotnet tool install --global roslyn-language-server --prerelease
   ```

   It needs the .NET 10 runtime. The tool lands in the dotnet global-tools directory
   (`~/.dotnet/tools`, or `%USERPROFILE%\.dotnet\tools` on Windows) — make sure that
   directory is on `PATH`, otherwise the launcher cannot find it.

2. **Find the solution (informational).** Look for `*.sln`/`*.slnx` under the
   workspace root. The daemon scopes itself automatically: exactly one → it opens
   that solution; several or none → it relies on `--autoLoadProjects`, which
   discovers the `*.csproj` projects on its own. There is no per-solution verb, so
   you don't need to choose one here.

3. **Start the broker daemon** by launching `RoslynLsp` with the workspace path as
   its argument. It is single-instance — it probes the workspace pipe first and exits
   immediately if a broker is already up — so this is a safe no-op when one is
   running. It installs the server tool if missing, then becomes the broker and
   blocks, so run it detached/in the background:

   ```bash
   "<plugin>/RoslynLsp" "<workspace root>" &
   ```

   On Windows, start it in the background instead of blocking the shell:

   ```powershell
   Start-Process -FilePath "<plugin>\RoslynLsp.exe" -ArgumentList "<workspace root>"
   ```

   `<plugin>` is the plugin directory described above (two levels up from this
   skill's base directory).

4. Report one short line on success (e.g. `Roslyn LSP ready for <solution>`). On
   failure, explain briefly; never block the session.

## Rules

- **Idempotent:** the daemon probes the pipe before starting, so it never launches a
  second broker; running it again when one is already up exits immediately. Running
  this skill again when everything is already up does no visible work.
- **Quiet:** no chatter when it is already running and scoped.
- **Never modify source code.** This skill only manages the language-server process.

## Configuration (optional)

The client honors these environment variables (see the project README):
`ROSLYN_LSP_PIPE` (pipe the broker hosts; defaults to the sole `*.sln`/`*.slnx` base
name, else `roslyn-lsp-<hash>` of the cwd), `ROSLYN_LSP_COMMAND` (the `--stdio` server
command the broker drives), and `ROSLYN_LSP_WAIT_MS`.
