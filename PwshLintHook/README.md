# PwshLintHook

A **`preToolUse`** command hook for the GitHub Copilot CLI that inspects the
`powershell` tool's command, parses it with the real PowerShell AST parser
(`System.Management.Automation.Language.Parser`), and **denies** commands that should
use a faster tool — telling the agent what to run instead.

## What it denies

| Pattern | Trigger | Suggested replacement |
| ------- | ------- | --------------------- |
| Filesystem search | `Get-ChildItem` (aliases `gci`/`ls`/`dir`) on the filesystem | the **`fd`** CLI, with an AST-derived equivalent command |
| Content search | `Select-String` (alias `sls`), or `Get-Content` (`gc`/`cat`/`type`) piped into a filter | the **`fff`** MCP |

Content search takes precedence: `gci -r *.cs \| sls TODO` is a content search, so it
is routed to `fff`, not `fd`.

For a filesystem search the deny reason carries the mapped command, e.g.

```
Get-ChildItem $sdk -Recurse -File -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\(obj|bin)\' } |
  Select-Object FullName
        ⇩
fd -t f -e cs -E obj -E bin . "$sdk"
```

Before denying a `Get-ChildItem` command the hook ensures `fd` is installed, running
`npm install -g fd-find` when it is missing from `PATH`.

## Safe by default

- **Allow-by-default.** Anything that is not a filesystem/content search — and any
  error — writes nothing and exits 0. `preToolUse` command hooks are fail-closed on a
  non-zero exit, so every path swallows errors and exits 0; only an explicit deny
  object on stdout blocks a command.
- **Non-filesystem providers are left alone.** `Get-ChildItem Env:`, `HKLM:\…`,
  `Cert:\…`, etc. can't be served by `fd`/`fff`, so they are never denied.
- **Plain reads are left alone.** `Get-Content app.config` (no filter) is not a
  content search.

## Design

Pure logic behind a thin IO shell, so the analysis is fully unit-tested:

- `Payload.fs` — Thoth.Json.Net decoders for the `preToolUse` payload (camelCase and VS
  Code snake_case); extracts `toolName` + the `command`. Encodes the deny response.
- `Pipeline.fs` — wraps the PowerShell parser and reduces every pipeline to a pure,
  AST-free `Stage list list` (alias-resolved name, category, switches, value-params,
  positionals, raw text).
- `Analysis.fs` — matches the model against the two patterns and builds the deny
  decision (including the `fd` command mapping).
- `Program.fs` — reads stdin, runs the analysis, ensures `fd`, writes the deny JSON.

Run it directly:

```bash
echo '{"toolName":"powershell","toolArgs":{"command":"Get-ChildItem -Recurse"}}' \
  | dotnet PwshLintHook.dll hook tool-guard
```

Because it depends on `System.Management.Automation` (not AOT-safe), it is published
framework-dependent and invoked via `dotnet PwshLintHook.dll`, shipped in its own
plugin subfolder so its dependency closure stays isolated.
