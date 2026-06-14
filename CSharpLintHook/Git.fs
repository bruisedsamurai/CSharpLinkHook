module CSharpLintHook.Git

open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open CSharpLintHook.Common

/// Runs `git <args>` in workingDir. Returns (exitCode, stdout, stderr).
/// Never throws; a missing git binary yields exit code 127.
let private runGit (workingDir: string) (args: string list) : int * string * string =
    try
        let psi =
            ProcessStartInfo(
                "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir
            )

        for a in args do
            psi.ArgumentList.Add a

        use p = new Process(StartInfo = psi)
        p.Start() |> ignore
        let outTask = p.StandardOutput.ReadToEndAsync()
        let errTask = p.StandardError.ReadToEndAsync()
        p.WaitForExit()
        (p.ExitCode, outTask.Result, errTask.Result)
    with ex ->
        (127, "", ex.Message)

/// The repository root containing dir, or None if dir is not inside a repo.
let tryRepoRoot (dir: string) : string option =
    let (code, out, _) = runGit dir [ "rev-parse"; "--show-toplevel" ]

    if code = 0 then
        let trimmed = out.Trim()
        if trimmed.Length > 0 then Some trimmed else None
    else
        None

let private isTracked (workingDir: string) (fileName: string) : bool =
    let (code, _, _) = runGit workingDir [ "ls-files"; "--error-unmatch"; "--"; fileName ]
    code = 0

let private hunkRegex =
    Regex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled)

/// Extracts changed new-file line ranges (0-based inclusive) from unified=0 diff text.
let private parseHunks (diff: string) : LineRange list =
    [ for line in diff.Split('\n') do
          let m = hunkRegex.Match line

          if m.Success then
              let start = int m.Groups[1].Value
              let count = if m.Groups[2].Success then int m.Groups[2].Value else 1

              if count <= 0 then
                  // Pure deletion at new-file position `start`: touch the join line.
                  let idx = max 0 (start - 1)
                  yield { StartLine = idx; EndLineInclusive = idx }
              else
                  // New lines [start, start + count - 1] (1-based) -> 0-based.
                  yield
                      { StartLine = start - 1
                        EndLineInclusive = start + count - 2 } ]

/// Classifies a file against its git HEAD base.
///
/// Every git command runs from the file's own directory and names the file by its
/// basename, so git resolves the repository and the path itself. This keeps the
/// mapping correct even when a symlink in the path (e.g. macOS /tmp ->
/// /private/tmp) makes git's resolved repo root differ from the lexical path —
/// the previous lexical `GetRelativePath` against that root silently mismatched
/// and degraded to whole-file formatting.
let classify (filePath: string) : DiffResult =
    let full = Path.GetFullPath filePath

    match Path.GetDirectoryName full with
    | null -> FormatWhole
    | dir when dir.Length = 0 -> FormatWhole
    | dir ->
        match tryRepoRoot dir with
        | None -> FormatWhole
        | Some _ ->
            match Path.GetFileName full with
            | null -> FormatWhole
            | fileName when fileName.Length = 0 -> FormatWhole
            | fileName ->
                if not (isTracked dir fileName) then
                    FormatWhole
                else
                    let (code, out, _) =
                        runGit dir [ "diff"; "--unified=0"; "--no-color"; "HEAD"; "--"; fileName ]

                    if code <> 0 then FormatWhole else Changed(parseHunks out)
