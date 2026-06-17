# CSharpLintHook

A **diff-aware C# formatter** written in F# on the Roslyn APIs. It reformats only
the regions of a C# file that **changed** relative to the git base, so it can run
as a fast Copilot CLI `postToolUse` hook that tidies up code the agent just edited
— without reflowing the rest of the file.

## Pipeline

```
old file / git base
   -> git diff --unified=0 HEAD        (changed line ranges in the new file)
   -> Roslyn parse new file
   -> expand each changed line to its nearest "safe" node
        (statement -> member declaration -> type declaration)
   -> de-duplicate node spans (disjoint spans kept separate)
   -> Formatter.FormatAsync(document, spans)
   -> write file
```

New / untracked files (no git base) are formatted whole.

## Architecture — free monad + interpreter

Pure programs describe *what* to do via an effect algebra; a single interpreter
performs the real effects at the **top layer**. All business logic is therefore
pure and testable (swap in a stub interpreter).

| File             | Role                                                                 |
|------------------|----------------------------------------------------------------------|
| `Common.fs`      | Domain types: `LineRange`, `DiffResult`, `FormatResult`.             |
| `Effects.fs`     | `Program<'a>` free monad, smart constructors, `program { }` builder. |
| `Payload.fs`     | Pure parse/build of the postToolUse JSON payload.                    |
| `Logic.fs`       | Pure programs: `hook`, `computeFormat`, `formatAndWrite`.            |
| `Git.fs`         | Interpreter backend: `git diff --unified=0 HEAD` -> changed ranges.  |
| `Formatting.fs`  | Interpreter backend: node selection + `Formatter.FormatAsync`.       |
| `Interpreter.fs` | `runAsync` — the only module that performs IO.                       |
| `Program.fs`     | argv dispatch (`hook` / `format`).                                   |

Effects in the algebra: `readStdin`, `writeStdout`, `readFile`, `writeFile`,
`fileExists`, `classifyDiff`, `formatWhole`, `formatRanges`, `logLine`.

## Build

```bash
cd CSharpLintHook
dotnet build -c Release
```

Packages: `Microsoft.CodeAnalysis.CSharp.Workspaces` (5.3.0) and `FSharp.Core`
(10.1.301). Target framework `net10.0`, nullable reference types enabled (most
nullness warnings are errors).

## Tests

```bash
dotnet test            # from the repo root (runs CSharpLintHook.Tests)
```

`CSharpLintHook.Tests` (xUnit, F#) is an **integration** suite: it drives the real
pipeline end to end against throwaway git repos in a temp directory (real `git`
binary + real Roslyn formatter), so it guards behaviour rather than mocks.

| Area                       | What it pins down                                                            |
|----------------------------|-----------------------------------------------------------------------------|
| `PayloadTests`             | payload parsing for string-encoded **and** object `toolArgs`, camelCase + VS Code snake_case, every path key, `.cs`/generated/`bin`/`obj` guards |
| `DiffAwareTests`           | only changed regions formatted; untouched code kept byte-for-byte; whole-file for untracked; idempotence; **two files** formatted independently |
| `SymlinkRegressionTests`   | reaching a repo through a symlink stays diff-aware (guards the `Git.fs` fix) |
| `HookTests`                | hook mode writes in place + emits `additionalContext` for the real string-encoded, object, and snake_case payloads; no-ops on failure / non-`.cs` / malformed payloads |


## CLI usage

```bash
# Hook mode (default): read a postToolUse JSON payload from stdin, format the
# changed C# file in place, and emit additionalContext describing the change.
dotnet CSharpLintHook/bin/Release/net10.0/CSharpLintHook.dll hook < payload.json

# Standalone formatting of a single file:
dotnet ...CSharpLintHook.dll format path/to/File.cs --stdout   # print result (default)
dotnet ...CSharpLintHook.dll format path/to/File.cs --write    # rewrite in place if changed
dotnet ...CSharpLintHook.dll format path/to/File.cs --check    # exit 1 if it would change
```

The `format` command works on any file the diff classifier can read; the hook
path additionally restricts to `*.cs` and skips generated files and `bin`/`obj`.

## postToolUse hook

`.github/hooks/postToolUse.json` runs the formatter in `hook` mode. The command is
**guarded** — it is a no-op until a Release build exists, so it never errors:

```jsonc
"bash": "test -f CSharpLintHook/bin/Release/net10.0/CSharpLintHook.dll && dotnet CSharpLintHook/bin/Release/net10.0/CSharpLintHook.dll hook || true"
```

Run `dotnet build -c Release` to activate it. On success the hook writes
`{ "additionalContext": "CSharpLintHook reformatted N changed region(s) in <file> ..." }`
so the model knows the on-disk file was adjusted.

### Payload shapes

The hook reads the `postToolUse` JSON payload from stdin and pulls the edited
file out of the tool arguments. Copilot CLI delivers those arguments in more than
one shape, and `Payload.parse` accepts all of them:

- **`toolArgs` as a JSON-encoded string** — the shape the live CLI actually
  sends, e.g. `"toolArgs": "{\"path\":\"C.cs\",\"file_text\":\"...\"}"`. The inner
  JSON is parsed before the path is extracted.
- **`toolArgs` as an object** — e.g. `"toolArgs": { "path": "C.cs" }` (the
  spec types it as `unknown`).
- **VS Code compatible snake_case** — `tool_name` / `tool_input` /
  `tool_result.result_type`, used when the event is configured as `PostToolUse`.

The file path is taken from the first matching key of `path`, `file_path`,
`filePath`, `filename`, `file`, or `target_file`. The hook only acts when the
tool result was a success and the path is a real `.cs` file (not generated, not
under `bin/`/`obj/`).

> A discovery helper, `.github/hooks/capture_post_tool_use.py`, remains in the
> repo. It logs raw payloads (re-register it in `postToolUse.json` via
> `uv run` if you need to inspect the exact `toolArgs` for a tool). Its
> `postToolUse.log` output is transient and should not be committed.

## Manual test recipe

```bash
# A temp repo where method A changes but method B does not:
mkdir /tmp/cshooktest && cd /tmp/cshooktest && git init -q
git config user.email t@t.t && git config user.name t
printf 'class C\n{\n    void A()\n    {\nint x=1;\n    }\n    void B()\n    {\nint y=2;\n    }\n}\n' > C.cs
git add C.cs && git commit -qm base
printf 'class C\n{\n    void A()\n    {\nint x=1;int z=3;\n    }\n    void B()\n    {\nint y=2;\n    }\n}\n' > C.cs

dotnet <path>/CSharpLintHook.dll format /tmp/cshooktest/C.cs --stdout
# Expect: method A's body reindented/normalized; method B left byte-for-byte messy
# (it was unchanged vs HEAD, so it is not formatted).
```

## Notes / future work

- Formatting uses Roslyn defaults via an `AdhocWorkspace`; honoring a project's
  `.editorconfig` requires the MSBuild workspace
  (`Microsoft.CodeAnalysis.Workspaces.MSBuild`) and is deferred.
- The diff is **line-based** (git) on purpose: a formatter must catch whitespace
  / indentation edits, which structural (AST) diffs deliberately ignore.
