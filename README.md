# roslyn-lsp-hook

A **GitHub Copilot CLI plugin** that wires two C# hooks into your agent sessions:

- **CSharpLintHook** — diff-aware Roslyn formatting that tidies only the regions
  of a file the agent just edited. See [`CSharpLintHook/README.md`](CSharpLintHook/README.md).
- **RoslynLspHook** — surfaces C# compiler diagnostics for edited files by
  appending them to the tool result (`modifiedResult`). See [`RoslynLspHook/README.md`](RoslynLspHook/README.md).

The build packages both tools, the hook wiring, and the `roslyn-start` skill into
a single installable plugin folder.

## Prerequisites

- **.NET 10 SDK** (pinned in [`global.json`](global.json)).
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
│   └── roslyn-start/SKILL.md   # starts the Roslyn language server
├── RoslynLspHook               # native AOT client binary
├── CSharpLintHook.dll          # framework-dependent formatter (run via dotnet)
└── *.dll, *.json               # CSharpLintHook runtime dependencies
```

`dist/` (and `publish/`) are git-ignored build output.

## VS Code agent-plugin variant

`Plugin` also assembles a VS Code-compatible copy next to the CLI folder:

```
dist/roslyn-lsp-hook-vscode/
├── .claude-plugin/
│   └── plugin.json             # manifest (Claude plugin format; name unchanged)
├── hooks/
│   └── hooks.json              # PascalCase events, single `command`, ${CLAUDE_PLUGIN_ROOT}
├── skills/
│   └── roslyn-start/SKILL.md
├── RoslynLspHook               # same binaries as the CLI folder
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

Point the Copilot CLI at the produced folder to store it as an installed plugin:

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
  `CSharpLintHook hook read` (matched to `bash|grep|view|powershell`) appends a line
  (`additionalContext`) naming the RoslynLspMcp MCP methods —
  `get_class_constructors_and_properties`, `get_class_methods`,
  `get_namespace_declarations` — whenever one of those tools touched a `*.cs` file,
  so the model knows it can use them to learn more about C# symbols. The methods are
  named directly because the MCP server can be registered under any name in a user's
  config. `RoslynLspHook` then reports any compiler diagnostics for edited C# back to
  the agent by appending them to the tool result (`modifiedResult`). One binary picks
  the flow from its argument (`hook format` vs `hook read`); the matcher keeps each
  flow scoped to the right tools, so reading a `.cs` file never reformats it.

> **Copilot CLI 1.0.64 note.** A `sessionStart` hook's stdout is discarded by the
> CLI, so the server is started as a process side effect (the detached worker)
> rather than by emitting context or auto-running a skill. The same release also
> ignores `additionalContext` returned from `postToolUse`, so `RoslynLspHook`
> instead returns a `modifiedResult` that echoes the original tool result with the
> diagnostics appended to `textResultForLlm`. (`CSharpLintHook`'s format flow still
> emits `additionalContext`; its reformatting is a file side effect that works
> regardless, so only its informational note is dropped. The read flow is *only* an
> `additionalContext` note, so until the CLI honours it that flow has no visible
> effect — a minor follow-up.)

The language server itself is the `roslyn-language-server` .NET global tool; the
`roslyn-start` skill installs it on demand with:

```bash
dotnet tool install --global roslyn-language-server --prerelease
```
