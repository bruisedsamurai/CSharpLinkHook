module RoslynLspMcp.Tests.Fixtures

// Realistic LSP *result* payloads (the JSON the broker passthrough hands back
// after stripping the JSON-RPC envelope), captured from the real
// roslyn-language-server, used to exercise the pure parsers and classifiers.

/// `textDocument/documentSymbol` result: a `Demo` namespace whose `Dog` type has
/// two constructors, two visible properties (public Breed g63, internal Legs g66),
/// one public method (Fetch g49) and one private method (Secret g51).
let documentSymbol =
    """[
  {"name":"Demo","kind":3,"glyph":48,
   "range":{"start":{"line":0,"character":0},"end":{"line":40,"character":1}},
   "selectionRange":{"start":{"line":0,"character":10},"end":{"line":0,"character":14}},
   "children":[
     {"name":"Dog","kind":5,"glyph":4,
      "detail":"class Dog",
      "range":{"start":{"line":23,"character":0},"end":{"line":35,"character":1}},
      "selectionRange":{"start":{"line":23,"character":17},"end":{"line":23,"character":20}},
      "children":[
        {"name":"Dog()","kind":6,"glyph":49,"range":{"start":{"line":24,"character":4},"end":{"line":24,"character":20}},"selectionRange":{"start":{"line":24,"character":11},"end":{"line":24,"character":14}},"children":[]},
        {"name":"Dog(string)","kind":6,"glyph":49,"range":{"start":{"line":25,"character":4},"end":{"line":25,"character":30}},"selectionRange":{"start":{"line":25,"character":11},"end":{"line":25,"character":14}},"children":[]},
        {"name":"Breed : string","kind":7,"glyph":63,"range":{"start":{"line":26,"character":4},"end":{"line":26,"character":40}},"selectionRange":{"start":{"line":26,"character":18},"end":{"line":26,"character":23}},"children":[]},
        {"name":"Legs : int","kind":7,"glyph":66,"range":{"start":{"line":27,"character":4},"end":{"line":27,"character":30}},"selectionRange":{"start":{"line":27,"character":15},"end":{"line":27,"character":19}},"children":[]},
        {"name":"Fetch() : string","kind":6,"glyph":49,"range":{"start":{"line":28,"character":4},"end":{"line":28,"character":40}},"selectionRange":{"start":{"line":28,"character":18},"end":{"line":28,"character":23}},"children":[]},
        {"name":"Secret() : void","kind":6,"glyph":51,"range":{"start":{"line":29,"character":4},"end":{"line":29,"character":30}},"selectionRange":{"start":{"line":29,"character":16},"end":{"line":29,"character":22}},"children":[]}
      ]}
   ]}
]"""

/// `textDocument/definition` result, classic `Location[]` form.
let definitionArray =
    """[{"uri":"file:///C:/work/Shapes.cs","range":{"start":{"line":28,"character":15},"end":{"line":28,"character":18}}}]"""

/// `textDocument/definition` result, `LocationLink[]` form.
let definitionLink =
    """[{"targetUri":"file:///C:/work/Other.cs","targetSelectionRange":{"start":{"line":3,"character":2},"end":{"line":3,"character":9}}}]"""

/// `textDocument/hover` result for a class (markdown content kept verbatim).
let hoverMarkdown =
    "{\"contents\":{\"kind\":\"markdown\",\"value\":\"```csharp\\r\\nclass Demo.Dog\\r\\n```\\r\\n  \\r\\nA dog, which is an animal\\\\.  \\r\\n\"},\"range\":{\"start\":{\"line\":23,\"character\":17},\"end\":{\"line\":23,\"character\":20}}}"

/// The exact markdown string the hover above should yield.
let hoverMarkdownValue = "```csharp\r\nclass Demo.Dog\r\n```\r\n  \r\nA dog, which is an animal\\.  \r\n"

/// `typeHierarchy/supertypes` result with one base item carrying opaque `data`.
let supertypes =
    """[{"name":"Animal","kind":5,"detail":"Demo","uri":"file:///C:/work/Shapes.cs","range":{"start":{"line":12,"character":4},"end":{"line":20,"character":5}},"selectionRange":{"start":{"line":12,"character":17},"end":{"line":12,"character":23}},"data":{"SymbolKeyData":"7 \"C#\" (D (N \"Demo\" 0 ...))","ProjectGuid":"314b809f-2578-4aba-9dcc-2fa1d7861b4e"}}]"""
