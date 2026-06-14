module RoslynLspHook.LspClient

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Text
open System.Threading
open RoslynLspHook.Common
open RoslynLspHook.Lsp

// A minimal LSP client that connects to the running Roslyn server over its named
// pipe, performs the initialize handshake (stubbing the few server-initiated
// requests Roslyn sends), opens the edited document, and collects diagnostics
// from either the pull response or a publishDiagnostics notification.

let private connectTimeoutMs = 2000

/// Read one `Content-Length`-framed message. None on stream end or cancellation.
let private readMessage (stream: Stream) (ct: CancellationToken) : Async<string option> =
    async {
        let headerBytes = ResizeArray<byte>(128)
        let one = Array.zeroCreate<byte> 1
        let mutable consecutive = 0
        let mutable ended = false
        let mutable haveHeader = false

        while not haveHeader && not ended do
            let! n = stream.ReadAsync(one, 0, 1, ct) |> Async.AwaitTask

            if n = 0 then
                ended <- true
            else
                let b = one[0]
                headerBytes.Add b

                if b = byte '\r' || b = byte '\n' then
                    consecutive <- consecutive + 1
                    if consecutive = 4 then haveHeader <- true
                else
                    consecutive <- 0

        if ended then
            return None
        else
            match parseContentLength (Encoding.ASCII.GetString(headerBytes.ToArray())) with
            | None -> return None
            | Some len ->
                let body = Array.zeroCreate<byte> len
                let mutable read = 0
                let mutable broke = false

                while read < len && not broke do
                    let! n = stream.ReadAsync(body, read, len - read, ct) |> Async.AwaitTask
                    if n = 0 then broke <- true else read <- read + n

                if read < len then return None else return Some(Encoding.UTF8.GetString body)
    }

let private writeMessage (stream: Stream) (payload: string) (ct: CancellationToken) : Async<unit> =
    async {
        let bytes = frame payload
        do! stream.WriteAsync(bytes, 0, bytes.Length, ct) |> Async.AwaitTask
        do! stream.FlushAsync ct |> Async.AwaitTask
    }

/// Connect to the server and fetch diagnostics for `file`. Returns [] on any
/// failure (server down, timeout, protocol hiccup) so the hook stays silent.
let getDiagnostics (cfg: LspConfig) (file: string) : Async<Diagnostic list> =
    async {
        try
            use client =
                new NamedPipeClientStream(".", cfg.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            try
                do! client.ConnectAsync connectTimeoutMs |> Async.AwaitTask
            with _ ->
                ()

            if not client.IsConnected then
                return []
            else
                use cts = new CancellationTokenSource(cfg.WaitMs)
                let ct = cts.Token
                let stream = client :> Stream
                let uri = fileUri file
                let initId = 1
                let diagId = 2
                let pid = Process.GetCurrentProcess().Id

                let mutable pullDiags: Diagnostic list option = None
                let mutable publishDiags: Diagnostic list = []
                let mutable finished = false

                do! writeMessage stream (initializeRequest initId pid cfg.Cwd) ct

                try
                    while not finished do
                        let! msgOpt = readMessage stream ct

                        match msgOpt with
                        | None -> finished <- true
                        | Some msg ->
                            match classify msg with
                            | ServerRequest(idRaw, method, json) ->
                                let result =
                                    match method with
                                    | "workspace/configuration" ->
                                        "[" + String.Join(",", Array.create (configurationItemCount json) "null") + "]"
                                    | _ -> "null"

                                do! writeMessage stream (stubResponse idRaw result) ct

                            | Response(Some id, _) when id = initId ->
                                // Handshake done: announce, open the edited file, pull diagnostics.
                                do! writeMessage stream (initializedNotification ()) ct
                                let text = try File.ReadAllText file with _ -> ""
                                do! writeMessage stream (didOpenNotification uri text) ct
                                do! writeMessage stream (diagnosticRequest diagId uri) ct

                            | Response(Some id, json) when id = diagId ->
                                let ds = diagnosticsFromPull json
                                pullDiags <- Some ds
                                if not (List.isEmpty ds) then finished <- true

                            | Notification("textDocument/publishDiagnostics", json) ->
                                let (puri, ds) = diagnosticsFromPublish json

                                match puri with
                                | Some u when String.Equals(u, uri, StringComparison.OrdinalIgnoreCase) ->
                                    publishDiags <- ds
                                    if not (List.isEmpty ds) then finished <- true
                                | _ -> ()

                            | _ -> ()
                with _ ->
                    () // deadline hit or stream closed; return whatever we gathered.

                let result =
                    match pullDiags with
                    | Some ds when not (List.isEmpty ds) -> ds
                    | _ -> publishDiags

                return result
        with _ ->
            return []
    }
