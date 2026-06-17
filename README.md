# roslyn-lsp-hook

A **GitHub Copilot CLI plugin** that wires two C# hooks into your agent sessions:

- **CSharpLintHook** — diff-aware Roslyn formatting that tidies only the regions
  of a file the agent just edited. See [`CSharpLintHook/README.md`](CSharpLintHook/README.md).
- **RoslynLspHook** — surfaces C# compiler diagnostics for edited files as
  `additionalContext`. See [`RoslynLspHook/README.md`](RoslynLspHook/README.md).

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
./build.sh Plugin       # assemble the plugin into dist/roslyn-lsp-hook
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

## Install the plugin

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

- **`sessionStart`** runs the `roslyn-start` skill to ensure a Roslyn language
  server is up for the workspace (idempotent and quiet when already running).
- **`postToolUse`** runs `CSharpLintHook` to reformat the changed regions of the
  edited C# file, then `RoslynLspHook` to report any compiler diagnostics back to
  the agent.
