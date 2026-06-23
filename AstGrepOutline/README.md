# AstGrepOutline

A small, Native-AOT-published `postToolUse` hook for the `roslyn-lsp-hook` Copilot CLI
plugin. After a `view` or `grep` tool call succeeds, it runs `ast-grep outline` against
the files and folders that tool touched and feeds the resulting structural outline back
to the model as `additionalContext`.

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

The `ast-grep` binary is discovered next to the published executable (it is bundled in
the plugin by the build) and otherwise from `PATH`.
