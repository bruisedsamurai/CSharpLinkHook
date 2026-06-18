module RoslynLspHook.Effects

open RoslynLspHook.Common

/// Free-monad program over the side-effecting operations this hook needs.
///
/// As in the sibling CSharpLintHook project, every non-`Pure` case carries its
/// arguments plus a continuation from the effect's result to the remaining
/// program. A `Program<'a>` is therefore an inert description of work; the
/// interpreter (Interpreter.fs) performs the effects, keeping the state machine
/// in Logic.fs pure and testable against a stub interpreter.
type Program<'a> =
    | Pure of 'a
    | ReadStdin of (string -> Program<'a>)
    | WriteStdout of string * (unit -> Program<'a>)
    | LogLine of string * (unit -> Program<'a>)
    | DirExists of string * (bool -> Program<'a>)
    /// Probe whether the language server's named pipe is currently connectable.
    | ProbeLsp of string * (bool -> Program<'a>)
    /// Start the language server in the background (idempotent: a no-op when the
    /// pipe is already connectable). Returns immediately; the server keeps running.
    | LaunchLsp of LspConfig * (unit -> Program<'a>)
    /// Connect to the running server and fetch diagnostics for one file.
    | FetchDiagnostics of LspConfig * string * (Diagnostic list -> Program<'a>)
    /// Connect to the running server and load a solution via `solution/open`.
    | OpenSolution of LspConfig * string * (bool -> Program<'a>)
    /// Spawn the detached background setup worker (install the server if missing,
    /// start it, open the sole solution) and return immediately. Used by
    /// sessionStart, whose own stdout is discarded by the CLI, so the work must
    /// happen as a side effect in a process that outlives the hook.
    | SpawnSetup of LspConfig * (unit -> Program<'a>)

/// Monadic bind: thread the continuation through the program tree.
let rec bind (f: 'a -> Program<'b>) (p: Program<'a>) : Program<'b> =
    match p with
    | Pure x -> f x
    | ReadStdin k -> ReadStdin(fun s -> bind f (k s))
    | WriteStdout(s, k) -> WriteStdout(s, fun () -> bind f (k ()))
    | LogLine(s, k) -> LogLine(s, fun () -> bind f (k ()))
    | DirExists(path, k) -> DirExists(path, fun b -> bind f (k b))
    | ProbeLsp(name, k) -> ProbeLsp(name, fun b -> bind f (k b))
    | LaunchLsp(cfg, k) -> LaunchLsp(cfg, fun () -> bind f (k ()))
    | FetchDiagnostics(cfg, file, k) -> FetchDiagnostics(cfg, file, fun d -> bind f (k d))
    | OpenSolution(cfg, path, k) -> OpenSolution(cfg, path, fun b -> bind f (k b))
    | SpawnSetup(cfg, k) -> SpawnSetup(cfg, fun () -> bind f (k ()))

let map (f: 'a -> 'b) (p: Program<'a>) : Program<'b> = bind (fun x -> Pure(f x)) p

// Smart constructors: each is a single-step program returning the effect result.
let readStdin: Program<string> = ReadStdin Pure
let writeStdout (s: string) : Program<unit> = WriteStdout(s, Pure)
let logLine (s: string) : Program<unit> = LogLine(s, Pure)
let dirExists (path: string) : Program<bool> = DirExists(path, Pure)
let probeLsp (pipeName: string) : Program<bool> = ProbeLsp(pipeName, Pure)
let launchLsp (cfg: LspConfig) : Program<unit> = LaunchLsp(cfg, Pure)

let fetchDiagnostics (cfg: LspConfig) (file: string) : Program<Diagnostic list> =
    FetchDiagnostics(cfg, file, Pure)

let openSolution (cfg: LspConfig) (path: string) : Program<bool> =
    OpenSolution(cfg, path, Pure)

let spawnSetup (cfg: LspConfig) : Program<unit> = SpawnSetup(cfg, Pure)

type ProgramBuilder() =
    member _.Return(x) = Pure x
    member _.ReturnFrom(p: Program<'a>) = p
    member _.Bind(p, f) = bind f p
    member _.Zero() = Pure()
    member _.Delay(f: unit -> Program<'a>) = f ()
    member _.Combine(p: Program<unit>, q: Program<'a>) = bind (fun () -> q) p

let program = ProgramBuilder()
