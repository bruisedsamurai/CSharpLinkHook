module RoslynLspHook.Tests.BrokerIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open Xunit
open RoslynLspHook.Common

// End-to-end check against the REAL roslyn-language-server: spawn the broker the
// way production does — as its own `RoslynLsp` daemon process (handed the project
// dir as its cwd argument) over a throwaway project that has a deliberate compiler
// error, then connect as a broker client and assert we get a `CS####` diagnostic
// back. This exercises the whole new topology (broker process hosts the pipe, owns
// a --stdio child, answers the broker protocol) that the unit tests can only stub.
// Running the broker out-of-process (rather than in-proc) also matches the real
// deployment and avoids starving the test runner's thread pool. It skips cleanly —
// passing as a no-op — when the language server tool or SDK isn't available, so the
// suite still runs without them.

let private csCode = Regex(@"^CS\d+$", RegexOptions.Compiled)

let private hasCsDiagnostic (diags: Diagnostic list) : bool =
    diags
    |> List.exists (fun d ->
        match d.Code with
        | Some c -> csCode.IsMatch c
        | None -> false)

/// Create a throwaway library project whose single source file has a guaranteed
/// semantic error (returning a string where an int is required ⇒ CS0029).
let private writeTempProject () : string * string =
    let dir =
        Path.Combine(Path.GetTempPath(), "roslyn-itest-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    Directory.CreateDirectory dir |> ignore

    let csproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        + "  <PropertyGroup>\n"
        + "    <OutputType>Library</OutputType>\n"
        + "    <TargetFramework>net10.0</TargetFramework>\n"
        + "    <Nullable>disable</Nullable>\n"
        + "  </PropertyGroup>\n"
        + "</Project>\n"

    File.WriteAllText(Path.Combine(dir, "Itest.csproj"), csproj)

    let cs =
        "class Broken\n{\n    int Get()\n    {\n        return \"not an int\";\n    }\n}\n"

    let csPath = Path.Combine(dir, "Broken.cs")
    File.WriteAllText(csPath, cs)
    dir, csPath

/// Run `dotnet args` in `dir`, draining output. True iff it exits 0 within 120s.
let private dotnet (dir: string) (args: string list) : bool =
    try
        let psi =
            ProcessStartInfo(
                "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir
            )

        for a in args do
            psi.ArgumentList.Add a

        use p = new Process(StartInfo = psi)
        p.Start() |> ignore
        p.StandardOutput.ReadToEndAsync() |> ignore
        p.StandardError.ReadToEndAsync() |> ignore
        p.WaitForExit 120000 && p.ExitCode = 0
    with _ ->
        false

/// Wrap the throwaway project in a real `.sln` and restore it. The broker scopes
/// itself to the sole solution and loads it with `solution/open`, which is the
/// deterministic load path (a bare unrestored/solution-less csproj does not
/// reliably produce diagnostics). Returns false if `dotnet` is unavailable or any
/// step fails, so the test can skip cleanly.
let private prepare (dir: string) : bool =
    dotnet dir [ "new"; "sln"; "-n"; "Itest"; "--format"; "sln" ]
    && dotnet dir [ "sln"; "Itest.sln"; "add"; "Itest.csproj" ]
    && dotnet dir [ "restore"; "Itest.sln" ]

/// Spawn the broker daemon (`RoslynLsp`) as its own process, handing it the
/// throwaway project's `dir` EXPLICITLY as its cwd argument and pinning the pipe
/// through an environment variable. The daemon dll sits beside this test assembly
/// thanks to the project reference. Returns the running process, or None if the
/// daemon dll can't be found. stdout/stderr are drained so the child never blocks
/// on a full pipe buffer.
let private spawnBroker (dir: string) (pipe: string) : Process option =
    let dll = Path.Combine(AppContext.BaseDirectory, "RoslynLsp.dll")

    if not (File.Exists dll) then
        None
    else
        let psi =
            ProcessStartInfo(
                "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir
            )

        psi.ArgumentList.Add dll
        psi.ArgumentList.Add dir
        psi.Environment["ROSLYN_LSP_PIPE"] <- pipe
        psi.Environment["ROSLYN_LSP_WAIT_MS"] <- "8000"

        let p = new Process(StartInfo = psi)
        p.OutputDataReceived.Add(fun _ -> ())
        p.ErrorDataReceived.Add(fun _ -> ())
        p.Start() |> ignore
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        Some p

[<Trait("Category", "Integration")>]
[<Fact>]
let ``broker surfaces a real CS diagnostic for a broken C# file`` () =
    RoslynLspHook.LspProcess.ensureToolsOnPath ()

    match RoslynLspHook.LspProcess.resolveExecutable "roslyn-language-server" with
    | None -> () // Tool/SDK not available here; skip cleanly as a no-op pass.
    | Some _ ->
        let dir, csPath = writeTempProject ()

        if not (prepare dir) then
            // No SDK / solution prep failed in this environment; skip cleanly.
            (try Directory.Delete(dir, true) with _ -> ())
        else

        let pipe = "roslyn-itest-" + Guid.NewGuid().ToString("N").Substring(0, 8)

        let cfg =
            { PipeName = pipe
              Cwd = dir
              Command = [ "roslyn-language-server"; "--stdio"; "--autoLoadProjects"; "--logLevel"; "Information" ]
              WaitMs = 20000 }

        match spawnBroker dir pipe with
        | None -> (try Directory.Delete(dir, true) with _ -> ()) // can't locate broker dll; skip.
        | Some broker ->

        try
            // Wait (≤ 45s) for the broker to host the pipe.
            let started = Stopwatch.StartNew()
            let mutable up = false

            while not up && started.Elapsed < TimeSpan.FromSeconds 45.0 do
                up <- RoslynLspHook.LspProcess.probe cfg.PipeName 500
                if not up then Thread.Sleep 500

            Assert.True(up, "broker never started hosting the pipe")

            // Poll diagnostics: the server's FIRST solution load (design-time build,
            // MSBuild + analyzer warmup) can take a couple of minutes cold, so retry
            // generously until we see a CS#### code or time out. Subsequent runs, with
            // a warm server, return on the first attempt.
            let budget = Stopwatch.StartNew()
            let mutable found: Diagnostic list = []

            while not (hasCsDiagnostic found) && budget.Elapsed < TimeSpan.FromSeconds 180.0 do
                let diags = RoslynLspHook.LspClient.getDiagnostics cfg csPath |> Async.RunSynchronously

                if hasCsDiagnostic diags then found <- diags else Thread.Sleep 2000

            Assert.True(
                hasCsDiagnostic found,
                sprintf "expected a CS#### diagnostic; got %A after %.0fs" found budget.Elapsed.TotalSeconds
            )
        finally
            (try if not broker.HasExited then broker.Kill true with _ -> ())
            (try broker.Dispose() with _ -> ())
            (try Directory.Delete(dir, true) with _ -> ())
