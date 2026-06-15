module Build.Program

open System
open System.IO
open System.Runtime.InteropServices
open Fake.Core
open Fake.Core.TargetOperators
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.DotNet

// ---------------------------------------------------------------------------
// Configuration. Paths are relative to the repo root, which is the working
// directory the build.sh / build.cmd wrappers cd into before invoking us.
// ---------------------------------------------------------------------------

/// Target runtime for the AOT client. Defaults to the host RID; override with
/// `RID=<rid>` (e.g. linux-x64) for cross-platform packaging on that host.
let rid =
    match Environment.environVarOrNone "RID" with
    | Some r when r.Trim().Length > 0 -> r
    | _ -> RuntimeInformation.RuntimeIdentifier

let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

let pluginName = "roslyn-lsp-hook"
let pluginSrc = "plugin"
let distRoot = "dist"
let distPlugin = distRoot </> pluginName
let publishRoot = "publish"
let roslynPublish = publishRoot </> "RoslynLspHook"
let csharpPublish = publishRoot </> "CSharpLintHook"
let roslynProj = "RoslynLspHook" </> "RoslynLspHook.fsproj"
let csharpProj = "CSharpLintHook" </> "CSharpLintHook.fsproj"
let solution = "CSharpLintHook.sln"

/// Restore the executable bit that File.Copy drops on Unix.
let private setExecutable (path: string) =
    if not isWindows && File.Exists path then
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute
        )

let private publish setParams project =
    project
    |> DotNet.publish (fun o ->
        let o = setParams { o with Configuration = DotNet.BuildConfiguration.Release }
        // FAKE's bundled MSBuild binlog reader tops out at format v16, but the
        // .NET 10 SDK emits v25; disable the internal binlog so the post-run
        // parse can't blow up an otherwise-successful publish.
        { o with MSBuildParams = { o.MSBuildParams with DisableInternalBinLog = true } })

let initTargets () =
    Target.create "Clean" (fun _ -> Shell.cleanDirs [ distRoot; publishRoot ])

    Target.create "Test" (fun _ ->
        solution
        |> DotNet.test (fun o ->
            { o with
                Configuration = DotNet.BuildConfiguration.Release
                MSBuildParams = { o.MSBuildParams with DisableInternalBinLog = true } }))

    // RoslynLspHook -> Native AOT single binary (PublishAot is set in its fsproj).
    Target.create "PublishRoslyn" (fun _ ->
        Shell.cleanDir roslynPublish

        roslynProj
        |> publish (fun o ->
            { o with
                Runtime = Some rid
                OutputPath = Some roslynPublish })

        // The shippable artifact is the single native binary; drop debug symbols.
        for d in Directory.GetDirectories(roslynPublish, "*.dSYM") do
            Shell.rm_rf d

        for f in Directory.GetFiles(roslynPublish, "*.pdb") do
            File.Delete f)

    // CSharpLintHook -> framework-dependent (Roslyn + MEF are not AOT-safe).
    // Portable DLLs, no native apphost; invoked via `dotnet CSharpLintHook.dll`.
    Target.create "PublishCSharp" (fun _ ->
        Shell.cleanDir csharpPublish

        csharpProj
        |> publish (fun o ->
            { o with
                OutputPath = Some csharpPublish
                Common =
                    { o.Common with
                        CustomParams = Some "-p:UseAppHost=false -p:SatelliteResourceLanguages=en" } })

        // Trim distributable clutter: localized satellites add ~7 MB of strings
        // we don't surface, and debug symbols aren't needed at runtime.
        for f in Directory.GetFiles(csharpPublish, "*.pdb") do
            File.Delete f)

    // Assemble the installable plugin folder: manifest + hooks + skill, with the
    // two binaries flattened into the plugin root (where hooks.json resolves them).
    Target.create "Plugin" (fun _ ->
        Shell.cleanDir distPlugin
        Directory.ensure distPlugin

        Shell.copyFile (distPlugin </> "plugin.json") (pluginSrc </> "plugin.json")
        Shell.copyFile (distPlugin </> "hooks.json") (pluginSrc </> "hooks.json")
        Shell.copyDir (distPlugin </> "skills") (pluginSrc </> "skills") (fun _ -> true)

        Shell.copyDir distPlugin roslynPublish (fun _ -> true)
        Shell.copyDir distPlugin csharpPublish (fun _ -> true)

        setExecutable (distPlugin </> "RoslynLspHook")

        Trace.tracefn "Assembled plugin '%s' at %s (rid=%s)" pluginName distPlugin rid)

    Target.create "Default" ignore

    // Build graph
    "PublishRoslyn" ==> "Plugin" |> ignore
    "PublishCSharp" ==> "Plugin" |> ignore

    "Clean" ==> "Default" |> ignore
    "Test" ==> "Default" |> ignore
    "Plugin" ==> "Default" |> ignore

    // Ordering when several of these run in one invocation
    "Clean" ?=> "Test" |> ignore
    "Clean" ?=> "PublishRoslyn" |> ignore
    "Clean" ?=> "PublishCSharp" |> ignore
    "Test" ?=> "PublishRoslyn" |> ignore
    "Test" ?=> "PublishCSharp" |> ignore

[<EntryPoint>]
let main argv =
    // Keep the execution context free of our positional target name so FAKE's
    // own argument parser can't misread it; we resolve the target ourselves.
    Context.FakeExecutionContext.Create false "build.fsx" []
    |> Context.RuntimeContext.Fake
    |> Context.setExecutionContext

    initTargets ()

    // Accept a bare target name (e.g. `build.sh Plugin`); fall back to Default.
    let target =
        argv
        |> Array.tryFind (fun a -> not (a.StartsWith "-"))
        |> Option.defaultValue "Default"

    Target.run 1 target []
    0
