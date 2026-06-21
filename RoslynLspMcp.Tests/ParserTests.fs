module RoslynLspMcp.Tests.ParserTests

open System.Text.Json.Nodes
open Xunit
open RoslynLspMcp.LspJson
open RoslynLspMcp.Tests.Fixtures

// --- documentSymbol ---------------------------------------------------------

let private dog () =
    parseDocumentSymbols documentSymbol
    |> List.head
    |> fun ns -> ns.Children |> List.find (fun t -> t.Name = "Dog")

[<Fact>]
let ``parseDocumentSymbols reads the namespace then its types`` () =
    let top = parseDocumentSymbols documentSymbol
    Assert.Single top |> ignore
    Assert.Equal("Demo", top.Head.Name)
    Assert.Equal(Kind.Namespace, top.Head.Kind)
    Assert.Equal("Dog", top.Head.Children.Head.Name)

[<Fact>]
let ``parseDocumentSymbols reads node fields including glyph and selection`` () =
    let breed = dog().Children |> List.find (fun c -> c.Name.StartsWith "Breed")
    Assert.Equal(Kind.Property, breed.Kind)
    Assert.Equal(63, breed.Glyph)
    Assert.Equal(26, breed.SelLine)
    Assert.Equal(18, breed.SelCharacter)

[<Fact>]
let ``constructors are the method-kind children whose name matches the type`` () =
    let ctors = dog().Children |> List.filter (isConstructor "Dog")
    Assert.Equal(2, ctors.Length)
    Assert.All(ctors, fun c -> Assert.StartsWith("Dog(", c.Name))

[<Fact>]
let ``visible properties exclude private; public methods exclude ctors and non-public`` () =
    let children = dog().Children

    let props =
        children
        |> List.filter (fun c -> c.Kind = Kind.Property && isVisiblePropertyGlyph c.Glyph)

    let methods =
        children
        |> List.filter (fun c -> c.Kind = Kind.Method && not (isConstructor "Dog" c) && isPublicMethodGlyph c.Glyph)

    Assert.Equal<string list>([ "Breed : string"; "Legs : int" ], props |> List.map (fun p -> p.Name))
    Assert.Equal<string list>([ "Fetch() : string" ], methods |> List.map (fun m -> m.Name))

[<Fact>]
let ``classification predicates`` () =
    Assert.True(isTypeKind Kind.Class)
    Assert.True(isTypeKind Kind.Interface)
    Assert.True(isTypeKind Kind.Struct)
    Assert.False(isTypeKind Kind.Method)
    Assert.True(isPublicMethodGlyph 49)
    Assert.False(isPublicMethodGlyph 51)
    Assert.True(isVisiblePropertyGlyph 63)
    Assert.True(isVisiblePropertyGlyph 66)
    Assert.False(isVisiblePropertyGlyph 65)
    Assert.Equal("List", simpleTypeName "List<int>")

// --- definition -------------------------------------------------------------

[<Fact>]
let ``parseDefinition reads a Location array`` () =
    match parseDefinition definitionArray with
    | Some loc ->
        Assert.Equal("file:///C:/work/Shapes.cs", loc.Uri)
        Assert.Equal(28, loc.Line)
        Assert.Equal(15, loc.Character)
    | None -> Assert.Fail "expected a location"

[<Fact>]
let ``parseDefinition reads a LocationLink array via targetUri`` () =
    match parseDefinition definitionLink with
    | Some loc ->
        Assert.Equal("file:///C:/work/Other.cs", loc.Uri)
        Assert.Equal(3, loc.Line)
        Assert.Equal(2, loc.Character)
    | None -> Assert.Fail "expected a location"

[<Fact>]
let ``parseDefinition returns None for an empty array`` () =
    Assert.Equal(None, parseDefinition "[]")
    Assert.Equal(None, parseDefinition "null")

// --- hover ------------------------------------------------------------------

[<Fact>]
let ``parseHover returns the markdown value verbatim`` () =
    Assert.Equal(Some hoverMarkdownValue, parseHover hoverMarkdown)

[<Fact>]
let ``parseHover handles a plain string content and missing content`` () =
    Assert.Equal(Some "plain", parseHover """{"contents":"plain"}""")
    Assert.Equal(None, parseHover """{"range":{}}""")

// --- type hierarchy ---------------------------------------------------------

[<Fact>]
let ``parseHierarchyItems keeps name, kind, uri and verbatim raw data`` () =
    let items = parseHierarchyItems supertypes
    Assert.Single items |> ignore
    let a = items.Head
    Assert.Equal("Animal", a.Name)
    Assert.Equal(Kind.Class, a.Kind)
    Assert.StartsWith("file:///", a.Uri)
    Assert.Contains("SymbolKeyData", a.Raw)
    Assert.Contains("314b809f-2578-4aba-9dcc-2fa1d7861b4e", a.Raw)

[<Fact>]
let ``supertypesParams wraps the raw item under an item key`` () =
    let item = (parseHierarchyItems supertypes).Head
    let parsed = JsonNode.Parse(supertypesParams item.Raw)
    let inner = parsed.["item"]
    Assert.NotNull inner
    Assert.Equal("Animal", inner.["name"].GetValue<string>())
    Assert.Contains("SymbolKeyData", inner.["data"].ToJsonString())

// --- request params ---------------------------------------------------------

[<Fact>]
let ``positionParams builds textDocument and zero-based position`` () =
    let parsed = JsonNode.Parse(positionParams "file:///C:/a.cs" 5 7)
    Assert.Equal("file:///C:/a.cs", parsed.["textDocument"].["uri"].GetValue<string>())
    Assert.Equal(5, parsed.["position"].["line"].GetValue<int>())
    Assert.Equal(7, parsed.["position"].["character"].GetValue<int>())

[<Fact>]
let ``documentSymbolParams carries the document uri`` () =
    let parsed = JsonNode.Parse(documentSymbolParams "file:///C:/a.cs")
    Assert.Equal("file:///C:/a.cs", parsed.["textDocument"].["uri"].GetValue<string>())
