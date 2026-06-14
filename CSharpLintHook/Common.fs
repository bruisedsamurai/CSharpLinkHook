module CSharpLintHook.Common

/// A contiguous range of changed/added lines in the new file (0-based, inclusive).
type LineRange =
    { StartLine: int
      EndLineInclusive: int }

/// Result of inspecting a file against its git base.
type DiffResult =
    /// No repo, untracked file, or no base commit: format the whole file.
    | FormatWhole
    /// File is tracked: these are the changed line ranges in the working copy.
    | Changed of LineRange list

/// Outcome of computing a formatted version of a file.
type FormatResult =
    { Path: string
      Found: bool
      Original: string
      Formatted: string
      Regions: int
      WholeFile: bool }

    /// True when formatting actually changed the file content.
    member this.IsChanged = this.Found && this.Formatted <> this.Original
