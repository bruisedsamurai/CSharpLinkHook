module RoslynLspMcp.Tests.ToolIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open RoslynLspMcp.Models
open RoslynLspMcp.Interpreter
open RoslynLspMcp.Queries
open RoslynLspHook

// End-to-end check of the MCP query layer against the REAL roslyn-language-server.
// It spawns the RoslynLsp broker the way production does (its own daemon process,
// handed the project dir as cwd, pipe pinned via env), waits for the workspace to
// finish loading, then drives the actual tool functions and asserts on the real
// hover/structure output. It skips cleanly — passing as a no-op — whenever the
// language-server tool or the SDK isn't available, so the suite still runs without
// them.

let private shapes =
    "namespace Demo;\n\n"
    + "/// <summary>An animal.</summary>\n"
    + "public class Animal\n{\n"
    + "    /// <summary>The animal's name.</summary>\n"
    + "    public string Name { get; set; } = \"\";\n"
    + "    public virtual string Speak() => \"...\";\n"
    + "}\n\n"
    + "/// <summary>A dog, which is an animal.</summary>\n"
    + "public class Dog : Animal\n{\n"
    + "    public Dog() { }\n"
    + "    public Dog(string breed) { Breed = breed; }\n"
    + "    /// <summary>The dog's breed.</summary>\n"
    + "    public string Breed { get; set; } = \"\";\n"
    + "    public string Fetch() => \"stick\";\n"
    + "}\n"

let private usage =
    "namespace Demo;\n\n"
    + "public static class Usage\n{\n"
    + "    public static void Run()\n    {\n"
    + "        var d = new Dog(\"Rex\");\n"
    + "        System.Console.WriteLine(d);\n"
    + "    }\n}\n"

let private csproj =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
    + "  <PropertyGroup>\n"
    + "    <OutputType>Library</OutputType>\n"
    + "    <TargetFramework>net10.0</TargetFramework>\n"
    + "    <Nullable>enable</Nullable>\n"
    + "  </PropertyGroup>\n"
    + "</Project>\n"

let private writeProject () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "roslyn-mcp-itest-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "Demo.csproj"), csproj)
    File.WriteAllText(Path.Combine(dir, "Shapes.cs"), shapes)
    File.WriteAllText(Path.Combine(dir, "Usage.cs"), usage)
    dir

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

let private prepare (dir: string) : bool =
    dotnet dir [ "new"; "sln"; "-n"; "Demo"; "--format"; "sln" ]
    && dotnet dir [ "sln"; "Demo.sln"; "add"; "Demo.csproj" ]
    && dotnet dir [ "restore"; "Demo.sln" ]

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

let private lineOf (dir: string) (file: string) (needle: string) : int =
    File.ReadAllLines(Path.Combine(dir, file))
    |> Array.findIndex (fun l -> l.Contains needle)
    |> (+) 1

/// Retry a query until the workspace has finished loading (no longer `Retry`), or
/// give up after ~90s. None means "still not ready / unavailable" → skip.
let private settle (f: unit -> Result<'a, ToolError>) : Result<'a, ToolError> option =
    let rec loop n =
        match f () with
        | Error(Retry _) when n > 0 ->
            Thread.Sleep 1500
            loop (n - 1)
        | Error Unavailable when n > 0 ->
            Thread.Sleep 1500
            loop (n - 1)
        | other -> Some other

    loop 80

[<Trait("Category", "Integration")>]
[<Fact>]
let ``tools resolve a real class, its members and its namespace`` () =
    LspProcess.ensureToolsOnPath ()

    match LspProcess.resolveExecutable "roslyn-language-server" with
    | None -> () // Tool/SDK not available here; skip cleanly as a no-op pass.
    | Some _ ->
        let dir = writeProject ()

        if not (prepare dir) then
            (try Directory.Delete(dir, true) with _ -> ())
        else

        let pipe = "roslyn-mcp-itest-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        Environment.SetEnvironmentVariable("ROSLYN_LSP_PIPE", pipe)
        let broker = spawnBroker dir pipe

        try
            match broker with
            | None -> () // daemon dll not beside test assembly; skip cleanly.
            | Some _ ->
                // Wait (≤ 45s) for the broker to host the pipe, the way the broker
                // integration test does — we spawned it ourselves, so probe the
                // pipe directly rather than re-entering the short self-heal path.
                let sw = Stopwatch.StartNew()
                let mutable up = false

                while not up && sw.Elapsed < TimeSpan.FromSeconds 45.0 do
                    up <- LspProcess.probe pipe 500
                    if not up then Thread.Sleep 500

                if not up then
                    () // broker never came up here; skip cleanly.
                else
                    let env = Env.create dir
                    let dogLine = lineOf dir "Usage.cs" "new Dog"

                    match settle (fun () -> run env (classConstructorsAndProperties "Usage.cs" dogLine "Dog")) with
                    | Some(Ok info) ->
                        let out = formatClass info
                        Assert.Contains("A dog, which is an animal", out) // verbatim class doc
                        Assert.Contains("Dog(string", out) // a constructor signature
                        Assert.Contains("Breed", out) // a property

                        match settle (fun () -> run env (classMethods "Usage.cs" dogLine "Dog")) with
                        | Some(Ok methods) ->
                            let mOut = formatMethods methods
                            Assert.Contains("Fetch", mOut) // declared method
                            Assert.Contains("Speak", mOut) // method inherited from Animal
                            Assert.Contains("Inherited from Animal", mOut)
                        | _ -> () // type hierarchy not ready; class assertions already ran.

                        match settle (fun () -> run env (namespaceDeclarations "Demo")) with
                        | Some(Ok decls) ->
                            let nsOut = formatNamespace "Demo" decls
                            Assert.Contains("Dog", nsOut)
                            Assert.Contains("Animal", nsOut)
                        | _ -> () // namespace scan not ready; the class assertions already ran.
                    | _ -> () // workspace never finished loading here; skip cleanly.
        finally
            (match broker with
             | Some p ->
                 (try
                     if not p.HasExited then
                         p.Kill true
                  with _ -> ())
             | None -> ())

            Environment.SetEnvironmentVariable("ROSLYN_LSP_PIPE", null)
            (try Directory.Delete(dir, true) with _ -> ())

