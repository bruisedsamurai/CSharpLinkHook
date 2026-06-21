module RoslynLspMcp.Find

open System
open RoslynLspMcp.LspJson

// Pure helpers for symbol resolution: locate a token on a given line, and climb a
// document-symbol tree to the type that encloses a definition location.

/// The 0-based character offset of the first occurrence of `symbol` on the
/// 1-based `line1` of `text`. None when the line or token is absent. Newlines are
/// normalized so CRLF and LF inputs index identically.
let findToken (text: string) (line1: int) (symbol: string) : int option =
    if line1 < 1 || String.IsNullOrEmpty symbol then
        None
    else
        let lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

        if line1 > lines.Length then
            None
        else
            match lines.[line1 - 1].IndexOf(symbol, StringComparison.Ordinal) with
            | i when i >= 0 -> Some i
            | _ -> None

let private contains (n: SymbolNode) (line: int) (character: int) : bool =
    let afterStart =
        line > n.StartLine || (line = n.StartLine && character >= n.StartCharacter)

    let beforeEnd =
        line < n.EndLine || (line = n.EndLine && character <= n.EndCharacter)

    afterStart && beforeEnd

/// The innermost type-kind node whose range contains the (0-based) position. A
/// go-to-definition lands on the member (or the type's own name), so this climbs
/// to the enclosing class / interface / struct.
let enclosingType (symbols: SymbolNode list) (line: int) (character: int) : SymbolNode option =
    let rec search (nodes: SymbolNode list) (best: SymbolNode option) : SymbolNode option =
        nodes
        |> List.fold
            (fun acc n ->
                let acc =
                    if isTypeKind n.Kind && contains n line character then
                        Some n
                    else
                        acc

                search n.Children acc)
            best

    search symbols None
