---
name: roslyn-start
description: Ensure the Roslyn language server is running for the current C# workspace so RoslynLspHook can surface diagnostics. Detects the solution/project, installs the server via dotnet if needed, starts it on the conventional named pipe, and remembers the choice. Idempotent and quiet when already running.
---

# roslyn-start

Make sure a Roslyn language server is alive and serving diagnostics for **this**
workspace, then get out of the way. Be quiet and idempotent: if it is already
running, do nothing and say nothing.

## Environment provided to the plugin

- `COPILOT_PLUGIN_ROOT` — this plugin's directory. The bundled binaries live
  directly here: the client is `$COPILOT_PLUGIN_ROOT/RoslynLspHook` and the
  formatter is `$COPILOT_PLUGIN_ROOT/CSharpLintHook.dll`.
- `COPILOT_PLUGIN_DATA` — a writable directory for this plugin. Persist the
  chosen solution here (e.g. `$COPILOT_PLUGIN_DATA/selected-solution`).
- `COPILOT_PROJECT_DIR` — the workspace root to scan for solutions/projects.

## Steps

1. **Probe first.** Run `"$COPILOT_PLUGIN_ROOT/RoslynLspHook" sessionStart`.
   If the pipe is already up this is a fast no-op — stop here and stay silent.
2. **Find the solution.** Look for `*.sln` under the workspace root.
   - Exactly one → use it.
   - Several → **ask the user** which one to use (list them). Do not guess.
   - None → fall back to the nearest enclosing `*.csproj`.
3. **Persist** the chosen path to `$COPILOT_PLUGIN_DATA/selected-solution` so
   later runs and resumed sessions skip the prompt.
4. **Ensure the server is installed.** If `Microsoft.CodeAnalysis.LanguageServer`
   is not present, install it via `dotnet` (restore the RID-specific package from
   Microsoft's `vs-impl` feed). See the project README for the exact feed and
   pinned version.
5. **Start** the server on the conventional pipe for this workspace and leave it
   running so `postToolUse` can pull diagnostics.
6. Report one short line on success (e.g. `Roslyn LSP ready for <solution>`). On
   failure, explain briefly; never block the session.

## Rules

- **Idempotent:** probe before doing anything; never start a second server.
- **Quiet:** no chatter when it is already running.
- **Never modify source code.** This skill only manages the language-server
  process.
