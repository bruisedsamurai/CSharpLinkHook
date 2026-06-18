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
