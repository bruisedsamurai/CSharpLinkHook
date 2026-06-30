module PwshLintHook.Pipeline

open System
open System.Management.Automation.Language

// Parse the PowerShell command with the real PowerShell AST parser
// (`System.Management.Automation.Language.Parser`), then reduce every pipeline to a
// pure, AST-free model the analysis can match against. Keeping the AST behind this
// boundary means Analysis (and its tests) never touch System.Management.Automation.

/// The semantic role a pipeline stage plays.
type Category =
    | Source
    | Filter
    | Projection
    | Formatting
    | Other

/// One classified command stage within a pipeline, reduced to the pure data the
/// analysis needs. `Name` is the canonical, alias-resolved, lowercased command name
/// (e.g. "get-childitem"); `Text` is the stage's raw source extent (used for
/// best-effort exclusion extraction from a Where-Object filter).
type Stage =
    { Name: string
      Category: Category
      Switches: Set<string>
      Params: (string * string) list
      Positionals: string list
      Text: string }

/// Common aliases → canonical cmdlet names (all lowercased).
let private aliasMap =
    [ "gci", "get-childitem"
      "ls", "get-childitem"
      "dir", "get-childitem"
      "gc", "get-content"
      "cat", "get-content"
      "type", "get-content"
      "sls", "select-string"
      "where", "where-object"
      "?", "where-object"
      "foreach", "foreach-object"
      "%", "foreach-object"
      "select", "select-object"
      "sort", "sort-object"
      "group", "group-object"
      "ft", "format-table"
      "fl", "format-list"
      "fw", "format-wide" ]
    |> Map.ofList

/// Resolve an alias to its canonical lowercased command name (identity for non-aliases).
let resolveAlias (name: string) : string =
    let lower = name.ToLowerInvariant()

    match Map.tryFind lower aliasMap with
    | Some canonical -> canonical
    | None -> lower

/// Classify a canonical (lowercased) command name into its semantic category.
let classify (canonical: string) : Category =
    if canonical = "where-object" || canonical = "select-string" then
        Filter
    elif
        canonical.StartsWith "format-"
        || canonical = "out-file"
        || canonical = "out-host"
        || canonical = "out-string"
        || canonical = "export-csv"
    then
        Formatting
    elif
        canonical = "select-object"
        || canonical = "sort-object"
        || canonical = "group-object"
        || canonical = "foreach-object"
    then
        Projection
    elif canonical.StartsWith "get-" || canonical.StartsWith "import-" then
        Source
    else
        Other

/// Switch (value-less) parameters for the commands whose arguments we bind. Knowing
/// these lets the binder avoid swallowing a following positional (e.g. the path in
/// `Get-ChildItem -Recurse C:\src`) as a switch's value.
let private switchSetFor (canonical: string) : Set<string> =
    match canonical with
    | "get-childitem" ->
        set
            [ "recurse"
              "file"
              "directory"
              "force"
              "hidden"
              "name"
              "followsymlink"
              "readonly"
              "system"
              "archive" ]
    | "select-string" ->
        set [ "casesensitive"; "simplematch"; "quiet"; "list"; "notmatch"; "allmatches"; "nogroup" ]
    | _ -> Set.empty

/// Reduce one CommandAst to a pure Stage. Parameters written `-Name Value` (space
/// separated) are bound by a switch-aware look-ahead; `-Name:Value` uses the AST's own
/// argument; bare expressions become positionals.
let private extractStage (cmd: CommandAst) : Stage =
    let elements = cmd.CommandElements

    let rawName =
        match cmd.GetCommandName() with
        | null -> if elements.Count > 0 then elements.[0].Extent.Text else ""
        | n -> n

    let canonical = resolveAlias rawName
    let switchSet = switchSetFor canonical

    let mutable switches = Set.empty
    let mutable parameters = []
    let mutable positionals = []
    let mutable i = 1

    while i < elements.Count do
        match box elements.[i] with
        | :? CommandParameterAst as p ->
            let pname = p.ParameterName.ToLowerInvariant()

            match Option.ofObj p.Argument with
            | Some arg ->
                parameters <- (pname, arg.Extent.Text) :: parameters
                i <- i + 1
            | None ->
                let nextIsValue =
                    i + 1 < elements.Count
                    && not (elements.[i + 1] :? CommandParameterAst)
                    && not (switchSet.Contains pname)

                if nextIsValue then
                    parameters <- (pname, elements.[i + 1].Extent.Text) :: parameters
                    i <- i + 2
                else
                    switches <- Set.add pname switches
                    i <- i + 1
        | :? ScriptBlockExpressionAst ->
            // The filter scriptblock (e.g. Where-Object { ... }); captured via `Text`,
            // not treated as a positional path.
            i <- i + 1
        | _ ->
            positionals <- elements.[i].Extent.Text :: positionals
            i <- i + 1

    { Name = canonical
      Category = classify canonical
      Switches = switches
      Params = List.rev parameters
      Positionals = List.rev positionals
      Text = cmd.Extent.Text }

/// Parse a command string and return one Stage list per pipeline found (nested
/// pipelines inside scriptblocks/subexpressions included). Parse errors are tolerated:
/// whatever AST the parser produces is analysed. Pure (the parser performs no IO).
let parse (commandText: string) : Stage list list =
    let mutable tokens = Unchecked.defaultof<Token array>
    let mutable errors = Unchecked.defaultof<ParseError array>
    let ast = Parser.ParseInput(commandText, &tokens, &errors)

    ast.FindAll(Func<Ast, bool>(fun n -> n :? PipelineAst), true)
    |> Seq.cast<PipelineAst>
    |> Seq.map (fun pipe ->
        pipe.PipelineElements
        |> Seq.choose (fun el ->
            match box el with
            | :? CommandAst as c -> Some(extractStage c)
            | _ -> None)
        |> List.ofSeq)
    |> List.ofSeq
