module RoslynLspMcp.Tests.InterpreterTests

open Xunit
open RoslynLspMcp.Models
open RoslynLspMcp.LspJson
open RoslynLspMcp.Effect
open RoslynLspMcp.Queries

// The point of the free-monad refactor: the three tools are pure programs (`Free`),
// so a STUB interpreter that answers effects from canned, in-memory data runs them
// end-to-end with no broker, no pipe, no filesystem, no Roslyn. These tests pin the
// queries' real control flow (resolve → documentSymbol → hover, supertype walk,
// namespace scan, error propagation) deterministically and in milliseconds.

/// A canned world the stub interpreter answers from. LSP replies are the same raw
/// JSON the real broker returns; the stub parses them with the production parsers,
/// so programs see exactly the domain values they would in production.
type private World =
    { Cwd: string
      Files: Map<string, string>
      Definition: string
      DocSymbolsByUri: Map<string, string>
      DefaultDocSymbols: string
      Hover: string
      Hierarchy: string
      SupertypesByUri: Map<string, string>
      DefaultSupertypes: string }

let private emptyWorld =
    { Cwd = "C:\\work"
      Files = Map.empty
      Definition = "[]"
      DocSymbolsByUri = Map.empty
      DefaultDocSymbols = "[]"
      Hover = "null"
      Hierarchy = "[]"
      SupertypesByUri = Map.empty
      DefaultSupertypes = "[]" }

/// Interpret a `Free` program against canned data. Mirrors the real interpreter's
/// shape (tail-recursive, parsers on the replies) but performs no IO.
let rec private runStub (w: World) (program: Free<'a>) : 'a =
    match program with
    | Pure a -> a
    | Impure op ->
        match op with
        | GetCwd k -> runStub w (k w.Cwd)
        | ReadFile(path, k) -> runStub w (k (Map.tryFind path w.Files))
        | FileExists(path, k) -> runStub w (k (Map.containsKey path w.Files))
        | EnumerateCs k ->
            let files =
                w.Files |> Map.toList |> List.map fst |> List.filter (fun p -> p.EndsWith ".cs")

            runStub w (k files)
        | Definition(_, _, _, k) -> runStub w (k (Ok(parseDefinition w.Definition)))
        | DocumentSymbols(uri, k) ->
            let json = Map.tryFind uri w.DocSymbolsByUri |> Option.defaultValue w.DefaultDocSymbols
            runStub w (k (Ok(parseDocumentSymbols json)))
        | Hover(_, _, _, k) -> runStub w (k (Ok(parseHover w.Hover)))
        | PrepareHierarchy(_, _, _, k) -> runStub w (k (Ok(parseHierarchyItems w.Hierarchy)))
        | Supertypes(_, itemUri, k) ->
            let json =
                Map.tryFind itemUri w.SupertypesByUri |> Option.defaultValue w.DefaultSupertypes

            runStub w (k (Ok(parseHierarchyItems json)))

let private usageText =
    "namespace Demo;\nclass Usage {\n  void M() {\n    var d = new Dog();\n  }\n}"

[<Fact>]
let ``classConstructorsAndProperties runs purely against a stub interpreter`` () =
    let world =
        { emptyWorld with
            Files = Map.ofList [ "C:\\work\\Usage.cs", usageText ]
            Definition = Fixtures.definitionArray // points at file:///C:/work/Shapes.cs (28,15)
            DocSymbolsByUri = Map.ofList [ "file:///C:/work/Shapes.cs", Fixtures.documentSymbol ]
            Hover = Fixtures.hoverMarkdown }

    match runStub world (classConstructorsAndProperties "Usage.cs" 4 "Dog") with
    | Ok info ->
        Assert.Equal("Dog", info.TypeName)
        Assert.Equal(Some Fixtures.hoverMarkdownValue, info.Hover) // verbatim hover threaded through
        Assert.Equal(2, List.length info.Constructors) // Dog(), Dog(string)
        Assert.Equal(2, List.length info.Properties) // Breed (g63), Legs (g66); private excluded
    | Error e -> Assert.True(false, sprintf "expected Ok, got %A" e)

[<Fact>]
let ``classMethods walks the supertype chain purely against a stub interpreter`` () =
    let dogSelf =
        """[{"name":"Dog","kind":5,"uri":"file:///C:/work/Shapes.cs","range":{"start":{"line":23,"character":0},"end":{"line":35,"character":1}},"selectionRange":{"start":{"line":23,"character":17},"end":{"line":23,"character":20}},"data":{"k":"dog"}}]"""

    let animalItem =
        """[{"name":"Animal","kind":5,"uri":"file:///C:/work/Animal.cs","range":{"start":{"line":2,"character":0},"end":{"line":9,"character":1}},"selectionRange":{"start":{"line":2,"character":17},"end":{"line":2,"character":23}},"data":{"k":"animal"}}]"""

    let animalDoc =
        """[{"name":"Demo","kind":3,"glyph":48,"range":{"start":{"line":0,"character":0},"end":{"line":12,"character":1}},"selectionRange":{"start":{"line":0,"character":10},"end":{"line":0,"character":14}},"children":[{"name":"Animal","kind":5,"glyph":4,"detail":"class Animal","range":{"start":{"line":2,"character":0},"end":{"line":9,"character":1}},"selectionRange":{"start":{"line":2,"character":17},"end":{"line":2,"character":23}},"children":[{"name":"Speak() : void","kind":6,"glyph":49,"range":{"start":{"line":4,"character":4},"end":{"line":4,"character":30}},"selectionRange":{"start":{"line":4,"character":16},"end":{"line":4,"character":21}},"children":[]}]}]}]"""

    let world =
        { emptyWorld with
            Files = Map.ofList [ "C:\\work\\Usage.cs", usageText; "C:\\work\\Animal.cs", "" ]
            Definition = Fixtures.definitionArray
            DocSymbolsByUri =
                Map.ofList
                    [ "file:///C:/work/Shapes.cs", Fixtures.documentSymbol
                      "file:///C:/work/Animal.cs", animalDoc ]
            Hierarchy = dogSelf
            SupertypesByUri = Map.ofList [ "file:///C:/work/Shapes.cs", animalItem ] }

    match runStub world (classMethods "Usage.cs" 4 "Dog") with
    | Ok m ->
        Assert.Equal("Dog", m.TypeName)
        Assert.True(m.OwnMethods |> List.exists (fun x -> x.Label.Contains "Fetch")) // declared, public
        Assert.False m.Partial // Animal resolved from source, not a referenced assembly
        Assert.Contains("Animal", m.Inherited |> List.map fst)
        Assert.True(m.Inherited |> List.collect snd |> List.exists (fun x -> x.Label.Contains "Speak"))
    | Error e -> Assert.True(false, sprintf "expected Ok, got %A" e)

[<Fact>]
let ``namespaceDeclarations scans files purely against a stub interpreter`` () =
    let demoBoth =
        """[{"name":"Demo","kind":3,"glyph":48,"range":{"start":{"line":0,"character":0},"end":{"line":50,"character":1}},"selectionRange":{"start":{"line":0,"character":10},"end":{"line":0,"character":14}},"children":[{"name":"Dog","kind":5,"glyph":4,"detail":"class Dog","range":{"start":{"line":5,"character":0},"end":{"line":10,"character":1}},"selectionRange":{"start":{"line":5,"character":17},"end":{"line":5,"character":20}},"children":[]},{"name":"Animal","kind":5,"glyph":4,"detail":"class Animal","range":{"start":{"line":12,"character":0},"end":{"line":20,"character":1}},"selectionRange":{"start":{"line":12,"character":17},"end":{"line":12,"character":23}},"children":[]}]}]"""

    let world =
        { emptyWorld with
            Files = Map.ofList [ "C:\\work\\Shapes.cs", ""; "C:\\work\\Usage.cs", "" ]
            DefaultDocSymbols = demoBoth }

    match runStub world (namespaceDeclarations "Demo") with
    | Ok decls ->
        let labels = decls |> List.map (fun d -> d.Label)
        Assert.True(labels |> List.exists (fun l -> l.Contains "Dog"))
        Assert.True(labels |> List.exists (fun l -> l.Contains "Animal"))
        Assert.Equal(2, List.length decls) // deduped by simple name across the two files
    | Error e -> Assert.True(false, sprintf "expected Ok, got %A" e)

[<Fact>]
let ``a missing token resolves to NotFound without touching a broker`` () =
    let world =
        { emptyWorld with
            Files = Map.ofList [ "C:\\work\\Usage.cs", "namespace Demo;\nclass Usage {}" ] }

    match runStub world (classConstructorsAndProperties "Usage.cs" 2 "Dog") with
    | Error(NotFound _) -> () // 'Dog' is not on line 2 — pure failure, no effects beyond the read
    | other -> Assert.True(false, sprintf "expected NotFound, got %A" other)
