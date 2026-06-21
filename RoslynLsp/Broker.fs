module RoslynLspHook.Broker

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open RoslynLspHook.Common
open RoslynLspHook.Lsp

// The long-lived broker. It owns one warm Roslyn language server, launched as a
// `--stdio` child it drives over the child's redirected stdin/stdout, and it
// HOSTS the named pipe that short-lived postToolUse hooks connect to. The hooks
// speak the tiny broker protocol (Lsp.brokerDiagRequest/…); the broker translates
// each request into real LSP against the warm server and replies with serialized
// diagnostics. This is the piece that fixes the old "both sides are pipe clients"
// topology where nothing hosted the pipe.
//
// Concurrency: a single background pump reads the child's stdout (answering the
// server's own requests, caching publishDiagnostics, and completing the responses
// we await by id). The host loop serves one pipe client at a time. All writes to
// the child's stdin are serialized through one semaphore.

let private logBroker (cfg: LspConfig) (msg: string) : unit =
    LspProcess.logSetup cfg ("[broker] " + msg)

/// Run a best-effort side effect, swallowing any exception. Avoids the leading-`(`
/// `try`-as-statement idiom that is ambiguous inside computation expressions.
let private swallow (action: unit -> unit) : unit =
    try
        action ()
    with _ ->
        ()

/// Build the `roslyn-language-server --stdio …` child start info, with the
/// broker's own pid as `--clientProcessId` so the server dies if the broker dies.
let private childStartInfo (cfg: LspConfig) (selfPid: int) : ProcessStartInfo option =
    match cfg.Command with
    | [] -> None
    | exe :: args ->
        match LspProcess.resolveExecutable exe with
        | None -> None
        | Some resolved ->
            let psi =
                ProcessStartInfo(
                    resolved,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = cfg.Cwd
                )

            for a in args do
                psi.ArgumentList.Add a

            psi.ArgumentList.Add "--clientProcessId"
            psi.ArgumentList.Add(string selfPid)
            Some psi

/// Run the broker until its Roslyn child exits or `externalCt` is cancelled.
/// Single-instance: if the pipe is already served, this returns at once without
/// starting a second server. Never throws (best-effort; the hook must not break).
let runBrokerCancellable (cfg: LspConfig) (externalCt: CancellationToken) : unit =
    try
        if LspProcess.probe cfg.PipeName 300 then
            logBroker cfg "pipe already served; exiting (single-instance)"
        else
            LspProcess.ensureToolsOnPath ()
            let selfPid = Process.GetCurrentProcess().Id

            match childStartInfo cfg selfPid with
            | None -> logBroker cfg "roslyn-language-server not resolvable; broker not started"
            | Some psi ->
                use lifetime = CancellationTokenSource.CreateLinkedTokenSource externalCt
                let ct = lifetime.Token
                use proc = new Process(StartInfo = psi)
                proc.EnableRaisingEvents <- true
                proc.Exited.Add(fun _ -> swallow (fun () -> lifetime.Cancel()))
                proc.ErrorDataReceived.Add(fun _ -> ()) // drain stderr → no buffer-fill deadlock

                proc.Start() |> ignore
                proc.BeginErrorReadLine()
                logBroker cfg $"started server pid={proc.Id} (cwd={cfg.Cwd})"

                let stdin = proc.StandardInput.BaseStream
                let stdout = proc.StandardOutput.BaseStream
                let writeLock = new SemaphoreSlim(1, 1)
                let pending = ConcurrentDictionary<int, TaskCompletionSource<string>>()
                let publishCache = ConcurrentDictionary<string, Diagnostic list>()
                // Set once the server reports `workspace/projectInitializationComplete`.
                // Passthrough (`handleLsp`) only checks this flag — it never blocks: if
                // the workspace is still loading the request fails fast so the caller can
                // retry once the warm server has finished loading.
                let initialized = new ManualResetEventSlim(false)
                let mutable nextId = 100
                let allocId () = Interlocked.Increment(&nextId)

                // Serialize every write to the child's stdin (pump + host both write).
                let writeChild (payload: string) : unit =
                    writeLock.Wait()

                    try
                        try
                            Wire.writeMessage stdin payload ct |> Async.RunSynchronously
                        with _ ->
                            ()
                    finally
                        writeLock.Release() |> ignore

                // Send a request and wait (≤ timeout) for the matching response.
                let request (payload: string) (id: int) (timeoutMs: int) : string option =
                    let tcs = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    pending[id] <- tcs
                    writeChild payload

                    let ok =
                        try
                            Task.WaitAny([| tcs.Task :> Task |], timeoutMs, ct) = 0
                        with _ ->
                            false

                    pending.TryRemove id |> ignore

                    if ok && tcs.Task.IsCompletedSuccessfully then
                        Some tcs.Task.Result
                    else
                        None

                // Background pump: read the child's stdout for the broker's life.
                let pump =
                    async {
                        let mutable running = true

                        while running && not ct.IsCancellationRequested do
                            let! msgOpt = Wire.readMessage stdout ct

                            match msgOpt with
                            | None -> running <- false
                            | Some msg ->
                                match classify msg with
                                | ServerRequest(idRaw, method, json) ->
                                    let result =
                                        match method with
                                        | "workspace/configuration" ->
                                            "[" + String.Join(",", Array.create (configurationItemCount json) "null") + "]"
                                        | _ -> "null"

                                    writeChild (stubResponse idRaw result)
                                | Response(Some id, json) ->
                                    match pending.TryRemove id with
                                    | true, tcs -> tcs.TrySetResult json |> ignore
                                    | _ -> ()
                                | Notification("textDocument/publishDiagnostics", json) ->
                                    match diagnosticsFromPublish json with
                                    | Some u, ds -> publishCache[u] <- ds
                                    | None, _ -> ()
                                | Notification("workspace/projectInitializationComplete", _) ->
                                    swallow (fun () -> initialized.Set())
                                | _ -> ()

                        swallow (fun () -> lifetime.Cancel())
                    }

                Async.Start(pump, ct)

                // Drive initialize/initialized once, then scope to the sole solution.
                let initialize () : bool =
                    let initId = allocId ()

                    match request (initializeRequest initId selfPid cfg.Cwd) initId cfg.WaitMs with
                    | Some _ ->
                        writeChild (initializedNotification ())

                        match LspProcess.soleSolution cfg.Cwd with
                        | Some sln ->
                            writeChild (solutionOpenNotification (fileUri (Path.GetFullPath sln)))
                            logBroker cfg ("opened sole solution " + sln)
                        | None -> logBroker cfg "no single solution; relying on --autoLoadProjects"

                        true
                    | None ->
                        logBroker cfg "initialize timed out"
                        false

                // Per-request handlers (reply bodies for the broker protocol).
                let handleDiag (path: string) : string =
                    try
                        let uri = fileUri path
                        publishCache.TryRemove uri |> ignore
                        let text = try File.ReadAllText path with _ -> ""
                        writeChild (didOpenNotification uri text)
                        let diagId = allocId ()

                        let pull =
                            match request (diagnosticRequest diagId uri) diagId cfg.WaitMs with
                            | Some json -> diagnosticsFromPull json
                            | None -> []

                        let result =
                            if not (List.isEmpty pull) then
                                pull
                            else
                                match publishCache.TryGetValue uri with
                                | true, ds -> ds
                                | _ -> []

                        writeChild (didCloseNotification uri)
                        serializeDiagnostics result
                    with _ ->
                        serializeDiagnostics []

                let handleOpen (path: string) : string =
                    swallow (fun () ->
                        writeChild (solutionOpenNotification (fileUri path))
                        logBroker cfg ("solution/open " + path))

                    brokerOkReply ()

                // Forward an arbitrary LSP request to the warm server. Opens each
                // `openPaths` file (so Roslyn can answer position-based queries),
                // issues the request, then closes them. Returns the raw LSP response
                // JSON on success; the client extracts `result` via `tryLspResult`.
                let handleLsp (method: string) (paramsJson: string) (openPaths: string list) : string =
                    if not initialized.IsSet then
                        // Workspace still loading: fail fast, never block. The caller retries.
                        lspErrorReply "workspace is still loading; retry shortly"
                    else
                        try
                            let opened =
                                openPaths
                                |> List.choose (fun p ->
                                    try
                                        let uri = fileUri p
                                        let text = File.ReadAllText p
                                        writeChild (didOpenNotification uri text)
                                        Some uri
                                    with _ ->
                                        None)

                            let id = allocId ()

                            let reply =
                                match request (lspRequest id method paramsJson) id cfg.WaitMs with
                                | Some json -> json
                                | None -> lspErrorReply "request timed out"

                            for uri in opened do
                                writeChild (didCloseNotification uri)

                            reply
                        with ex ->
                            logBroker cfg (sprintf "lsp passthrough failed (%s): %s" method ex.Message)
                            lspErrorReply ex.Message

                if initialize () then
                    logBroker cfg ("hosting pipe " + cfg.PipeName)

                    try
                        use server =
                            new NamedPipeServerStream(
                                cfg.PipeName,
                                PipeDirection.InOut,
                                1,
                                PipeTransmissionMode.Byte,
                                PipeOptions.Asynchronous
                            )

                        while not ct.IsCancellationRequested do
                            swallow (fun () ->
                                server.WaitForConnectionAsync ct |> Async.AwaitTask |> Async.RunSynchronously)

                            if server.IsConnected then
                                let stream = server :> Stream

                                let reqOpt =
                                    try
                                        Wire.readMessage stream ct |> Async.RunSynchronously
                                    with _ ->
                                        None

                                match reqOpt with
                                | Some reqJson ->
                                    let reply =
                                        match parseBrokerCommand reqJson with
                                        | Diag p -> handleDiag p
                                        | Open p -> handleOpen p
                                        | Lsp(m, p, opens) -> handleLsp m p opens
                                        | Unknown -> serializeDiagnostics []

                                    swallow (fun () ->
                                        Wire.writeMessage stream reply ct |> Async.RunSynchronously)
                                | None -> ()

                                swallow (fun () -> server.Disconnect())
                    with _ ->
                        ()

                swallow (fun () -> if not proc.HasExited then proc.Kill true)

                logBroker cfg "broker exiting"
    with ex ->
        logBroker cfg ("broker error: " + ex.Message)

/// Run the broker until its Roslyn child exits. Used by the `setup`/`broker` verbs.
let runBroker (cfg: LspConfig) : unit =
    runBrokerCancellable cfg CancellationToken.None
