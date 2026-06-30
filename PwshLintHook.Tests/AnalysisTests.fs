module PwshLintHook.Tests.AnalysisTests

open Xunit
open PwshLintHook.Analysis

let private fd (cmd: string) : string =
    match analyze cmd with
    | DenyFd fdCommand -> fdCommand
    | other -> failwithf "expected DenyFd, got %A" other

let private isFf (cmd: string) : bool =
    match analyze cmd with
    | DenyFf _ -> true
    | _ -> false

let private isAllow (cmd: string) : bool =
    match analyze cmd with
    | Allow -> true
    | _ -> false

[<Fact>]
let ``filesystem search maps to the equivalent fd command`` () =
    let cmd =
        "Get-ChildItem $sdk -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Select-Object FullName | Format-Table -AutoSize -Wrap"

    Assert.Equal("fd -t f -e cs -E obj -E bin . \"$sdk\"", fd cmd)

[<Fact>]
let ``bare Get-ChildItem maps to fd dot`` () = Assert.Equal("fd .", fd "Get-ChildItem")

[<Fact>]
let ``-Directory maps to fd -t d`` () =
    Assert.Equal("fd -t d . C:\\src", fd "Get-ChildItem -Directory C:\\src")

[<Fact>]
let ``-Force adds -H -I to fd`` () =
    Assert.Equal("fd -t f -H -I .", fd "Get-ChildItem -File -Force")

[<Fact>]
let ``Select-String is a content search`` () =
    Assert.True(isFf "Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern TODO")

[<Fact>]
let ``Get-Content piped into a filter is a content search`` () =
    Assert.True(isFf "Get-Content app.log | Where-Object { $_ -match 'error' }")

[<Fact>]
let ``content search takes precedence over filesystem search`` () =
    // gci feeds Select-String: the pipeline is fundamentally a content search → fff.
    Assert.True(isFf "gci -r *.cs | sls TODO")

[<Fact>]
let ``a plain Get-Content read is allowed`` () =
    Assert.True(isAllow "Get-Content app.config")

[<Fact>]
let ``a non-filesystem provider drive is allowed`` () =
    Assert.True(isAllow "Get-ChildItem Env:")
    Assert.True(isAllow "Get-ChildItem HKLM:\\Software")

[<Fact>]
let ``an unrelated command is allowed`` () =
    Assert.True(isAllow "git status")
    Assert.True(isAllow "dotnet build")

[<Fact>]
let ``fdReason embeds the suggested command`` () =
    let reason = fdReason "fd ."
    Assert.Contains("fd .", reason)
    Assert.Contains("Get-ChildItem", reason)
