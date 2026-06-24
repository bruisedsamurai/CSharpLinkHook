# AstGrepOutline

A small, Native-AOT-published `postToolUse` hook for the `roslyn-lsp-hook` Copilot CLI
plugin. After a `view` or `grep` tool call succeeds, it ensures `@ast-grep/cli` is
installed/updated globally via `npm i -g @ast-grep/cli`, then runs `ast-grep outline`
against the files and folders that tool touched and feeds the resulting structural
outline back to the model as `additionalContext`.

It is a direct port of the former `plugin/scripts/ast_grep_outline.py`; the behavioural
rules it preserves:

- Only acts on `view` / `grep` results with `resultType == "success"`.
- Extracts path-like arguments from the tool args (`path`, `paths`, `file_path`,
  `filePath`, `filename`, `file`, `target_file`, `targetFile`), accepting both the
  Copilot CLI camelCase and VS Code snake_case payload shapes, and tool args that arrive
  as either a JSON object or a JSON-encoded string.
- `grep` with no explicit path falls back to the hook's `cwd`.
- Resolves (`~` expansion, combine-with-cwd, full-path), keeps only existing
  files/folders, dedupes case-insensitively on Windows, and caps at 5 targets.
- Runs `ast-grep outline <target>` with a 10s timeout per target, skipping any that
  error or time out.
- Combines the per-target outlines and caps the emitted context at ~9.5K characters.
- Never breaks the agent loop: any failure exits 0 with no output.

On each run the hook first installs/updates `@ast-grep/cli` globally with
`npm i -g @ast-grep/cli` (best-effort: a missing npm, no network, or a slow install is
swallowed), so the tool tracks the latest release instead of a binary bundled by the
build. The `ast-grep` binary is then resolved from the npm global root
(`npm root -g` → `@ast-grep/cli/ast-grep[.exe]`), falling back to `PATH`.
