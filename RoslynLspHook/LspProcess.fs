module RoslynLspHook.LspProcess

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open RoslynLspHook.Common

/// Try to connect to the server's named pipe within `timeoutMs`.
/// True ⇒ the language server is up and accepting connections on that pipe.
let probe (pipeName: string) (timeoutMs: int) : bool =
    try
        use client =
            new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

        client.Connect timeoutMs
        client.IsConnected
    with _ ->
        false

/// Shell-quote a single argument for a POSIX `/bin/sh -c` command line.
let private shQuote (s: string) : string =
    "'" + s.Replace("'", "'\\''") + "'"

/// Launch the server detached so it outlives this short-lived hook process and
/// keeps hosting the pipe for subsequent postToolUse invocations. Best-effort:
/// any failure is swallowed (the hook must never break the agent loop).
let private startDetached (cfg: LspConfig) : unit =
    match cfg.Command with
    | [] -> ()
    | exe :: args ->
        try
            let logPath = Path.Combine(Path.GetTempPath(), sprintf "roslyn-lsp-%s.log" cfg.PipeName)

            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                // `start /b` runs the server without a new window and returns at once.
                let psi =
                    ProcessStartInfo("cmd.exe", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cfg.Cwd)

                psi.ArgumentList.Add "/c"
                psi.ArgumentList.Add "start"
                psi.ArgumentList.Add ""
                psi.ArgumentList.Add "/b"
                psi.ArgumentList.Add exe
                for a in args do
                    psi.ArgumentList.Add a

                Process.Start psi |> ignore
            else
                // nohup + `&` under /bin/sh detaches the server from this process.
                let full = cfg.Command |> List.map shQuote |> String.concat " "
                let script = sprintf "nohup %s > %s 2>&1 &" full (shQuote logPath)
                let psi = ProcessStartInfo("/bin/sh", UseShellExecute = false, WorkingDirectory = cfg.Cwd)
                psi.ArgumentList.Add "-c"
                psi.ArgumentList.Add script
                use p = new Process(StartInfo = psi)
                p.Start() |> ignore
                p.WaitForExit() // /bin/sh returns immediately after backgrounding.
        with _ ->
            ()

/// Start the server only if its pipe is not already connectable. This makes
/// sessionStart idempotent across resumes and avoids duplicate servers.
let ensureStarted (cfg: LspConfig) : unit =
    if not (probe cfg.PipeName 300) then
        startDetached cfg
