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

/// The sole top-level `*.sln`/`*.slnx` in `cwd`, if there is exactly one across
/// both extensions. None when there are zero or several (or on any IO error), so
/// callers fall back to `--autoLoadProjects`. Enumerate-and-filter by exact
/// extension rather than a `*.sln` glob, because on Windows `Directory.GetFiles`
/// with `*.sln` also matches `*.slnx` (legacy 8.3 extension matching).
let soleSolution (cwd: string) : string option =
    try
        if String.IsNullOrEmpty cwd || not (Directory.Exists cwd) then
            None
        else
            let isSolution (f: string) =
                let ext = Path.GetExtension f
                String.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
                || String.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase)

            match
                Directory.EnumerateFiles(cwd, "*", SearchOption.TopDirectoryOnly)
                |> Seq.filter isSolution
                |> Seq.truncate 2
                |> Seq.toArray
            with
            | [| one |] -> Some one
            | _ -> None
    with _ ->
        None

/// Shell-quote a single argument for a POSIX `/bin/sh -c` command line.
let private shQuote (s: string) : string =
    "'" + s.Replace("'", "'\\''") + "'"

/// Resolve an executable name to a real file using PATH (plus PATHEXT on Windows),
/// or as a direct path when it already contains a separator. Returns None when
/// nothing is found, so the caller can skip launching quietly instead of letting
/// the shell emit an OS-level "cannot find" error.
let resolveExecutable (exe: string) : string option =
    try
        if exe.IndexOfAny [| '/'; '\\' |] >= 0 then
            if File.Exists exe then Some exe else None
        else
            let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

            let exts =
                if isWindows then
                    match Environment.GetEnvironmentVariable "PATHEXT" with
                    | null
                    | "" -> [ ".EXE"; ".CMD"; ".BAT"; ".COM" ]
                    | pe -> pe.Split(';') |> Array.filter (fun s -> s.Length > 0) |> List.ofArray
                else
                    [ "" ]

            let dirs =
                match Environment.GetEnvironmentVariable "PATH" with
                | null
                | "" -> []
                | p -> p.Split(Path.PathSeparator) |> Array.filter (fun s -> s.Length > 0) |> List.ofArray

            seq {
                for d in dirs do
                    yield Path.Combine(d, exe)

                    if isWindows then
                        for e in exts do
                            yield Path.Combine(d, exe + e)
            }
            |> Seq.tryFind File.Exists
    with _ ->
        None

/// Append a line to this workspace's setup log so the otherwise-invisible
/// detached worker leaves a trace the user can inspect. Best-effort.
let logSetup (cfg: LspConfig) (msg: string) : unit =
    try
        let path = Path.Combine(Path.GetTempPath(), $"roslyn-lsp-{cfg.PipeName}-setup.log")
        let ts = DateTime.Now.ToString "u"
        let line = $"[{ts}] {msg}{Environment.NewLine}"
        File.AppendAllText(path, line)
    with _ ->
        ()

/// Spawn `exe args` detached so it outlives this short-lived hook process. On
/// Windows `start /b` runs it without a new window; elsewhere `nohup … &` under
/// /bin/sh does the same. Best-effort: any failure is swallowed (the hook must
/// never break the agent loop).
let private spawnDetached (cwd: string) (logPath: string) (exe: string) (args: string list) : unit =
    try
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            let psi =
                ProcessStartInfo("cmd.exe", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd)

            psi.ArgumentList.Add "/c"
            psi.ArgumentList.Add "start"
            psi.ArgumentList.Add ""
            psi.ArgumentList.Add "/b"
            psi.ArgumentList.Add exe

            for a in args do
                psi.ArgumentList.Add a

            Process.Start psi |> ignore
        else
            let full = (exe :: args) |> List.map shQuote |> String.concat " "
            let script = $"nohup {full} > {shQuote logPath} 2>&1 &"
            let psi = ProcessStartInfo("/bin/sh", UseShellExecute = false, WorkingDirectory = cwd)
            psi.ArgumentList.Add "-c"
            psi.ArgumentList.Add script
            use p = new Process(StartInfo = psi)
            p.Start() |> ignore
            p.WaitForExit() // /bin/sh returns immediately after backgrounding.
    with _ ->
        ()

/// The dotnet global-tools directory (`~/.dotnet/tools`), where
/// `dotnet tool install --global` drops `roslyn-language-server`. None when it
/// does not exist yet.
let private dotnetToolsDir () : string option =
    try
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

        if String.IsNullOrEmpty home then
            None
        else
            let d = Path.Combine(home, ".dotnet", "tools")
            if Directory.Exists d then Some d else None
    with _ ->
        None

/// Make sure the dotnet global-tools directory is on this process's PATH so a
/// freshly installed `roslyn-language-server` is resolvable without a new shell.
let ensureToolsOnPath () : unit =
    match dotnetToolsDir () with
    | None -> ()
    | Some d ->
        let cur =
            match Environment.GetEnvironmentVariable "PATH" with
            | null -> ""
            | p -> p

        let present =
            cur.Split(Path.PathSeparator)
            |> Array.exists (fun x ->
                String.Equals(
                    x.TrimEnd(Path.DirectorySeparatorChar),
                    d.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase
                ))

        if not present then
            Environment.SetEnvironmentVariable("PATH", d + string Path.PathSeparator + cur)

/// Ensure the `roslyn-language-server` .NET global tool is installed. If it is
/// already resolvable we do nothing; otherwise we run
/// `dotnet tool install --global roslyn-language-server --prerelease` and wait
/// for it. All progress is written to the workspace setup log. Best-effort.
let ensureInstalled (cfg: LspConfig) : unit =
    try
        ensureToolsOnPath ()

        match resolveExecutable "roslyn-language-server" with
        | Some p -> logSetup cfg $"roslyn-language-server already present at {p}"
        | None ->
            logSetup cfg "roslyn-language-server not found; running: dotnet tool install --global roslyn-language-server --prerelease"

            let psi =
                ProcessStartInfo(
                    "dotnet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = cfg.Cwd
                )

            psi.ArgumentList.Add "tool"
            psi.ArgumentList.Add "install"
            psi.ArgumentList.Add "--global"
            psi.ArgumentList.Add "roslyn-language-server"
            psi.ArgumentList.Add "--prerelease"

            use p = new Process(StartInfo = psi)
            p.Start() |> ignore
            let outTask = p.StandardOutput.ReadToEndAsync()
            let errTask = p.StandardError.ReadToEndAsync()

            if p.WaitForExit 300000 then
                logSetup cfg $"install exited with code {p.ExitCode}"
                let out = outTask.Result.Trim()
                let err = errTask.Result.Trim()
                if out.Length > 0 then logSetup cfg ("install stdout: " + out)
                if err.Length > 0 then logSetup cfg ("install stderr: " + err)
            else
                (try p.Kill true with _ -> ())
                logSetup cfg "install timed out after 300s and was killed"

            ensureToolsOnPath ()
    with ex ->
        logSetup cfg ("ensureInstalled error: " + ex.Message)

/// Re-launch this same executable detached with the given args, so heavy setup
/// runs in a process that outlives the sessionStart hook. Returns immediately.
let spawnSetup (cfg: LspConfig) : unit =
    match Environment.ProcessPath with
    | null -> ()
    | self ->
        let logPath = Path.Combine(Path.GetTempPath(), $"roslyn-lsp-{cfg.PipeName}-setup.log")
        spawnDetached cfg.Cwd logPath self [ "setup" ]

/// Ensure the broker is running. The broker hosts the pipe; if its pipe is not
/// connectable we respawn the detached `setup` worker (install-if-missing → become
/// the broker) and return at once. Idempotent and non-blocking, so it is safe to
/// call from every flow and as the postToolUse self-heal.
let ensureBroker (cfg: LspConfig) : unit =
    if not (probe cfg.PipeName 300) then
        spawnSetup cfg
