module RoslynLspMcp.Tests.FindTests

open Xunit
open RoslynLspMcp.LspJson
open RoslynLspMcp.Find
open RoslynLspMcp.Tests.Fixtures

// --- findToken --------------------------------------------------------------

[<Fact>]
let ``findToken returns the 0-based offset on a 1-based line`` () =
    let text = "using System;\nclass Foo { }\n"
    Assert.Equal(Some 6, findToken text 2 "Foo")

[<Fact>]
let ``findToken handles CRLF the same as LF`` () =
    let lf = "a\nFoo b\nc"
    let crlf = "a\r\nFoo b\r\nc"
    Assert.Equal(Some 0, findToken lf 2 "Foo")
    Assert.Equal(Some 0, findToken crlf 2 "Foo")
    Assert.Equal(Some 4, findToken crlf 2 "b")

[<Fact>]
let ``findToken returns None when token absent or line out of range`` () =
    let text = "one\ntwo\nthree"
    Assert.Equal(None, findToken text 1 "Foo")
    Assert.Equal(None, findToken text 99 "two")
    Assert.Equal(None, findToken text 0 "one")

// --- enclosingType ----------------------------------------------------------

let private symbols = parseDocumentSymbols documentSymbol

[<Fact>]
let ``enclosingType finds the type at its own name position`` () =
    match enclosingType symbols 23 18 with
    | Some n -> Assert.Equal("Dog", n.Name)
    | None -> Assert.Fail "expected Dog"

[<Fact>]
let ``enclosingType climbs from a member position to the enclosing type`` () =
    match enclosingType symbols 28 20 with
    | Some n -> Assert.Equal("Dog", n.Name)
    | None -> Assert.Fail "expected Dog"

[<Fact>]
let ``enclosingType returns None outside any type`` () =
    Assert.Equal(None, enclosingType symbols 39 0)
