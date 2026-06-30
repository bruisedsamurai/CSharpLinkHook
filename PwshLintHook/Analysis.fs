module PwshLintHook.Analysis

open System
open System.Text.RegularExpressions
open PwshLintHook.Pipeline

// Match the pure pipeline model against two replacement patterns and turn a match into
// a deny decision:
//   * filesystem search  (Get-ChildItem on the filesystem)        → use the `fd` CLI
//   * content search      (Select-String, or Get-Content + filter)  → use the `fff` MCP
// Content search takes precedence: a `gci -r | sls TODO` pipeline is fundamentally a
// content search even though it enumerates files first.

/// The outcome of analysing a command. `DenyFd` carries the AST-derived equivalent
/// `fd` command; `DenyFf` carries the fully-composed deny reason.
type Decision =
    | Allow
    | DenyFd of fdCommand: string
    | DenyFf of reason: string

let private unquote (s: string) = s.Trim().Trim('\'', '"')

/// PowerShell provider drives that are *not* the filesystem (registry, env, certs, …).
/// A Get-ChildItem/Get-Content rooted at one of these can't be served by fd/fff, so it
/// is left alone.
let private providerRegex =
    Regex(@"^(env|alias|function|variable|cert|hklm|hkcu|hkey_[a-z_]+|wsman):", RegexOptions.IgnoreCase)

let private looksNonFilesystem (text: string) : bool = providerRegex.IsMatch(unquote text)

let private isGci (s: Stage) = s.Name = "get-childitem"
let private isGc (s: Stage) = s.Name = "get-content"

/// The path-like arguments a stage targets: `-Path`/`-LiteralPath` plus positionals.
let private pathCandidates (s: Stage) : string list =
    (s.Params
     |> List.choose (fun (k, v) -> if k = "path" || k = "literalpath" then Some v else None))
    @ s.Positionals

let private isFilesystemGci (s: Stage) =
    isGci s && not (pathCandidates s |> List.exists looksNonFilesystem)

let private isFilesystemGc (s: Stage) =
    isGc s && not (pathCandidates s |> List.exists looksNonFilesystem)

/// A pipeline is a content search when it runs Select-String, or reads file content
/// (Get-Content) and then filters it.
let private isContentSearch (stages: Stage list) : bool =
    let hasSls = stages |> List.exists (fun s -> s.Name = "select-string")
    let hasFsGc = stages |> List.exists isFilesystemGc
    let hasFilter = stages |> List.exists (fun s -> s.Category = Filter)
    hasSls || (hasFsGc && hasFilter)

/// `*.cs` → `cs` (an fd `-e` extension); anything else → None.
let private extensionFromFilter (value: string) : string option =
    let m = Regex.Match(unquote value, @"^\*\.([A-Za-z0-9_]+)$")
    if m.Success then Some m.Groups.[1].Value else None

/// Best-effort exclusion globs for fd `-E`: alternation/word tokens from a Where-Object
/// `-notmatch '…'` (e.g. `\\(obj|bin)\\` → obj, bin) plus any Get-ChildItem `-Exclude`.
let private exclusionTokens (stages: Stage list) : string list =
    let fromWhere =
        stages
        |> List.filter (fun s -> s.Name = "where-object")
        |> List.collect (fun s ->
            Regex.Matches(s.Text, "-notmatch\\s+(['\"])(.*?)\\1")
            |> Seq.cast<Match>
            |> Seq.collect (fun m ->
                Regex.Matches(m.Groups.[2].Value, @"[A-Za-z0-9_.-]+")
                |> Seq.cast<Match>
                |> Seq.map (fun w -> w.Value))
            |> List.ofSeq)

    let fromExclude =
        stages
        |> List.filter isGci
        |> List.collect (fun s ->
            s.Params
            |> List.choose (fun (k, v) -> if k = "exclude" then Some(unquote v) else None))

    fromWhere @ fromExclude
    |> List.filter (fun t -> t.Length > 0 && not (Seq.forall Char.IsDigit t))
    |> List.distinct

/// Re-quote a path argument for the suggested command when it carries a space or a
/// variable reference; leave already-quoted or simple paths as-is.
let private quotePath (p: string) : string =
    let t = p.Trim()

    if t.Length = 0 then
        t
    elif (t.StartsWith "\"" && t.EndsWith "\"") || (t.StartsWith "'" && t.EndsWith "'") then
        t
    elif Seq.exists (fun c -> c = ' ' || c = '$') t then
        "\"" + t + "\""
    else
        t

/// Compose the equivalent `fd` command from a pipeline's Get-ChildItem stage (and any
/// Where-Object exclusions): `fd [-t f|d] [-e ext…] [-E excl…] [-H -I] . [path]`.
let private buildFdCommand (stages: Stage list) : string =
    let gci = stages |> List.find isFilesystemGci

    let typeFlag =
        if gci.Switches.Contains "file" then [ "-t"; "f" ]
        elif gci.Switches.Contains "directory" then [ "-t"; "d" ]
        else []

    let exts =
        gci.Params
        |> List.filter (fun (k, _) -> k = "filter" || k = "include")
        |> List.choose (fun (_, v) -> extensionFromFilter v)
        |> List.distinct
        |> List.collect (fun e -> [ "-e"; e ])

    let excludes = exclusionTokens stages |> List.collect (fun e -> [ "-E"; e ])

    // Get-ChildItem -Force surfaces hidden + ignored entries; fd hides both by default.
    let hiddenIgnore = if gci.Switches.Contains "force" then [ "-H"; "-I" ] else []

    let path =
        gci.Params
        |> List.tryPick (fun (k, v) -> if k = "path" || k = "literalpath" then Some v else None)
        |> Option.orElse (List.tryHead gci.Positionals)

    let pathPart =
        match path with
        | Some p -> [ quotePath p ]
        | None -> []

    [ "fd" ] @ typeFlag @ exts @ excludes @ hiddenIgnore @ [ "." ] @ pathPart
    |> String.concat " "

/// Compose the deny reason for a filesystem-search match.
let fdReason (fdCommand: string) : string =
    $"Use the `fd` CLI instead of Get-ChildItem for file searches — it is much faster. Equivalent: `%s{fdCommand}`. Note: fd respects .gitignore and skips hidden files by default; add -I and/or -H if you need to match Get-ChildItem exactly."

/// The deny reason for a content-search match.
let private ffReason: string =
    "Use the `fff` MCP (the fast file/content finder) instead of Get-Content/Select-String for searching file contents. Call the fff MCP's search tool with your query rather than piping Get-Content or Select-String."

/// Analyse a PowerShell command string and decide whether to deny it. Pure.
let analyze (commandText: string) : Decision =
    let pipelines = parse commandText

    match pipelines |> List.tryFind isContentSearch with
    | Some _ -> DenyFf ffReason
    | None ->
        match pipelines |> List.tryFind (List.exists isFilesystemGci) with
        | Some stages -> DenyFd(buildFdCommand stages)
        | None -> Allow
