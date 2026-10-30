using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Vigo.Core.Models;
using Vigo.Core.Services;

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

    /// <param name="references">
    /// When given, <c>#12</c>, <c>!34</c> and <c>@login</c> in the text (not in code nor in a link) link to the service, and an
    /// Azure DevOps mention <c>@&lt;identity id&gt;</c> shows the person's name.
    /// </param>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown, MarkdownReferences? references = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var document = Markdig.Markdown.Parse(markdown.Replace("\r\n", "\n"), Pipeline);
        return Blocks(document, references);
    }

    private static List<MarkdownBlock> Blocks(IEnumerable<Block> blocks, MarkdownReferences? refs)
    {
        var result = new List<MarkdownBlock>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    result.Add(new MarkdownHeading(heading.Level, Inlines(heading.Inline, refs)));
                    break;
                case ParagraphBlock paragraph:
                    var content = Inlines(paragraph.Inline, refs);
                    if (content.Count > 0) result.Add(new MarkdownParagraph(content));
                    break;
                case FencedCodeBlock fenced:
                    result.Add(new MarkdownCodeBlock(string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info, CodeText(fenced)));
                    break;
                case CodeBlock code:
                    result.Add(new MarkdownCodeBlock(null, CodeText(code)));
                    break;
                case QuoteBlock quote:
                    result.Add(new MarkdownQuote(Blocks(quote, refs)));
                    break;
                case ListBlock list:
                    var items = new List<IReadOnlyList<MarkdownBlock>>();
                    foreach (var item in list.OfType<ListItemBlock>()) items.Add(Blocks(item, refs));
                    var start = int.TryParse(list.OrderedStart, out var number) ? number : 1;
                    result.Add(new MarkdownList(list.IsOrdered, start, items));
                    break;
                case ThematicBreakBlock:
                    result.Add(new MarkdownRule());
                    break;
                case Table table:
                    result.Add(ToTable(table, refs));
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
                    result.AddRange(Blocks(container, refs));
                    break;
            }
        }
        return result;
    }

    private static string CodeText(LeafBlock code) => code.Lines.ToString().TrimEnd('\n', '\r');

    private static MarkdownTable ToTable(Table table, MarkdownReferences? refs)
    {
        List<IReadOnlyList<MarkdownInline>> header = [];
        List<IReadOnlyList<IReadOnlyList<MarkdownInline>>> rows = [];
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = row.OfType<TableCell>()
                .Select(cell => (IReadOnlyList<MarkdownInline>)[.. cell.OfType<ParagraphBlock>().SelectMany(p => Inlines(p.Inline, refs))])
                .ToList();
            if (row.IsHeader) header = cells;
            else rows.Add(cells);
        }
        return new MarkdownTable(header, rows);
    }

    // refs is null inside a link: its text is not linked again.
    private static List<MarkdownInline> Inlines(ContainerInline? container, MarkdownReferences? refs)
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
                    var label = Inlines(link, null);
                    result.Add(new MarkdownLink(link.Url ?? string.Empty, label.Count > 0 ? label : [new MarkdownText(link.Url ?? string.Empty)]));
                    break;
                case EmphasisInline emphasis:
                    var kind = emphasis.DelimiterChar == '~' ? MarkdownEmphasisKind.Strikethrough
                        : emphasis.DelimiterCount >= 2 ? MarkdownEmphasisKind.Bold
                        : MarkdownEmphasisKind.Italic;
                    result.Add(new MarkdownEmphasis(kind, Inlines(emphasis, refs)));
                    break;
                // @<identity id>: the id reads as an HTML tag when it starts with a letter.
                case HtmlInline mention when refs is not null && MentionTag().IsMatch(mention.Tag):
                    result.Add(new MarkdownText(mention.Tag));
                    break;
                case HtmlInline img when img.Tag.StartsWith("<img", StringComparison.OrdinalIgnoreCase):
                    if (ImageOf(img.Tag) is { } picture) result.Add(picture);
                    break;
                case HtmlInline html when html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase):
                    result.Add(new MarkdownBreak(true));
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested, refs));
                    break;
            }
        }
        return refs is null ? result : WithReferences(result, refs);
    }

    [GeneratedRegex(@"^<[0-9a-fA-F-]{36}>$")]
    private static partial Regex MentionTag();

    // As the review page did: a reference starts the text or follows a space or "(" ; #n and !n are numbers.
    [GeneratedRegex(@"@<(?<id>[0-9a-fA-F-]{36})>|(?<lead>^|[\s(])(?<sign>[#!@])(?<name>[A-Za-z0-9][A-Za-z0-9_.-]*)")]
    private static partial Regex Reference();

    private static List<MarkdownInline> WithReferences(List<MarkdownInline> inlines, MarkdownReferences refs)
    {
        var result = new List<MarkdownInline>();
        var text = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            // Markdig cuts a text at "<", "!"…: the pieces are joined before looking for references.
            if (inline is MarkdownText t)
            {
                text.Append(t.Text);
                continue;
            }
            AddReferences(result, text.ToString(), refs);
            text.Clear();
            result.Add(inline);
        }
        AddReferences(result, text.ToString(), refs);
        return result;
    }

    private static void AddReferences(List<MarkdownInline> result, string text, MarkdownReferences refs)
    {
        var last = 0;
        foreach (Match m in Reference().Matches(text))
        {
            int start, end;
            MarkdownInline replacement;
            if (m.Groups["id"].Success)
            {
                (start, end) = (m.Index, m.Index + m.Length);
                var name = refs.Names?.GetValueOrDefault(m.Groups["id"].Value) is { Length: > 0 } known ? known : Loc.Get("ReviewEditor.UnknownPerson");
                replacement = new MarkdownEmphasis(MarkdownEmphasisKind.Bold, [new MarkdownText("@" + name)]);
            }
            else
            {
                var sign = m.Groups["sign"].Value[0];
                // "@bob." ends a sentence: the dot is not part of the login.
                var name = m.Groups["name"].Value.TrimEnd('.');
                if (sign != '@' && !name.All(char.IsAsciiDigit)) continue;
                var template = sign switch { '#' => refs.Links.Issue, '!' => refs.Links.Pull, _ => refs.Links.User };
                if (template is null) continue;
                start = m.Groups["sign"].Index;
                end = start + 1 + name.Length;
                replacement = new MarkdownLink(template.Replace("{0}", Uri.EscapeDataString(name), StringComparison.Ordinal), [new MarkdownText(sign + name)]);
            }
            if (start > last) result.Add(new MarkdownText(text[last..start]));
            result.Add(replacement);
            last = end;
        }
        if (last < text.Length) result.Add(new MarkdownText(text[last..]));
    }

    private static string PlainText(ContainerInline container) =>
        string.Concat(Inlines(container, null).Select(i => i switch
        {
            MarkdownText t => t.Text,
            MarkdownCode c => c.Text,
            _ => string.Empty,
        }));
}
