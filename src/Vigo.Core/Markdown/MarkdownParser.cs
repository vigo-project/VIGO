using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Vigo.Core.Markdown;

/// <summary>
/// Markdown (GitHub flavor, as far as pull requests and issues need it) read into a small model the app draws in XAML
/// (J6.9a). Raw HTML is dropped except <c>&lt;br&gt;</c> and <c>&lt;img&gt;</c> (GitHub uploads pictures as HTML); nothing is
/// fetched while parsing.
/// </summary>
public static partial class MarkdownParser
{
    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\b(?<name>src|alt)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    // The picture of an <img> tag; null without a src.
    private static MarkdownImage? ImageOf(string tag)
    {
        string? src = null, alt = null;
        foreach (Match m in Attribute().Matches(tag))
        {
            var value = System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value);
            if (m.Groups["name"].Value.Equals("src", StringComparison.OrdinalIgnoreCase)) src = value;
            else alt = value;
        }
        return string.IsNullOrWhiteSpace(src) ? null : new MarkdownImage(src, alt ?? string.Empty);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var document = Markdig.Markdown.Parse(markdown.Replace("\r\n", "\n"), Pipeline);
        return Blocks(document);
    }

    private static List<MarkdownBlock> Blocks(IEnumerable<Block> blocks)
    {
        var result = new List<MarkdownBlock>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    result.Add(new MarkdownHeading(heading.Level, Inlines(heading.Inline)));
                    break;
                case ParagraphBlock paragraph:
                    var content = Inlines(paragraph.Inline);
                    if (content.Count > 0) result.Add(new MarkdownParagraph(content));
                    break;
                case FencedCodeBlock fenced:
                    result.Add(new MarkdownCodeBlock(string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info, CodeText(fenced)));
                    break;
                case CodeBlock code:
                    result.Add(new MarkdownCodeBlock(null, CodeText(code)));
                    break;
                case QuoteBlock quote:
                    result.Add(new MarkdownQuote(Blocks(quote)));
                    break;
                case ListBlock list:
                    var items = new List<IReadOnlyList<MarkdownBlock>>();
                    foreach (var item in list.OfType<ListItemBlock>()) items.Add(Blocks(item));
                    var start = int.TryParse(list.OrderedStart, out var number) ? number : 1;
                    result.Add(new MarkdownList(list.IsOrdered, start, items));
                    break;
                case ThematicBreakBlock:
                    result.Add(new MarkdownRule());
                    break;
                case Table table:
                    result.Add(ToTable(table));
                    break;
                case HtmlBlock html:
                    // Only the pictures of a raw HTML block are kept (one paragraph, the images side by side).
                    var pictures = new List<MarkdownInline>();
                    foreach (Match tag in ImgTag().Matches(html.Lines.ToString()))
                    {
                        if (ImageOf(tag.Value) is { } picture)
                        {
                            if (pictures.Count > 0) pictures.Add(new MarkdownText(" "));
                            pictures.Add(picture);
                        }
                    }
                    if (pictures.Count > 0) result.Add(new MarkdownParagraph(pictures));
                    break;
                case ContainerBlock container:
                    result.AddRange(Blocks(container));
                    break;
            }
        }
        return result;
    }

    private static string CodeText(LeafBlock code) => code.Lines.ToString().TrimEnd('\n', '\r');

    private static MarkdownTable ToTable(Table table)
    {
        List<IReadOnlyList<MarkdownInline>> header = [];
        List<IReadOnlyList<IReadOnlyList<MarkdownInline>>> rows = [];
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = row.OfType<TableCell>()
                .Select(cell => (IReadOnlyList<MarkdownInline>)[.. cell.OfType<ParagraphBlock>().SelectMany(p => Inlines(p.Inline))])
                .ToList();
            if (row.IsHeader) header = cells;
            else rows.Add(cells);
        }
        return new MarkdownTable(header, rows);
    }

    private static List<MarkdownInline> Inlines(ContainerInline? container)
    {
        var result = new List<MarkdownInline>();
        if (container is null) return result;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    result.Add(new MarkdownText(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    result.Add(new MarkdownText(entity.Transcoded.ToString()));
                    break;
                case CodeInline code:
                    result.Add(new MarkdownCode(code.Content));
                    break;
                case LineBreakInline lineBreak:
                    result.Add(new MarkdownBreak(lineBreak.IsHard));
                    break;
                case TaskList task:
                    result.Add(new MarkdownTask(task.Checked));
                    break;
                case AutolinkInline auto:
                    result.Add(new MarkdownLink(auto.Url, [new MarkdownText(auto.Url)]));
                    break;
                case LinkInline { IsImage: true } image:
                    result.Add(new MarkdownImage(image.Url ?? string.Empty, PlainText(image)));
                    break;
                case LinkInline link:
                    var label = Inlines(link);
                    result.Add(new MarkdownLink(link.Url ?? string.Empty, label.Count > 0 ? label : [new MarkdownText(link.Url ?? string.Empty)]));
                    break;
                case EmphasisInline emphasis:
                    var kind = emphasis.DelimiterChar == '~' ? MarkdownEmphasisKind.Strikethrough
                        : emphasis.DelimiterCount >= 2 ? MarkdownEmphasisKind.Bold
                        : MarkdownEmphasisKind.Italic;
                    result.Add(new MarkdownEmphasis(kind, Inlines(emphasis)));
                    break;
                case HtmlInline img when img.Tag.StartsWith("<img", StringComparison.OrdinalIgnoreCase):
                    if (ImageOf(img.Tag) is { } picture) result.Add(picture);
                    break;
                case HtmlInline html when html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase):
                    result.Add(new MarkdownBreak(true));
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested));
                    break;
            }
        }
        return result;
    }

    private static string PlainText(ContainerInline container) =>
        string.Concat(Inlines(container).Select(i => i switch
        {
            MarkdownText t => t.Text,
            MarkdownCode c => c.Text,
            _ => string.Empty,
        }));
}
