# roslyn-lsp-hook

A **GitHub Copilot CLI plugin** that wires C# and structural-code hooks into your agent sessions:

- **CSharpLintHook** — diff-aware Roslyn formatting that tidies only the regions
  of a file the agent just edited. See [`CSharpLintHook/README.md`](CSharpLintHook/README.md).
- **RoslynLspHook** — surfaces C# compiler diagnostics for edited files by
  appending them to the tool result (`modifiedResult`). See [`RoslynLspHook/README.md`](RoslynLspHook/README.md).
- **ast-grep outline hook** — appends `ast-grep outline <path>` context when the
  agent reads or searches a file or folder.

The build packages these tools, the hook wiring, and the `roslyn-start` /
`ast-grep` skills into a single installable plugin folder.

## Prerequisites

- **.NET 10 SDK** (pinned in [`global.json`](global.json)).
- **Node.js + npm** at runtime — the ast-grep outline hook installs/updates
  `@ast-grep/cli` globally (`npm i -g @ast-grep/cli`) each time it runs.
- **GitHub Copilot CLI** (`copilot`) to install and run the plugin.

## Build the plugin folder

The repo ships a FAKE build driven by wrapper scripts that run from the repo root
no matter where they are invoked.

```bash
# macOS / Linux
./build.sh Plugin       # assemble the plugin into dist/roslyn-lsp-hook (+ -vscode)
./build.sh              # Default: Clean + Test + Plugin
```

```cmd
:: Windows
build.cmd Plugin
build.cmd
```

The `Plugin` target publishes both binaries and assembles them with the manifest,
hooks, and skill. The `Default` target (no argument) additionally cleans and runs
the test suite first.

`RoslynLspHook` is published as a Native AOT single binary, so the plugin is
**platform-specific**. The runtime defaults to the host; override it to package for
another platform:

```bash
RID=linux-x64 ./build.sh Plugin     # also e.g. osx-arm64, win-x64
```

## Where the plugin folder is produced

The build writes the assembled plugin to:

```
dist/roslyn-lsp-hook/
├── plugin.json                 # manifest
├── hooks.json                  # sessionStart + postToolUse wiring
├── skills/
│   ├── roslyn-start/SKILL.md   # starts the Roslyn language server
│   └── ast-grep/               # fetched from ast-grep/agent-skill
├── RoslynLspHook               # native AOT client binary
├── AstGrepOutline              # native AOT postToolUse outline hook
├── CSharpLintHook.dll          # framework-dependent formatter (run via dotnet)
└── *.dll, *.json               # CSharpLintHook runtime dependencies
```

`publish/` is git-ignored intermediate output. `dist/` is committed so the repo
can act as a Copilot CLI plugin marketplace.

## VS Code agent-plugin variant

`Plugin` also assembles a VS Code-compatible copy next to the CLI folder:

```
dist/roslyn-lsp-hook-vscode/
├── .claude-plugin/
│   └── plugin.json             # manifest (Claude plugin format; name unchanged)
├── hooks/
│   └── hooks.json              # PascalCase events, single `command`, ${CLAUDE_PLUGIN_ROOT}
├── skills/
│   ├── roslyn-start/SKILL.md
│   └── ast-grep/
├── RoslynLspHook               # same binaries as the CLI folder
├── AstGrepOutline
└── CSharpLintHook.dll, *.dll
```

VS Code loads plugin hooks in Claude Code format, which differs from the Copilot
CLI schema: event keys are PascalCase, each entry uses a single `command` (not
split `bash`/`powershell`), and the plugin-root token is `${CLAUDE_PLUGIN_ROOT}`.
The build translates `plugin/hooks.json` and `plugin/plugin.json` into this
layout, choosing the `command` variant for the build's target OS — so this
folder is platform-specific just like the binaries.

### Install the VS Code variant

For a plugin you installed with the Copilot CLI, VS Code auto-detects it under
`~/.copilot/installed-plugins/` — nothing to do. To use this **locally built**
folder instead, register it directly:

1. Make sure agent plugins are enabled — set `chat.plugins.enabled` to `true` in
   your VS Code settings.

2. Add the absolute path of the built folder to `chat.pluginLocations` (`true`
   enables it):

   ```json
   // settings.json
   "chat.pluginLocations": {
       "C:\\Users\\you\\Projects\\CSharpLinkHook\\dist\\roslyn-lsp-hook-vscode": true
   }
   ```

3. Reload the window (**Developer: Reload Window**). The plugin appears in the
   Extensions view under **Agent Plugins - Installed** (search `@agentPlugins`),
   and its `roslyn-start` skill shows in **Chat: Configure Skills**.

Because the setting points at the folder, a later `build.cmd Plugin` updates the
plugin in place — just reload the window to pick up new binaries (no reinstall).

Alternatively, to install from a Git repository rather than a local build, run
**Chat: Install Plugin From Source** and pass the repo URL.

## Install the plugin (Copilot CLI)

This repo includes a marketplace named `TokenSaver` at
`.github/plugin/marketplace.json`. Add it and install the packaged plugin from
the committed `dist/roslyn-lsp-hook` folder:

```bash
copilot plugin marketplace add bruisedsamurai/CSharpLinkHook
copilot plugin install roslyn-lsp-hook@TokenSaver
```

For local development, point the Copilot CLI at the produced folder to store it
as an installed plugin:

```bash
copilot plugin install ./dist/roslyn-lsp-hook
```

Verify it loaded:

```bash
copilot plugin list
```

The CLI **caches** a plugin's components on install. After rebuilding, install
again to pick up the new binaries:

```bash
./build.sh Plugin && copilot plugin install ./dist/roslyn-lsp-hook
```

To remove it, uninstall by the plugin's manifest `name` (not the path):

```bash
copilot plugin uninstall roslyn-lsp-hook
```

## What the plugin does once installed

- **`sessionStart`** runs `RoslynLspHook sessionStart`, which spawns a **detached
  background worker** (`RoslynLspHook setup`) and returns immediately, so it never
  blocks the session. The worker installs the `roslyn-language-server` tool if it
  is missing, starts the server for the workspace (idempotent — it never launches a
  second one), and — when the workspace has exactly one `*.sln` — scopes the server
  to it. Progress is written to `%TEMP%/roslyn-lsp-<pipe>-setup.log`
  (`/tmp` on Unix). For a workspace with **several** solutions, run the
  `roslyn-start` skill to pick one.
- **`postToolUse`** wires three entries. `CSharpLintHook hook format` (matched to
  `edit|create`) reformats the changed regions of the edited C# file in place.
  The ast-grep outline hook (matched to `grep|view`) installs/updates
  `@ast-grep/cli` via `npm i -g`, then runs `ast-grep outline <path>` for file targets
  and folder targets, then appends the
  outline as `additionalContext`. `RoslynLspHook` then reports any compiler diagnostics for edited C# back to
  the agent by appending them to the tool result (`modifiedResult`). The matcher keeps each
  flow scoped to the right tools, so reading a `.cs` file never reformats it.

> **Copilot CLI 1.0.64 note.** A `sessionStart` hook's stdout is discarded by the
> CLI, so the server is started as a process side effect (the detached worker)
> rather than by emitting context or auto-running a skill. The same release also
> ignores `additionalContext` returned from `postToolUse`, so `RoslynLspHook`
> instead returns a `modifiedResult` that echoes the original tool result with the
> diagnostics appended to `textResultForLlm`. (`CSharpLintHook`'s format flow still
> emits `additionalContext`; its reformatting is a file side effect that works
> regardless, so only its informational note is dropped. The ast-grep outline hook also returns
> `additionalContext`, so it is subject to the same CLI behavior.)

The language server itself is the `roslyn-language-server` .NET global tool; the
`roslyn-start` skill installs it on demand with:

```bash
dotnet tool install --global roslyn-language-server --prerelease
```
