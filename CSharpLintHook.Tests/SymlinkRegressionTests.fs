module CSharpLintHook.Tests.SymlinkRegressionTests

open System.IO
open Xunit
open CSharpLintHook
open CSharpLintHook.Common
open CSharpLintHook.Tests.TestHelpers

/// Regression guard for the symlinked-path bug.
///
/// `git rev-parse --show-toplevel` resolves symlinks, so when a file is reached
/// through a symlinked directory (e.g. macOS /tmp -> /private/tmp) git's repo
/// root differs from the lexical path. The previous implementation derived the
/// tracked path with `Path.GetRelativePath(root, full)`, producing a bogus
/// `../link/...` path; `git ls-files` then failed and classification silently
/// degraded to whole-file formatting. The fix runs git from the file's own
/// directory using its basename. These tests reproduce the condition by
/// reaching a real repo through an explicit symlink.

[<Fact>]
let ``classify through a symlinked path returns Changed, not whole-file`` () =
    let real = newTempDir ()
    let link = newTempDir () |> fun d -> tryDeleteDir d; d
    Directory.CreateSymbolicLink(link, real) |> ignore

    try
        initRepo real
        writeFile (Path.Combine(real, "C.cs")) (messyTwoMethodClass "")
        commitAll real "baseline"
        writeFile (Path.Combine(real, "C.cs")) (messyTwoMethodClass "int z=3;")

        // Reach the file through the symlink.
        let linkedFile = Path.Combine(link, "C.cs")

        match Git.classify linkedFile with
        | FormatWhole -> failwith "regression: symlinked path degraded to whole-file formatting"
        | Changed ranges -> Assert.NotEmpty ranges
    finally
        tryDeleteDir link
        tryDeleteDir real

[<Fact>]
let ``diff-aware formatting still works through a symlinked path`` () =
    let real = newTempDir ()
    let link = newTempDir () |> fun d -> tryDeleteDir d; d
    Directory.CreateSymbolicLink(link, real) |> ignore

    try
        initRepo real
        writeFile (Path.Combine(real, "C.cs")) (messyTwoMethodClass "")
        commitAll real "baseline"
        writeFile (Path.Combine(real, "C.cs")) (messyTwoMethodClass "int z=3;")

        let linkedFile = Path.Combine(link, "C.cs")
        let r = Interpreter.run (Logic.computeFormat linkedFile)

        Assert.True r.IsChanged
        Assert.False r.WholeFile
        Assert.Contains("int x = 1;", r.Formatted)
        Assert.Contains("int y=2;", r.Formatted) // B untouched: proves diff-aware, not whole-file
    finally
        tryDeleteDir link
        tryDeleteDir real
