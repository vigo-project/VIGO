namespace Vigo.Core.Markdown;

/// <summary>Inline content of a Markdown block (J6.9a): text, code, emphasis, links.</summary>
public abstract record MarkdownInline;

/// <summary>Plain text.</summary>
public sealed record MarkdownText(string Text) : MarkdownInline;

/// <summary>Inline code span.</summary>
public sealed record MarkdownCode(string Text) : MarkdownInline;

public enum MarkdownEmphasisKind
{
    Italic,
    Bold,
    Strikethrough,
}

public sealed record MarkdownEmphasis(MarkdownEmphasisKind Kind, IReadOnlyList<MarkdownInline> Content) : MarkdownInline;

/// <summary>Link; <paramref name="Url"/> is as written (the renderer decides which schemes it opens).</summary>
public sealed record MarkdownLink(string Url, IReadOnlyList<MarkdownInline> Content) : MarkdownInline;

/// <summary>Image, shown as a link to its address (nothing is downloaded while reading).</summary>
public sealed record MarkdownImage(string Url, string Alt) : MarkdownInline;

/// <summary>Line break inside a paragraph: hard (two spaces, backslash, &lt;br&gt;) or soft (a space).</summary>
public sealed record MarkdownBreak(bool IsHard) : MarkdownInline;

/// <summary>Check box of a task list item ("- [x] done").</summary>
public sealed record MarkdownTask(bool IsChecked) : MarkdownInline;

/// <summary>Block of a Markdown document.</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Content) : MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Content) : MarkdownBlock;

public sealed record MarkdownCodeBlock(string? Language, string Text) : MarkdownBlock;

public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <param name="Start">Number of the first item of an ordered list.</param>
/// <param name="Items">Blocks of each item.</param>
public sealed record MarkdownList(bool IsOrdered, int Start, IReadOnlyList<IReadOnlyList<MarkdownBlock>> Items) : MarkdownBlock;

public sealed record MarkdownRule : MarkdownBlock;

/// <param name="Header">Cells of the header row; empty when the table has none.</param>
/// <param name="Rows">Cells of the other rows.</param>
public sealed record MarkdownTable(
    IReadOnlyList<IReadOnlyList<MarkdownInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows) : MarkdownBlock;
