module CSharpLintHook.Program

open System
open CSharpLintHook.Common
open CSharpLintHook.Effects

/// Describe the scope of a reformat for CLI status messages: whole-file formats
/// (new/untracked files) have no changed regions, so report them as such rather
/// than the misleading "0 region(s)".
let private describeScope (r: FormatResult) : string =
    if r.WholeFile then
        "whole file"
    else
        sprintf "%d region(s)" r.Regions

let private usage () =
    eprintfn "CSharpLintHook — diff-aware Roslyn formatter"
    eprintfn ""
    eprintfn "Usage:"
    eprintfn "  csharplinthook hook [format]   Read a postToolUse payload from stdin and reformat"
    eprintfn "                                 the changed C# file in place (edit/create tools)."
    eprintfn "  csharplinthook format <file>   Format the changed regions of <file>."
    eprintfn "      --stdout   write formatted text to stdout (default)"
    eprintfn "      --write    rewrite <file> in place if it changed"
    eprintfn "      --check    exit 1 if <file> would change (no write)"

let private runFormatCommand (rest: string list) : int =
    let flags = rest |> List.filter (fun (a: string) -> a.StartsWith "--") |> Set.ofList
    let files = rest |> List.filter (fun (a: string) -> not (a.StartsWith "--"))

    match files with
    | [ file ] ->
        try
            let r = Interpreter.run (Logic.computeFormat (IO.Path.GetFullPath file))

            if not r.Found then
                eprintfn "File not found: %s" file
                2
            elif flags.Contains "--check" then
                if r.IsChanged then
                    eprintfn "Would reformat %s (%s)" file (describeScope r)
                    1
                else
                    0
            elif flags.Contains "--write" then
                if r.IsChanged then
                    Interpreter.run (writeFile r.Path r.Formatted)
                    eprintfn "Reformatted %s (%s)" file (describeScope r)
                else
                    eprintfn "No changes: %s" file

                0
            else
                Console.Out.Write r.Formatted
                0
        with ex ->
            eprintfn "Error: %s" ex.Message
            2
    | _ ->
        usage ()
        2

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | []
    | [ "hook" ]
    | [ "hook"; "format" ] ->
        // Format flow (edit/create): never break the agent loop — swallow errors, exit 0.
        try
            Interpreter.run Logic.hookFormat
            0
        with _ ->
            0
    | "format" :: rest -> runFormatCommand rest
    | _ ->
        usage ()
        2
