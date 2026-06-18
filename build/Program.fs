module Build.Program

open System
open System.IO
open System.Runtime.InteropServices
open Fake.Core
open Fake.Core.TargetOperators
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.DotNet
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Encodings.Web

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
let distPluginVsCode = distRoot </> (pluginName + "-vscode")
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

// ---------------------------------------------------------------------------
// VS Code agent-plugin variant. VS Code loads plugin hooks in Claude Code
// format, which differs from the Copilot CLI schema in plugin/hooks.json:
//   * event keys are PascalCase (SessionStart, PostToolUse), not camelCase
//   * each entry carries a single `command`, not split bash/powershell keys
//   * the plugin-root token is ${CLAUDE_PLUGIN_ROOT}, not ${PLUGIN_ROOT}
// The VS Code files are derived from the CLI sources so they can't drift.
// ---------------------------------------------------------------------------

let private jsonWriteOptions =
    JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private vsCodeEventName (cliName: string) =
    match cliName with
    | "sessionStart" -> "SessionStart"
    | "sessionEnd" -> "SessionEnd"
    | "userPromptSubmitted" -> "UserPromptSubmit"
    | "preToolUse" -> "PreToolUse"
    | "postToolUse" -> "PostToolUse"
    | "errorOccurred" -> "ErrorOccurred"
    | other when other.Length > 0 -> string (Char.ToUpperInvariant other.[0]) + other.Substring 1
    | other -> other

/// Collapse the CLI's OS-split bash/powershell command into the single command
/// VS Code expects, picking the variant for this build's target OS and rewriting
/// the plugin-root token.
let private toVsCodeCommand (entry: JsonObject) =
    let pick (key: string) =
        match entry.[key] with
        | null -> None
        | node -> Some(node.GetValue<string>())

    let chosen =
        if isWindows then pick "powershell" |> Option.orElse (pick "bash")
        else pick "bash" |> Option.orElse (pick "powershell")

    chosen |> Option.map (fun c -> c.Replace("${PLUGIN_ROOT}", "${CLAUDE_PLUGIN_ROOT}"))

let private writeVsCodeHooks (srcPath: string) (destPath: string) =
    let outHooks = JsonObject()

    match JsonNode.Parse(File.ReadAllText srcPath) with
    | null -> ()
    | root ->
        match root.["hooks"] with
        | null -> ()
        | hooksNode ->
            for evt in hooksNode.AsObject() do
                match evt.Value with
                | null -> ()
                | arr ->
                    let outArr = JsonArray()

                    for entryNode in arr.AsArray() do
                        match entryNode with
                        | null -> ()
                        | node ->
                            let entry = node.AsObject()
                            let outEntry = JsonObject()

                            for field in [ "type"; "cwd"; "timeoutSec"; "env" ] do
                                match entry.[field] with
                                | null -> ()
                                | v -> outEntry.[field] <- v.DeepClone()

                            match toVsCodeCommand entry with
                            | Some cmd -> outEntry.["command"] <- JsonValue.Create cmd
                            | None -> ()

                            outArr.Add outEntry

                    outHooks.[vsCodeEventName evt.Key] <- outArr

    let outRoot = JsonObject()
    outRoot.["hooks"] <- outHooks
    File.WriteAllText(destPath, outRoot.ToJsonString jsonWriteOptions)

let private writeVsCodeManifest (srcPath: string) (destPath: string) =
    match JsonNode.Parse(File.ReadAllText srcPath) with
    | null -> ()
    | manifest ->
        manifest.["hooks"] <- JsonValue.Create "hooks/hooks.json"
        File.WriteAllText(destPath, manifest.ToJsonString jsonWriteOptions)

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

        Trace.tracefn "Assembled plugin '%s' at %s (rid=%s)" pluginName distPlugin rid

        // VS Code agent-plugin variant (Claude plugin format) assembled alongside
        // the CLI folder. Same binaries and skill; the manifest and hooks are
        // translated to the format VS Code's plugin loader expects.
        Shell.cleanDir distPluginVsCode
        Directory.ensure (distPluginVsCode </> ".claude-plugin")
        Directory.ensure (distPluginVsCode </> "hooks")

        writeVsCodeManifest (pluginSrc </> "plugin.json") (distPluginVsCode </> ".claude-plugin" </> "plugin.json")
        writeVsCodeHooks (pluginSrc </> "hooks.json") (distPluginVsCode </> "hooks" </> "hooks.json")
        Shell.copyDir (distPluginVsCode </> "skills") (pluginSrc </> "skills") (fun _ -> true)

        Shell.copyDir distPluginVsCode roslynPublish (fun _ -> true)
        Shell.copyDir distPluginVsCode csharpPublish (fun _ -> true)

        setExecutable (distPluginVsCode </> "RoslynLspHook")

        Trace.tracefn "Assembled VS Code plugin variant at %s (rid=%s)" distPluginVsCode rid)

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
