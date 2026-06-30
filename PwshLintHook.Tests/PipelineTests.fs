module PwshLintHook.Tests.PipelineTests

open Xunit
open PwshLintHook.Pipeline

let private firstPipeline (cmd: string) : Stage list =
    match parse cmd with
    | p :: _ -> p
    | [] -> failwith "expected at least one pipeline"

[<Theory>]
[<InlineData("gci", "get-childitem")>]
[<InlineData("ls", "get-childitem")>]
[<InlineData("dir", "get-childitem")>]
[<InlineData("gc", "get-content")>]
[<InlineData("cat", "get-content")>]
[<InlineData("sls", "select-string")>]
[<InlineData("?", "where-object")>]
[<InlineData("Get-ChildItem", "get-childitem")>]
let ``resolveAlias maps aliases to canonical names`` (alias: string, canonical: string) =
    Assert.Equal(canonical, resolveAlias alias)

[<Fact>]
let ``classify assigns the right category`` () =
    Assert.Equal(Source, classify "get-childitem")
    Assert.Equal(Source, classify "import-csv")
    Assert.Equal(Filter, classify "where-object")
    Assert.Equal(Filter, classify "select-string")
    Assert.Equal(Projection, classify "select-object")
    Assert.Equal(Projection, classify "sort-object")
    Assert.Equal(Formatting, classify "format-table")
    Assert.Equal(Formatting, classify "out-file")
    Assert.Equal(Other, classify "remove-item")

[<Fact>]
let ``parse splits the canonical pipeline into four classified stages`` () =
    let stages =
        firstPipeline
            "Get-ChildItem $sdk -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Select-Object FullName | Format-Table -AutoSize -Wrap"

    let names = stages |> List.map (fun s -> s.Name)
    Assert.Equal<string list>([ "get-childitem"; "where-object"; "select-object"; "format-table" ], names)

    let categories = stages |> List.map (fun s -> s.Category)
    Assert.Equal<Category list>([ Source; Filter; Projection; Formatting ], categories)

[<Fact>]
let ``parse binds Get-ChildItem switches, value-params and positionals`` () =
    let gci = firstPipeline "Get-ChildItem $sdk -Recurse -File -Filter *.cs" |> List.head
    Assert.True(gci.Switches.Contains "recurse")
    Assert.True(gci.Switches.Contains "file")
    Assert.Equal(Some "*.cs", gci.Params |> List.tryPick (fun (k, v) -> if k = "filter" then Some v else None))
    Assert.Equal<string list>([ "$sdk" ], gci.Positionals)

[<Fact>]
let ``parse does not swallow a positional path after a switch`` () =
    // -Recurse is a known switch, so C:\src must remain a positional, not its value.
    let gci = firstPipeline "Get-ChildItem -Recurse C:\\src" |> List.head
    Assert.True(gci.Switches.Contains "recurse")
    Assert.Equal<string list>([ "C:\\src" ], gci.Positionals)

[<Fact>]
let ``parse binds -Path value`` () =
    let gci = firstPipeline "Get-ChildItem -Path C:\\src -Filter *.fs" |> List.head
    Assert.Equal(Some "C:\\src", gci.Params |> List.tryPick (fun (k, v) -> if k = "path" then Some v else None))

[<Fact>]
let ``parse returns no stages for an empty command`` () =
    Assert.Equal<Stage list list>([], parse "   ")
