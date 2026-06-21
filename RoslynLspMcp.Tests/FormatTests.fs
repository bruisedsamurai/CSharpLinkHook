module RoslynLspMcp.Tests.FormatTests

open Xunit
open RoslynLspMcp.Models

// --- error messages ---------------------------------------------------------

[<Fact>]
let ``formatError gives a retry message for a still-loading workspace`` () =
    let msg = formatError (Retry "projects loading")
    Assert.Contains("still loading", msg)
    Assert.Contains("projects loading", msg)

[<Fact>]
let ``formatError explains an unavailable broker and echoes NotFound text`` () =
    Assert.Contains("not available", formatError Unavailable)
    Assert.Equal("could not resolve Foo", formatError (NotFound "could not resolve Foo"))

// --- class output -----------------------------------------------------------

[<Fact>]
let ``formatClass embeds documentation and member hovers verbatim`` () =
    let info =
        { TypeName = "Demo.Dog"
          Hover = Some "```csharp\r\nclass Demo.Dog\r\n```\r\nA dog."
          Constructors =
            [ { Label = "Dog()"; Hover = Some "Dog()" }
              { Label = "Dog(string)"; Hover = Some "Dog(string breed)" } ]
          Properties = [ { Label = "Breed : string"; Hover = Some "string Demo.Dog.Breed { get; set; }" } ] }

    let out = formatClass info
    Assert.Contains("# Demo.Dog", out)
    Assert.Contains("class Demo.Dog\r\n```\r\nA dog.", out) // verbatim, untrimmed
    Assert.Contains("Dog(string breed)", out)
    Assert.Contains("string Demo.Dog.Breed { get; set; }", out)

[<Fact>]
let ``formatClass notes when there are no constructors or properties`` () =
    let info =
        { TypeName = "Demo.Empty"
          Hover = None
          Constructors = []
          Properties = [] }

    let out = formatClass info
    Assert.Contains("(no constructors)", out)
    Assert.Contains("no public, protected, or internal properties", out)

// --- methods output ---------------------------------------------------------

[<Fact>]
let ``formatMethods groups inherited methods and adds a partial note when flagged`` () =
    let info =
        { TypeName = "Demo.Dog"
          OwnMethods = [ { Label = "Fetch() : string"; Hover = Some "string Demo.Dog.Fetch()" } ]
          Inherited = [ ("Animal", [ { Label = "Speak() : string"; Hover = Some "string Demo.Animal.Speak()" } ]) ]
          Partial = true }

    let out = formatMethods info
    Assert.Contains("Declared on Demo.Dog", out)
    Assert.Contains("string Demo.Dog.Fetch()", out)
    Assert.Contains("Inherited from Animal", out)
    Assert.Contains("string Demo.Animal.Speak()", out)
    Assert.Contains("may be partial", out)

[<Fact>]
let ``formatMethods omits the partial note when complete`` () =
    let info =
        { TypeName = "Demo.Dog"
          OwnMethods = []
          Inherited = []
          Partial = false }

    Assert.DoesNotContain("may be partial", formatMethods info)

// --- namespace output -------------------------------------------------------

[<Fact>]
let ``formatNamespace lists declarations and handles an empty namespace`` () =
    let decls =
        [ { Label = "class Dog"; Hover = Some "class Demo.Dog\r\nA dog." }
          { Label = "interface IShape"; Hover = None } ]

    let out = formatNamespace "Demo" decls
    Assert.Contains("# namespace Demo", out)
    Assert.Contains("class Demo.Dog\r\nA dog.", out)
    Assert.Contains("interface IShape", out) // falls back to label when no hover

    let empty = formatNamespace "Demo.Empty" []
    Assert.Contains("no top-level type declarations", empty)
