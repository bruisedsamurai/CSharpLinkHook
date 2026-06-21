module RoslynLspMcp.Program

open System.IO
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Console
open RoslynLspMcp.Interpreter
open RoslynLspMcp.Tools

// Entry point: an MCP server over stdio. The host scans this assembly for tool
// types and exposes them. Because the transport owns stdout, ALL logging must go
// to stderr. Tools share a single RoslynSession that talks to the per-workspace
// Roslyn broker (the same one the RoslynLsp plugin warms).

[<EntryPoint>]
let main argv =
    let builder = Host.CreateApplicationBuilder argv

    builder.Logging.AddConsole(fun (o: ConsoleLoggerOptions) -> o.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    builder.Services.AddSingleton<Env>(fun _ -> Env.create (Directory.GetCurrentDirectory()))
    |> ignore

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof<RoslynTools>.Assembly)
    |> ignore

    builder.Build().RunAsync() |> Async.AwaitTask |> Async.RunSynchronously
    0
