module CSharpLintHook.Effects

open CSharpLintHook.Common

/// Free-monad program over the side-effecting operations the application needs.
///
/// Every non-`Pure` case carries its arguments plus a continuation from the
/// effect's result to the remaining program. A `Program<'a>` is therefore just
/// an inert description of work; an interpreter (see Interpreter.fs) is what
/// actually performs the effects. This keeps all business logic pure/testable.
type Program<'a> =
    | Pure of 'a
    | ReadStdin of (string -> Program<'a>)
    | WriteStdout of string * (unit -> Program<'a>)
    | ReadFile of string * (string -> Program<'a>)
    | WriteFile of string * string * (unit -> Program<'a>)
    | FileExists of string * (bool -> Program<'a>)
    | ClassifyDiff of string * (DiffResult -> Program<'a>)
    | FmtWhole of string * (string -> Program<'a>)
    | FmtRanges of string * LineRange list * ((string * int) -> Program<'a>)
    | LogLine of string * (unit -> Program<'a>)

/// Monadic bind: thread the continuation through the program tree.
let rec bind (f: 'a -> Program<'b>) (p: Program<'a>) : Program<'b> =
    match p with
    | Pure x -> f x
    | ReadStdin k -> ReadStdin(fun s -> bind f (k s))
    | WriteStdout(s, k) -> WriteStdout(s, fun () -> bind f (k ()))
    | ReadFile(path, k) -> ReadFile(path, fun s -> bind f (k s))
    | WriteFile(path, c, k) -> WriteFile(path, c, fun () -> bind f (k ()))
    | FileExists(path, k) -> FileExists(path, fun b -> bind f (k b))
    | ClassifyDiff(path, k) -> ClassifyDiff(path, fun d -> bind f (k d))
    | FmtWhole(c, k) -> FmtWhole(c, fun s -> bind f (k s))
    | FmtRanges(c, r, k) -> FmtRanges(c, r, fun res -> bind f (k res))
    | LogLine(s, k) -> LogLine(s, fun () -> bind f (k ()))

let map (f: 'a -> 'b) (p: Program<'a>) : Program<'b> = bind (fun x -> Pure(f x)) p

// Smart constructors: each is a single-step program returning the effect result.
let readStdin: Program<string> = ReadStdin Pure
let writeStdout (s: string) : Program<unit> = WriteStdout(s, Pure)
let readFile (path: string) : Program<string> = ReadFile(path, Pure)
let writeFile (path: string) (content: string) : Program<unit> = WriteFile(path, content, Pure)
let fileExists (path: string) : Program<bool> = FileExists(path, Pure)
let classifyDiff (path: string) : Program<DiffResult> = ClassifyDiff(path, Pure)
let formatWhole (content: string) : Program<string> = FmtWhole(content, Pure)

let formatRanges (content: string) (ranges: LineRange list) : Program<string * int> =
    FmtRanges(content, ranges, Pure)

let logLine (s: string) : Program<unit> = LogLine(s, Pure)

type ProgramBuilder() =
    member _.Return(x) = Pure x
    member _.ReturnFrom(p: Program<'a>) = p
    member _.Bind(p, f) = bind f p
    member _.Zero() = Pure()
    member _.Delay(f: unit -> Program<'a>) = f ()
    member _.Combine(p: Program<unit>, q: Program<'a>) = bind (fun () -> q) p

let program = ProgramBuilder()
