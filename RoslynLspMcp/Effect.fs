module RoslynLspMcp.Effect

open RoslynLspMcp.Models
open RoslynLspMcp.LspJson

// The LSP effect algebra, as a free monad. Each `Eff` case names one effect the
// tools need — a parsed LSP exchange, or a filesystem read — together with a
// continuation from that effect's result to the rest of the program. A tool query
// is then a pure value of type `Free<'a>` describing WHAT to do; a separate
// interpreter decides HOW (the real broker, or a stub in tests). Nothing here
// performs IO.
//
// LSP exchanges hand the program a `Result<_, ToolError>` rather than short-circuiting
// on their own: the *program* decides whether a given Retry / Unavailable / NotFound
// aborts the whole query or is absorbed (e.g. a missing hover is just "no docs",
// while a still-loading workspace must abort). Filesystem effects cannot raise a
// ToolError, so they yield an option / list / bool directly.

/// One effect, parameterised by the continuation type `'next`.
type Eff<'next> =
    | GetCwd of (string -> 'next)
    | ReadFile of path: string * (string option -> 'next)
    | FileExists of path: string * (bool -> 'next)
    | EnumerateCs of (string list -> 'next)
    | Definition of uri: string * line: int * character: int * (Result<Location option, ToolError> -> 'next)
    | DocumentSymbols of uri: string * (Result<SymbolNode list, ToolError> -> 'next)
    | Hover of uri: string * line: int * character: int * (Result<string option, ToolError> -> 'next)
    | PrepareHierarchy of uri: string * line: int * character: int * (Result<HierarchyItem list, ToolError> -> 'next)
    | Supertypes of itemRaw: string * itemUri: string * (Result<HierarchyItem list, ToolError> -> 'next)

/// Functor map over an effect's continuation — the engine behind `bind`.
let mapEff (f: 'a -> 'b) (op: Eff<'a>) : Eff<'b> =
    match op with
    | GetCwd k -> GetCwd(k >> f)
    | ReadFile(p, k) -> ReadFile(p, k >> f)
    | FileExists(p, k) -> FileExists(p, k >> f)
    | EnumerateCs k -> EnumerateCs(k >> f)
    | Definition(u, l, c, k) -> Definition(u, l, c, k >> f)
    | DocumentSymbols(u, k) -> DocumentSymbols(u, k >> f)
    | Hover(u, l, c, k) -> Hover(u, l, c, k >> f)
    | PrepareHierarchy(u, l, c, k) -> PrepareHierarchy(u, l, c, k >> f)
    | Supertypes(r, u, k) -> Supertypes(r, u, k >> f)

/// A pure program over the effect algebra: a finished value (`Pure`), or an effect
/// whose continuation yields the rest of the program (`Impure`).
type Free<'a> =
    | Pure of 'a
    | Impure of Eff<Free<'a>>

let rec bind (f: 'a -> Free<'b>) (m: Free<'a>) : Free<'b> =
    match m with
    | Pure a -> f a
    | Impure op -> Impure(mapEff (bind f) op)

let map (f: 'a -> 'b) (m: Free<'a>) : Free<'b> = bind (f >> Pure) m

// Smart constructors: each is a one-effect program whose continuation is `Pure`.
let getCwd: Free<string> = Impure(GetCwd Pure)
let readFile (path: string) : Free<string option> = Impure(ReadFile(path, Pure))
let fileExists (path: string) : Free<bool> = Impure(FileExists(path, Pure))
let enumerateCs: Free<string list> = Impure(EnumerateCs Pure)

let definition (uri: string) (line: int) (character: int) : Free<Result<Location option, ToolError>> =
    Impure(Definition(uri, line, character, Pure))

let documentSymbols (uri: string) : Free<Result<SymbolNode list, ToolError>> =
    Impure(DocumentSymbols(uri, Pure))

let hover (uri: string) (line: int) (character: int) : Free<Result<string option, ToolError>> =
    Impure(Hover(uri, line, character, Pure))

let prepareHierarchy (uri: string) (line: int) (character: int) : Free<Result<HierarchyItem list, ToolError>> =
    Impure(PrepareHierarchy(uri, line, character, Pure))

let supertypes (itemRaw: string) (itemUri: string) : Free<Result<HierarchyItem list, ToolError>> =
    Impure(Supertypes(itemRaw, itemUri, Pure))

type FreeBuilder() =
    member _.Return(x) = Pure x
    member _.ReturnFrom(m: Free<_>) = m
    member _.Bind(m, f) = bind f m
    member _.Zero() = Pure()

let lsp = FreeBuilder()

/// Thread an effectful step across a list, collecting the results in order.
let rec mapM (f: 'a -> Free<'b>) (xs: 'a list) : Free<'b list> =
    match xs with
    | [] -> Pure []
    | x :: rest -> bind (fun y -> bind (fun ys -> Pure(y :: ys)) (mapM f rest)) (f x)

/// Left fold whose step is itself an effectful program.
let rec foldM (f: 'state -> 'a -> Free<'state>) (state: 'state) (xs: 'a list) : Free<'state> =
    match xs with
    | [] -> Pure state
    | x :: rest -> bind (fun s -> foldM f s rest) (f state x)
