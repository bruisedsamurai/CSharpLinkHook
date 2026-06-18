---
name: roslyn-start
description: Ensure the Roslyn language server (the roslyn-language-server .NET global tool) is running for the current C# workspace and scoped to its solution so RoslynLspHook can surface diagnostics. Installs the tool via dotnet if missing, picks the solution (asks when several), and loads it. Idempotent and quiet when already running.
---

# roslyn-start

Make sure the Roslyn language server is alive and serving diagnostics for **this**
workspace, then get out of the way. Be quiet and idempotent: if it is already
running and scoped to the right solution, do nothing user-visible.

> **You usually don't need to run this manually.** The plugin's `sessionStart`
> hook already spawns a detached background worker that installs the server tool
> if missing, starts it, and opens the sole `*.sln` automatically. Run this skill
> when you want to (re)scope the server yourself — most importantly when the
> workspace has **several** `*.sln` files and you must choose one (the automatic
> worker only auto-opens when there is exactly one).

## Locating the plugin and the workspace

- **Plugin directory** — the folder holding the bundled client binary
  `RoslynLspHook` (`RoslynLspHook.exe` on Windows). It is two levels up from this
  skill's base directory (the "Base directory for this skill" shown in the skill
  context is `<plugin>/skills/roslyn-start`), so the binary lives at
  `<plugin>/RoslynLspHook`. Invoke it by that absolute path.
- **Workspace root** — your current working directory; scan it for solutions/projects.

## The server binary

The plugin runs `roslyn-language-server`, the Roslyn LSP distributed as a .NET global
tool, inside a long-lived **broker**: the broker owns one warm server (driven over
`--stdio`) and hosts the workspace's named pipe, and each `postToolUse` hook connects
to that pipe as a thin client. The tool must be on `PATH`. The `RoslynLspHook` verbs
below ensure the broker is up (starting it if needed); if the tool is missing the
client stays silent. Both the `sessionStart` background worker and this skill install
it on demand (`dotnet tool install --global roslyn-language-server --prerelease`).

## Steps

1. **Ensure the server tool is installed.** If `roslyn-language-server` is not on
   `PATH` (e.g. `dotnet tool list --global` does not list it), install it:

   ```bash
   dotnet tool install --global roslyn-language-server --prerelease
   ```

   It needs the .NET 10 runtime. The tool lands in the dotnet global-tools directory
   (`~/.dotnet/tools`, or `%USERPROFILE%\.dotnet\tools` on Windows) — make sure that
   directory is on `PATH`, otherwise the launcher cannot find it.

2. **Find the solution.** Look for `*.sln`/`*.slnx` under the workspace root.
   - Exactly one → use it.
   - Several → **ask the user** which one to use (list them). Do not guess.
   - None → there is nothing to scope; the server's `--autoLoadProjects` discovers
     the `*.csproj` projects on its own.

3. **Start and scope the server** with the bundled client. It ensures the broker is
   up (hosting the workspace pipe) if it isn't already, then loads the solution:

   - With a chosen solution:

     ```bash
     "<plugin>/RoslynLspHook" open-solution "<chosen .sln>"
     ```

   - With no solution (project-only workspace):

     ```bash
     "<plugin>/RoslynLspHook" start
     ```

   `<plugin>` is the plugin directory described above (two levels up from this
   skill's base directory). On Windows use `"<plugin>\RoslynLspHook.exe"`.

4. Report one short line on success (e.g. `Roslyn LSP ready for <solution>`). On
   failure, explain briefly; never block the session.

## Rules

- **Idempotent:** the client probes the pipe before starting, so it never launches a
  second broker; re-opening the same solution is a safe no-op. Running this skill again
  when everything is already up does no visible work.
- **Quiet:** no chatter when it is already running and scoped.
- **Never modify source code.** This skill only manages the language-server process.

## Configuration (optional)

The client honors these environment variables (see the project README):
`ROSLYN_LSP_PIPE` (pipe the broker hosts; defaults to the sole `*.sln`/`*.slnx` base
name, else `roslyn-lsp-<hash>` of the cwd), `ROSLYN_LSP_COMMAND` (the `--stdio` server
command the broker drives), and `ROSLYN_LSP_WAIT_MS`.
