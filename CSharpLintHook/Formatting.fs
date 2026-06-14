module CSharpLintHook.Formatting

open System.Collections.Generic
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Formatting
open Microsoft.CodeAnalysis.Text
open CSharpLintHook.Common

/// Climb from a node to the nearest "safe" formatting unit: the enclosing
/// statement, else member declaration, else type declaration, else the root.
let private safeUnit (root: SyntaxNode) (node: SyntaxNode) : SyntaxNode =
    match node.FirstAncestorOrSelf<StatementSyntax>() with
    | null ->
        match node.FirstAncestorOrSelf<MemberDeclarationSyntax>() with
        | null ->
            match node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() with
            | null -> root
            | t -> t :> SyntaxNode
        | m -> m :> SyntaxNode
    | s -> s :> SyntaxNode

/// Clamp a span into root.FullSpan and make it non-empty so FindNode resolves.
let private clampForFind (root: SyntaxNode) (span: TextSpan) : TextSpan =
    let endMax = root.FullSpan.End
    let s = max 0 (min span.Start endMax)
    let e = max s (min span.End endMax)

    if s = e then
        if e < endMax then TextSpan.FromBounds(s, e + 1)
        elif s > 0 then TextSpan.FromBounds(s - 1, e)
        else TextSpan.FromBounds(0, endMax)
    else
        TextSpan.FromBounds(s, e)

/// For each changed line, map to its safe-unit node's FullSpan. Identical node
/// spans are de-duplicated; disjoint spans are kept separate (not coalesced).
let private targetSpans (root: SyntaxNode) (text: SourceText) (ranges: LineRange list) : TextSpan list =
    let lineCount = text.Lines.Count
    let seen = HashSet<TextSpan>()
    let acc = ResizeArray<TextSpan>()

    for r in ranges do
        let startL = max 0 r.StartLine
        let endL = min (lineCount - 1) r.EndLineInclusive

        for ln in startL..endL do
            let line = text.Lines[ln]
            let span = clampForFind root line.Span
            let node = root.FindNode(span, findInsideTrivia = true, getInnermostNodeForTie = true)
            let fullSpan = (safeUnit root node).FullSpan

            if seen.Add fullSpan then
                acc.Add fullSpan

    List.ofSeq acc

let private makeDocument (text: SourceText) (ws: AdhocWorkspace) : Document =
    let projInfo =
        ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "CSharpLintHook",
            "CSharpLintHook",
            LanguageNames.CSharp
        )

    let project = ws.AddProject projInfo
    ws.AddDocument(project.Id, "Source.cs", text)

/// Format the whole document (used when there is no git base, e.g. new files).
let formatAllAsync (text: SourceText) (ct: CancellationToken) : Async<SourceText> =
    async {
        use ws = new AdhocWorkspace()
        let doc = makeDocument text ws
        let! formatted = Formatter.FormatAsync(doc, cancellationToken = ct) |> Async.AwaitTask
        let! result = formatted.GetTextAsync ct |> Async.AwaitTask
        return result
    }

/// Format only the safe-unit nodes covering the changed lines. Returns the new
/// text and the number of distinct regions formatted.
let formatRangesAsync (text: SourceText) (ranges: LineRange list) (ct: CancellationToken) : Async<SourceText * int> =
    async {
        match ranges with
        | [] -> return (text, 0)
        | _ ->
            use ws = new AdhocWorkspace()
            let doc = makeDocument text ws
            let! rootOpt = doc.GetSyntaxRootAsync ct |> Async.AwaitTask

            match rootOpt with
            | null -> return (text, 0)
            | root ->
                let spans = targetSpans root text ranges

                match spans with
                | [] -> return (text, 0)
                | _ ->
                    let! formatted = Formatter.FormatAsync(doc, spans, cancellationToken = ct) |> Async.AwaitTask
                    let! result = formatted.GetTextAsync ct |> Async.AwaitTask
                    return (result, List.length spans)
    }
