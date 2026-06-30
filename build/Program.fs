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

let private ensureVsWhereOnPath () =
    if isWindows then
        let installerDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) </> "Microsoft Visual Studio" </> "Installer"
        let vswhere = installerDir </> "vswhere.exe"

        if File.Exists vswhere then
            let currentPath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
            let pathParts = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

            if not (pathParts |> Array.exists (fun p -> String.Equals(p.TrimEnd(Path.DirectorySeparatorChar), installerDir, StringComparison.OrdinalIgnoreCase))) then
                Environment.SetEnvironmentVariable("PATH", installerDir + string Path.PathSeparator + currentPath)

let pluginName = "roslyn-lsp-hook"
let pluginSrc = "plugin"
let distRoot = "dist"
let distPlugin = distRoot </> pluginName
let distPluginVsCode = distRoot </> (pluginName + "-vscode")
let publishRoot = "publish"
let csharpPublish = publishRoot </> "CSharpLintHook"
let astGrepOutlinePublish = publishRoot </> "AstGrepOutline"
let csharpProj = "CSharpLintHook" </> "CSharpLintHook.fsproj"
let astGrepOutlineProj = "AstGrepOutline" </> "AstGrepOutline.fsproj"
let pwshPublish = publishRoot </> "PwshLintHook"
let pwshProj = "PwshLintHook" </> "PwshLintHook.fsproj"
/// PwshLintHook ships in its own plugin subfolder: its System.Management.Automation
/// dependency closure must not collide with the CSharpLintHook DLLs flattened into the
/// plugin root.
let pwshDistSubdir = "PwshLintHook"
let fffMcpPublish = publishRoot </> "fff-mcp"
/// The fff MCP server is installed from fff's own installer script, fetched at build
/// time (so it always tracks the latest release) and pointed at our staging dir. The
/// plugin ships its own mcpServers config, so the script's editor-setup hints and PATH
/// persistence are unused.
let fffMcpInstallerUrl =
    if isWindows then
        "https://raw.githubusercontent.com/dmtrKovalenko/fff/main/install-mcp.ps1"
    else
        "https://raw.githubusercontent.com/dmtrKovalenko/fff/main/install-mcp.sh"
/// The binary name fff's installer drops into the install dir, OS-specific.
let fffMcpBinaryName = if isWindows then "fff-mcp.exe" else "fff-mcp"
let solution = "CSharpLintHook.slnx"
let astGrepSkillRepo = "https://github.com/ast-grep/agent-skill.git"
let astGrepSkillRepoDir = publishRoot </> "ast-grep-agent-skill"
let astGrepSkillRepoPath = "ast-grep" </> "skills" </> "ast-grep"
let astGrepSkillPlugin = pluginSrc </> "skills" </> "ast-grep"

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

let private runRaw command args =
    CreateProcess.fromRawCommand command args
    |> CreateProcess.withWorkingDirectory "."
    |> CreateProcess.ensureExitCode
    |> Proc.run
    |> ignore

/// Download a URL to a file. Used to fetch fff's installer script at build time.
let private downloadFile (url: string) (dest: string) =
    use client = new System.Net.Http.HttpClient()
    client.Timeout <- TimeSpan.FromMinutes 5.0
    client.DefaultRequestHeaders.UserAgent.ParseAdd "fff-mcp-installer"
    File.WriteAllBytes(dest, client.GetByteArrayAsync(url).GetAwaiter().GetResult())

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

/// Point the `fff` MCP server's `command` at the OS-correct binary under the given
/// plugin-root token (`${PLUGIN_ROOT}` for the CLI, `${CLAUDE_PLUGIN_ROOT}` for VS
/// Code). No-op when the manifest declares no `fff` server.
let private patchFffCommand (manifest: JsonNode) (rootToken: string) =
    match manifest.["mcpServers"] with
    | null -> ()
    | servers ->
        match servers.["fff"] with
        | null -> ()
        | fff -> fff.["command"] <- JsonValue.Create(sprintf "%s/%s" rootToken fffMcpBinaryName)

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

                            for field in [ "type"; "matcher"; "cwd"; "timeoutSec"; "env" ] do
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
        patchFffCommand manifest "${CLAUDE_PLUGIN_ROOT}"
        File.WriteAllText(destPath, manifest.ToJsonString jsonWriteOptions)

let private copyCliPluginScaffold () =
    Directory.ensure distPlugin
    Shell.copyFile (distPlugin </> "plugin.json") (pluginSrc </> "plugin.json")
    Shell.copyFile (distPlugin </> "hooks.json") (pluginSrc </> "hooks.json")

    // Rewrite the fff MCP command to the OS-correct binary under ${PLUGIN_ROOT}.
    let manifestPath = distPlugin </> "plugin.json"

    match JsonNode.Parse(File.ReadAllText manifestPath) with
    | null -> ()
    | manifest ->
        patchFffCommand manifest "${PLUGIN_ROOT}"
        File.WriteAllText(manifestPath, manifest.ToJsonString jsonWriteOptions)

    Shell.rm_rf (distPlugin </> "skills")
    Shell.rm_rf (distPlugin </> "scripts")
    Shell.copyDir (distPlugin </> "skills") (pluginSrc </> "skills") (fun _ -> true)

let private copyVsCodePluginScaffold () =
    Directory.ensure distPluginVsCode
    Directory.ensure (distPluginVsCode </> ".claude-plugin")
    Directory.ensure (distPluginVsCode </> "hooks")

    writeVsCodeManifest (pluginSrc </> "plugin.json") (distPluginVsCode </> ".claude-plugin" </> "plugin.json")
    writeVsCodeHooks (pluginSrc </> "hooks.json") (distPluginVsCode </> "hooks" </> "hooks.json")
    Shell.rm_rf (distPluginVsCode </> "skills")
    Shell.rm_rf (distPluginVsCode </> "scripts")
    Shell.copyDir (distPluginVsCode </> "skills") (pluginSrc </> "skills") (fun _ -> true)

let initTargets () =
    Target.create "Clean" (fun _ -> Shell.cleanDirs [ distRoot; publishRoot ])

    Target.create "Test" (fun _ ->
        solution
        |> DotNet.test (fun o ->
            { o with
                Configuration = DotNet.BuildConfiguration.Release
                MSBuildParams = { o.MSBuildParams with DisableInternalBinLog = true } }))

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

    // AstGrepOutline -> Native AOT single binary (PublishAot is set in its fsproj).
    // The postToolUse `grep|view` hook runs this binary, which ensures `@ast-grep/cli`
    // is installed globally via `npm i -g` and then shells out to `ast-grep` to outline
    // the files/folders the tool just touched.
    Target.create "PublishAstGrepOutline" (fun _ ->
        Shell.cleanDir astGrepOutlinePublish

        astGrepOutlineProj
        |> publish (fun o ->
            { o with
                Runtime = Some rid
                OutputPath = Some astGrepOutlinePublish })

        // The shippable artifact is the single native binary; drop debug symbols.
        for d in Directory.GetDirectories(astGrepOutlinePublish, "*.dSYM") do
            Shell.rm_rf d

        for f in Directory.GetFiles(astGrepOutlinePublish, "*.pdb") do
            File.Delete f)

    // PwshLintHook -> framework-dependent (System.Management.Automation's PowerShell
    // parser is not AOT-safe). Portable DLLs, no native apphost; invoked via
    // `dotnet PwshLintHook.dll`. Published *with the RID* because SMA ships its
    // implementation assembly only under runtimes/<rid>/lib (the RID-agnostic asset is
    // a compile-time ref), so a RID-less publish would omit it. Shipped in its own
    // subfolder so its dependency closure stays isolated from the plugin root.
    Target.create "PublishPwshLintHook" (fun _ ->
        Shell.cleanDir pwshPublish

        pwshProj
        |> publish (fun o ->
            { o with
                Runtime = Some rid
                SelfContained = Some false
                OutputPath = Some pwshPublish
                Common =
                    { o.Common with
                        CustomParams = Some "-p:UseAppHost=false -p:SatelliteResourceLanguages=en" } })

        for f in Directory.GetFiles(pwshPublish, "*.pdb") do
            File.Delete f)

    // fff-mcp -> the FFF MCP server. Rather than build the Rust workspace (which needs a
    // Rust/Zig/LLVM toolchain), fetch fff's own installer script at build time and run it
    // pointed at our staging dir; it downloads the prebuilt release binary. The plugin
    // ships its own mcpServers config, so the installer's editor-setup output and PATH
    // persistence are ignored (we pass -PathScope None on Windows; the Unix script does
    // not persist PATH).
    Target.create "FetchFffMcp" (fun _ ->
        Directory.ensure publishRoot
        Shell.cleanDir fffMcpPublish
        let installDir = Path.GetFullPath fffMcpPublish

        let scriptPath =
            Path.Combine(Path.GetTempPath(), (if isWindows then "fff-install-mcp.ps1" else "fff-install-mcp.sh"))

        downloadFile fffMcpInstallerUrl scriptPath

        let runner =
            if isWindows then
                CreateProcess.fromRawCommand
                    "powershell"
                    [ "-NoProfile"
                      "-ExecutionPolicy"
                      "Bypass"
                      "-File"
                      scriptPath
                      "-InstallDir"
                      installDir
                      "-PathScope"
                      "None" ]
            else
                CreateProcess.fromRawCommand "bash" [ scriptPath ]
                |> CreateProcess.setEnvironmentVariable "FFF_MCP_INSTALL_DIR" installDir

        runner
        |> CreateProcess.withWorkingDirectory "."
        |> CreateProcess.ensureExitCode
        |> Proc.run
        |> ignore

        let staged = fffMcpPublish </> fffMcpBinaryName

        if not (File.Exists staged) then
            failwithf "fff installer did not produce %s" staged

        setExecutable staged)

    Target.create "FetchAstGrepSkill" (fun _ ->
        Directory.ensure publishRoot
        Shell.rm_rf astGrepSkillRepoDir
        Shell.cleanDir astGrepSkillPlugin

        runRaw "git" [ "clone"; "--depth"; "1"; astGrepSkillRepo; astGrepSkillRepoDir ]
        Shell.copyDir astGrepSkillPlugin (astGrepSkillRepoDir </> astGrepSkillRepoPath) (fun _ -> true)
        Shell.rm_rf astGrepSkillRepoDir)

    // Assemble the installable plugin folder: manifest + hooks + skill, with the
    // two binaries flattened into the plugin root (where hooks.json resolves them).
    Target.create "Plugin" (fun _ ->
        Shell.cleanDir distPlugin
        copyCliPluginScaffold ()

        Shell.copyDir distPlugin csharpPublish (fun _ -> true)
        Shell.copyDir distPlugin astGrepOutlinePublish (fun _ -> true)
        Shell.copyDir (distPlugin </> pwshDistSubdir) pwshPublish (fun _ -> true)
        Shell.copyDir distPlugin fffMcpPublish (fun _ -> true)

        setExecutable (distPlugin </> "AstGrepOutline")
        setExecutable (distPlugin </> fffMcpBinaryName)

        Trace.tracefn "Assembled plugin '%s' at %s (rid=%s)" pluginName distPlugin rid

        // VS Code agent-plugin variant (Claude plugin format) assembled alongside
        // the CLI folder. Same binaries and skill; the manifest and hooks are
        // translated to the format VS Code's plugin loader expects.
        Shell.cleanDir distPluginVsCode
        copyVsCodePluginScaffold ()

        Shell.copyDir distPluginVsCode csharpPublish (fun _ -> true)
        Shell.copyDir distPluginVsCode astGrepOutlinePublish (fun _ -> true)
        Shell.copyDir (distPluginVsCode </> pwshDistSubdir) pwshPublish (fun _ -> true)
        Shell.copyDir distPluginVsCode fffMcpPublish (fun _ -> true)

        setExecutable (distPluginVsCode </> "AstGrepOutline")
        setExecutable (distPluginVsCode </> fffMcpBinaryName)

        Trace.tracefn "Assembled VS Code plugin variant at %s (rid=%s)" distPluginVsCode rid)

    Target.create "Default" ignore

    // Build graph
    "PublishCSharp" ==> "Plugin" |> ignore
    "PublishAstGrepOutline" ==> "Plugin" |> ignore
    "PublishPwshLintHook" ==> "Plugin" |> ignore
    "FetchFffMcp" ==> "Plugin" |> ignore
    "FetchAstGrepSkill" ==> "Plugin" |> ignore

    "Clean" ==> "Default" |> ignore
    "Test" ==> "Default" |> ignore
    "Plugin" ==> "Default" |> ignore

    // Ordering when several of these run in one invocation
    "Clean" ?=> "Test" |> ignore
    "Clean" ?=> "PublishCSharp" |> ignore
    "Clean" ?=> "PublishAstGrepOutline" |> ignore
    "Clean" ?=> "PublishPwshLintHook" |> ignore
    "Clean" ?=> "FetchFffMcp" |> ignore
    "Clean" ?=> "FetchAstGrepSkill" |> ignore
    "Test" ?=> "PublishCSharp" |> ignore
    "Test" ?=> "PublishAstGrepOutline" |> ignore
    "Test" ?=> "PublishPwshLintHook" |> ignore
    "Test" ?=> "FetchFffMcp" |> ignore
    "Test" ?=> "FetchAstGrepSkill" |> ignore

[<EntryPoint>]
let main argv =
    // Keep the execution context free of our positional target name so FAKE's
    // own argument parser can't misread it; we resolve the target ourselves.
    Context.FakeExecutionContext.Create false "build.fsx" []
    |> Context.RuntimeContext.Fake
    |> Context.setExecutionContext

    ensureVsWhereOnPath ()
    initTargets ()

    // Accept a bare target name (e.g. `build.sh Plugin`); fall back to Default.
    let target =
        argv
        |> Array.tryFind (fun a -> not (a.StartsWith "-"))
        |> Option.defaultValue "Default"

    Target.run 1 target []
    0
