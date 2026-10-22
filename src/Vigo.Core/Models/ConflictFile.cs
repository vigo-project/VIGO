namespace Vigo.Core.Models;

/// <summary>Side of a conflict: the version checked out (ours, stage 2) or the one brought in (theirs, stage 3).</summary>
public enum ConflictSide
{
    Ours,
    Theirs,
}

/// <summary>Part of a conflicted file: lines both sides agree on, or a conflict between them.</summary>
public abstract record ConflictSegment;

/// <summary>Lines merged without conflict (identical on both sides, or changed on one side only).</summary>
public sealed record CommonLines(IReadOnlyList<string> Lines) : ConflictSegment;

/// <summary>One conflict: the lines of each side and those of the merge base (empty when the file was added on both sides).</summary>
public sealed record ConflictHunk(IReadOnlyList<string> Ours, IReadOnlyList<string> Base, IReadOnlyList<string> Theirs) : ConflictSegment
{
    public IReadOnlyList<string> Side(ConflictSide side) => side == ConflictSide.Ours ? Ours : Theirs;
}

/// <summary>
/// Unmerged file as the conflict editor needs it, read from the index stages (not from the conflict markers of the
/// working tree): text merged by <c>git merge-file</c>, or why it cannot be merged line by line.
/// </summary>
/// <param name="Path">Repository-relative path, '/' separators.</param>
/// <param name="Kind">Kind reported by git status.</param>
/// <param name="HasOurs">Stage 2 exists: the file is present on our side.</param>
/// <param name="HasTheirs">Stage 3 exists: the file is present on their side.</param>
/// <param name="IsBinary">git refuses to merge the content (binary file, symbolic link, submodule).</param>
/// <param name="Segments">Merged text, lines without their end-of-line; empty when not mergeable line by line.</param>
/// <param name="LineEnding">End of line to write the result with: the working tree's (CRLF with core.autocrlf).</param>
/// <param name="EndsWithNewline">The merged text ends with an end of line.</param>
public sealed record ConflictFile(
    string Path,
    ConflictKind Kind,
    bool HasOurs,
    bool HasTheirs,
    bool IsBinary,
    IReadOnlyList<ConflictSegment> Segments,
    string LineEnding = "\n",
    bool EndsWithNewline = true)
{
    /// <summary>Both sides have a text that git merged: resolved block by block in the editor.</summary>
    public bool IsText => HasOurs && HasTheirs && !IsBinary;

    public IEnumerable<ConflictHunk> Hunks => Segments.OfType<ConflictHunk>();

    /// <summary>
    /// Text of the result to write: the editor's text (lines separated by '\n') with the file's line ending, and
    /// the final end of line kept as the merged text had it.
    /// </summary>
    public string ToFileText(string editorText)
    {
        var text = editorText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!EndsWithNewline && text.EndsWith('\n')) text = text[..^1];
        return LineEnding == "\n" ? text : text.Replace("\n", LineEnding, StringComparison.Ordinal);
    }
}

/// <summary>How a conflicted file is resolved; the file is staged afterwards.</summary>
public abstract record ConflictResolution
{
    private ConflictResolution()
    {
    }

    /// <summary>The file gets this content (already in its final line endings).</summary>
    public sealed record Content(string Text) : ConflictResolution;

    /// <summary>
    /// The whole version of one side (<c>checkout --ours / --theirs</c>); the file is deleted when that side does not
    /// have it.
    /// </summary>
    public sealed record TakeSide(ConflictSide Side) : ConflictResolution;

    /// <summary>The file is deleted.</summary>
    public sealed record Delete : ConflictResolution;
}
