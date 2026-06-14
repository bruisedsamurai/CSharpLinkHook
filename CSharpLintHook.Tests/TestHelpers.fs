module CSharpLintHook.Tests.TestHelpers

open System
open System.Diagnostics
open System.IO

/// Run `git <args>` in workingDir, returning (exitCode, stdout, stderr).
/// Mirrors how the app shells out, so tests exercise the real git binary.
let git (workingDir: string) (args: string list) : int * string * string =
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
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    (p.ExitCode, out, err)

/// Assert a git command succeeded; throws with stderr otherwise.
let gitOk (workingDir: string) (args: string list) : unit =
    let (code, _, err) = git workingDir args

    if code <> 0 then
        failwithf "git %s failed (%d): %s" (String.Join(" ", args)) code err

/// Create a fresh, uniquely-named temporary directory. The real path is
/// returned (symlinks like macOS /tmp are intentionally left intact so tests
/// run through them).
let newTempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "clh_test_" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// Recursively delete a directory, ignoring errors (best-effort cleanup).
let tryDeleteDir (dir: string) : unit =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

/// Initialise a git repo in dir with a deterministic identity and no signing.
let initRepo (dir: string) : unit =
    gitOk dir [ "init"; "-q" ]
    gitOk dir [ "config"; "user.email"; "test@test.invalid" ]
    gitOk dir [ "config"; "user.name"; "Test" ]
    gitOk dir [ "config"; "commit.gpgsign"; "false" ]
    gitOk dir [ "config"; "core.autocrlf"; "false" ]

/// Write a file (creating parent dirs) with exact content, no added newline.
let writeFile (path: string) (content: string) : unit =
    Path.GetDirectoryName path
    |> Option.ofObj
    |> Option.iter (fun d -> Directory.CreateDirectory d |> ignore)

    File.WriteAllText(path, content)

/// Stage everything and commit with the given message.
let commitAll (dir: string) (message: string) : unit =
    gitOk dir [ "add"; "-A" ]
    gitOk dir [ "commit"; "-q"; "-m"; message ]

/// A disposable scratch git repository for one test. `Dir` is the repo root.
type ScratchRepo() =
    let dir = newTempDir ()
    do initRepo dir

    member _.Dir = dir
    member _.Path(rel: string) = Path.Combine(dir, rel)
    member this.Write (rel: string) (content: string) = writeFile (this.Path rel) content
    member _.Commit(message: string) = commitAll dir message
    member _.Read(rel: string) = File.ReadAllText(Path.Combine(dir, rel))
    member _.Git(args: string list) = git dir args

    interface IDisposable with
        member _.Dispose() = tryDeleteDir dir

/// A deliberately messy two-method class. `extra` is appended inside method A's
/// body so callers can produce a tracked change confined to A.
let messyTwoMethodClass (extra: string) : string =
    String.concat
        "\n"
        [ "class C"
          "{"
          "    void A()"
          "    {"
          "int x=1;" + extra
          "    }"
          "    void B()"
          "    {"
          "int y=2;"
          "    }"
          "}"
          "" ]
