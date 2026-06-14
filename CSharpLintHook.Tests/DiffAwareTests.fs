module CSharpLintHook.Tests.DiffAwareTests

open System.IO
open Xunit
open CSharpLintHook
open CSharpLintHook.Tests.TestHelpers

/// Run the real pipeline (filesystem + git + Roslyn) for a file path.
let private compute (path: string) =
    Interpreter.run (Logic.computeFormat path)

let private formatWrite (path: string) =
    Interpreter.run (Logic.formatAndWrite path)

[<Fact>]
let ``missing file reports not found and is not changed`` () =
    use repo = new ScratchRepo()
    let r = compute (repo.Path "DoesNotExist.cs")
    Assert.False r.Found
    Assert.False r.IsChanged

[<Fact>]
let ``file outside any git repo is formatted whole`` () =
    let dir = newTempDir ()

    try
        let path = Path.Combine(dir, "Loose.cs")
        writeFile path (messyTwoMethodClass "")
        let r = compute path
        Assert.True r.Found
        Assert.True r.WholeFile
        Assert.True r.IsChanged
        // Whole-file: BOTH methods get normalized.
        Assert.Contains("int x = 1;", r.Formatted)
        Assert.Contains("int y = 2;", r.Formatted)
    finally
        tryDeleteDir dir

[<Fact>]
let ``untracked file inside a repo is formatted whole`` () =
    use repo = new ScratchRepo()
    repo.Write "Other.cs" "class Placeholder { }\n"
    repo.Commit "seed"
    // New.cs is never committed -> untracked -> whole-file.
    repo.Write "New.cs" (messyTwoMethodClass "")
    let r = compute (repo.Path "New.cs")
    Assert.True r.WholeFile
    Assert.Contains("int x = 1;", r.Formatted)
    Assert.Contains("int y = 2;", r.Formatted)

[<Fact>]
let ``tracked file with no working changes is left untouched`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    let r = compute (repo.Path "C.cs")
    Assert.True r.Found
    Assert.False r.WholeFile
    Assert.False r.IsChanged

[<Fact>]
let ``only the changed method is formatted; the untouched method stays messy`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    // Change ONLY method A's body line.
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let r = compute (repo.Path "C.cs")

    Assert.True r.IsChanged
    Assert.False r.WholeFile
    Assert.True(r.Regions >= 1)
    // Method A (changed) is normalized.
    Assert.Contains("int x = 1;", r.Formatted)
    Assert.Contains("int z = 3;", r.Formatted)
    Assert.DoesNotContain("int x=1;int z=3;", r.Formatted)
    // Method B (unchanged) is preserved byte-for-byte, still messy.
    Assert.Contains("int y=2;", r.Formatted)
    Assert.DoesNotContain("int y = 2;", r.Formatted)

[<Fact>]
let ``formatAndWrite persists the formatted text to disk`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let (r, wrote) = formatWrite (repo.Path "C.cs")

    Assert.True wrote
    Assert.True r.IsChanged
    let onDisk = repo.Read "C.cs"
    Assert.Equal(r.Formatted, onDisk)
    Assert.Contains("int x = 1;", onDisk)
    Assert.Contains("int y=2;", onDisk) // B still messy on disk

[<Fact>]
let ``formatting is idempotent: a second pass writes nothing`` () =
    use repo = new ScratchRepo()
    repo.Write "C.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"
    repo.Write "C.cs" (messyTwoMethodClass "int z=3;")

    let (_, wrote1) = formatWrite (repo.Path "C.cs")
    Assert.True wrote1

    // Re-stage so the just-written formatting becomes the new base, then a
    // second run over an unchanged working copy must be a no-op.
    repo.Commit "formatted"
    let (r2, wrote2) = formatWrite (repo.Path "C.cs")
    Assert.False wrote2
    Assert.False r2.IsChanged

[<Fact>]
let ``two files are classified and formatted independently`` () =
    use repo = new ScratchRepo()
    repo.Write "Foo.cs" (messyTwoMethodClass "")
    repo.Write "Bar.cs" (messyTwoMethodClass "")
    repo.Commit "baseline"

    // Change only method A in each file.
    repo.Write "Foo.cs" (messyTwoMethodClass "int z=3;")
    repo.Write "Bar.cs" (messyTwoMethodClass "int w=4;")

    let (foo, fooWrote) = formatWrite (repo.Path "Foo.cs")
    let (bar, barWrote) = formatWrite (repo.Path "Bar.cs")

    Assert.True fooWrote
    Assert.True barWrote

    // Each file: its own changed method formatted, its B left messy.
    Assert.Contains("int z = 3;", foo.Formatted)
    Assert.Contains("int y=2;", foo.Formatted)

    Assert.Contains("int w = 4;", bar.Formatted)
    Assert.Contains("int y=2;", bar.Formatted)

    // Disk reflects both, independently.
    Assert.Contains("int z = 3;", repo.Read "Foo.cs")
    Assert.Contains("int w = 4;", repo.Read "Bar.cs")
