module RoslynLspHook.LspClient

open System.IO
open System.IO.Pipes
open System.Threading
open RoslynLspHook.Common
open RoslynLspHook.Lsp

// A thin client of the broker (Broker.fs). The broker hosts the named pipe and
// owns the warm Roslyn server; here we only connect, send one framed broker
// command, read the framed reply, and decode it. All real LSP lives in the broker.
// Every failure path returns the empty/false result so the hook stays silent and
// never breaks the agent loop.

let private connectTimeoutMs = 2000

/// Open a pipe connection to the broker, send `request`, and return its single
/// framed reply. None on any failure (broker down, timeout, protocol hiccup).
let private roundTrip (cfg: LspConfig) (request: string) : Async<string option> =
    async {
        try
            use client =
                new NamedPipeClientStream(".", cfg.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            try
                do! client.ConnectAsync connectTimeoutMs |> Async.AwaitTask
            with _ ->
                ()

            if not client.IsConnected then
                return None
            else
                use cts = new CancellationTokenSource(cfg.WaitMs)
                let ct = cts.Token
                let stream = client :> Stream
                do! Wire.writeMessage stream request ct
                return! Wire.readMessage stream ct
        with _ ->
            return None
    }

/// Ask the broker for diagnostics on `file`. [] on any failure (broker down,
/// timeout, protocol hiccup) so the hook stays silent.
let getDiagnostics (cfg: LspConfig) (file: string) : Async<Diagnostic list> =
    async {
        let! reply = roundTrip cfg (brokerDiagRequest file)
        return reply |> Option.map deserializeDiagnostics |> Option.defaultValue []
    }

/// Ask the broker to scope its warm server to `solutionPath`. false on any failure.
let openSolution (cfg: LspConfig) (solutionPath: string) : Async<bool> =
    async {
        let! reply = roundTrip cfg (brokerOpenRequest solutionPath)
        return reply |> Option.map parseOk |> Option.defaultValue false
    }

/// Forward an arbitrary LSP request through the broker to the warm server,
/// opening `openPaths` (absolute `.cs` files) around the call so position-based
/// queries resolve. Returns the LSP `result` payload as a JSON string, or None
/// when the broker is down, the request failed/timed out, the workspace is still
/// loading, or the server returned no result (absent / JSON `null` / error).
let lspPassthrough
    (cfg: LspConfig)
    (method: string)
    (paramsJson: string)
    (openPaths: string list)
    : Async<string option> =
    async {
        let! reply = roundTrip cfg (brokerLspRequest method paramsJson openPaths)
        return reply |> Option.bind tryLspResult
    }

/// The outcome of a passthrough exchange, distinguishing the cases a caller needs
/// to act on differently: a usable result, a broker-reported error (e.g. the
/// workspace is still loading — retry later), an empty result (resolved nothing),
/// or no reply at all (broker unreachable).
type LspReply =
    | LspResult of string
    | LspNotReady of string
    | LspNoResult
    | LspUnavailable

/// Like `lspPassthrough` but surfaces the full outcome so callers can tell a
/// still-loading workspace (retry) from an unreachable broker or an empty result.
let lspExchange
    (cfg: LspConfig)
    (method: string)
    (paramsJson: string)
    (openPaths: string list)
    : Async<LspReply> =
    async {
        let! reply = roundTrip cfg (brokerLspRequest method paramsJson openPaths)

        match reply with
        | None -> return LspUnavailable
        | Some r ->
            match tryLspResult r with
            | Some result -> return LspResult result
            | None ->
                match tryLspError r with
                | Some msg -> return LspNotReady msg
                | None -> return LspNoResult
    }
