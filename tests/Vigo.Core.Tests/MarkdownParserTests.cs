using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Markdown of pull requests and issues read into the model the app draws (J6.9a).</summary>
public sealed class MarkdownParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Nothing_to_show_gives_no_block(string? markdown) => Assert.Empty(MarkdownParser.Parse(markdown));

    [Fact]
    public void Headings_and_paragraphs_keep_their_text()
    {
        var blocks = MarkdownParser.Parse("# Title\n\nSome *words* and **more**.\n");

        var heading = Assert.IsType<MarkdownHeading>(blocks[0]);
        Assert.Equal(1, heading.Level);
        Assert.Equal("Title", Assert.IsType<MarkdownText>(Assert.Single(heading.Content)).Text);

        var paragraph = Assert.IsType<MarkdownParagraph>(blocks[1]);
        Assert.Collection(paragraph.Content,
            i => Assert.Equal("Some ", Assert.IsType<MarkdownText>(i).Text),
            i => Assert.Equal(MarkdownEmphasisKind.Italic, Assert.IsType<MarkdownEmphasis>(i).Kind),
            i => Assert.Equal(" and ", Assert.IsType<MarkdownText>(i).Text),
            i => Assert.Equal(MarkdownEmphasisKind.Bold, Assert.IsType<MarkdownEmphasis>(i).Kind),
            i => Assert.Equal(".", Assert.IsType<MarkdownText>(i).Text));
    }

    [Fact]
    public void A_fenced_block_keeps_its_language_and_its_lines_as_they_are()
    {
        var blocks = MarkdownParser.Parse("```csharp\nvar x = 1;\n  return x;\n```\n");

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(blocks));
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\n  return x;", code.Text);
    }

    [Fact]
    public void Lists_know_their_kind_start_and_task_boxes()
    {
        var blocks = MarkdownParser.Parse("3. three\n4. four\n\n- [x] done\n- [ ] todo\n");

        var ordered = Assert.IsType<MarkdownList>(blocks[0]);
        Assert.True(ordered.IsOrdered);
        Assert.Equal(3, ordered.Start);
        Assert.Equal(2, ordered.Items.Count);

        var tasks = Assert.IsType<MarkdownList>(blocks[1]);
        Assert.False(tasks.IsOrdered);
        var boxes = tasks.Items.Select(item => Assert.IsType<MarkdownParagraph>(item[0]).Content.OfType<MarkdownTask>().Single().IsChecked).ToList();
        Assert.Equal([true, false], boxes);
    }

    [Fact]
    public void Links_images_and_bare_addresses_become_links()
    {
        var blocks = MarkdownParser.Parse("See [docs](https://example.com/a) and https://example.com/b ![logo](https://example.com/l.png)");

        var content = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks)).Content;
        Assert.Equal(["https://example.com/a", "https://example.com/b"], content.OfType<MarkdownLink>().Select(l => l.Url));
        var image = Assert.Single(content.OfType<MarkdownImage>());
        Assert.Equal("https://example.com/l.png", image.Url);
        Assert.Equal("logo", image.Alt);
    }

    [Fact]
    public void Html_images_are_kept_inline_and_in_html_blocks()
    {
        var inline = MarkdownParser.Parse("Look <img width=\"200\" alt=\"a &amp; b\" src=\"https://example.com/i.png\"> here");
        var picture = Assert.Single(Assert.IsType<MarkdownParagraph>(Assert.Single(inline)).Content.OfType<MarkdownImage>());
        Assert.Equal(("https://example.com/i.png", "a & b"), (picture.Url, picture.Alt));

        var block = MarkdownParser.Parse("<img src='https://example.com/1.png'>\n<img alt=\"two\" src=\"https://example.com/2.png\">\n\n<img alt=\"no source\">");
        var images = Assert.IsType<MarkdownParagraph>(Assert.Single(block)).Content.OfType<MarkdownImage>().ToList();
        Assert.Equal(["https://example.com/1.png", "https://example.com/2.png"], images.Select(i => i.Url));
    }

    [Fact]
    public void Quotes_rules_and_tables_are_read()
    {
        var blocks = MarkdownParser.Parse("> quoted\n\n---\n\n| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n");

        Assert.IsType<MarkdownQuote>(blocks[0]);
        Assert.IsType<MarkdownRule>(blocks[1]);
        var table = Assert.IsType<MarkdownTable>(blocks[2]);
        Assert.Equal(2, table.Header.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("4", Assert.IsType<MarkdownText>(Assert.Single(table.Rows[1][1])).Text);
    }

    [Fact]
    public void Raw_html_is_dropped_except_line_breaks()
    {
        var blocks = MarkdownParser.Parse("<!-- hidden -->\n\nline<br>next <script>alert(1)</script>\n\n<details><summary>x</summary>y</details>\n");

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks));
        Assert.Contains(paragraph.Content, i => i is MarkdownBreak { IsHard: true });
        Assert.DoesNotContain(paragraph.Content.OfType<MarkdownText>(), t => t.Text.Contains("<script", StringComparison.Ordinal));
    }

    [Fact]
    public void Strikethrough_is_read()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse("~~old~~")));

        Assert.Equal(MarkdownEmphasisKind.Strikethrough, Assert.IsType<MarkdownEmphasis>(Assert.Single(paragraph.Content)).Kind);
    }
    private const string Ada = "9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";
    private const string Bob = "ad6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";

    private static readonly MarkdownReferences References = new(
        new ReferenceLinks("https://h/o/r/issues/{0}", "https://h/o/r/pull/{0}", "https://h/{0}"),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Ada] = "Ada Lovelace", [Bob] = "" });

    private static IReadOnlyList<MarkdownInline> Inlines(string markdown, MarkdownReferences? references = null) =>
        Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse(markdown, references ?? References))).Content;

    [Theory]
    [InlineData("See #12.", "https://h/o/r/issues/12", "#12")]
    [InlineData("See !34.", "https://h/o/r/pull/34", "!34")]
    [InlineData("See @octo-cat.", "https://h/octo-cat", "@octo-cat")]
    public void A_reference_links_to_the_service(string markdown, string url, string label) =>
        Assert.Collection(Inlines(markdown),
            i => Assert.Equal("See ", Assert.IsType<MarkdownText>(i).Text),
            i =>
            {
                var link = Assert.IsType<MarkdownLink>(i);
                Assert.Equal(url, link.Url);
                Assert.Equal(label, Assert.IsType<MarkdownText>(Assert.Single(link.Content)).Text);
            },
            i => Assert.Equal(".", Assert.IsType<MarkdownText>(i).Text));

    [Fact]
    public void A_person_mentioned_by_identifier_shows_the_name_or_a_placeholder()
    {
        // Bob's id starts with a letter (read as an HTML tag by Markdig); Ada's with a digit.
        var mentions = Inlines($"@<{Ada}> and @<{Bob}>").OfType<MarkdownEmphasis>()
            .Select(e => Assert.IsType<MarkdownText>(Assert.Single(e.Content)).Text);

        Assert.Equal(["@Ada Lovelace", "@" + Loc.Get("ReviewEditor.UnknownPerson")], mentions);
    }

    [Fact]
    public void Code_links_and_words_keep_their_references_as_text()
    {
        Assert.Equal("#12", Assert.IsType<MarkdownCode>(Assert.Single(Inlines("`#12`"))).Text);
        Assert.Equal("https://x/", Assert.IsType<MarkdownLink>(Assert.Single(Inlines("[#12](https://x/)"))).Url);
        Assert.Equal("a#12 #abc", Assert.IsType<MarkdownText>(Assert.Single(Inlines("a#12 #abc"))).Text);
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse("```\n#12 @<" + Ada + ">\n```", References)));
        Assert.Equal("#12 @<" + Ada + ">", code.Text);
    }

    [Fact]
    public void Without_addresses_a_reference_stays_text() =>
        Assert.Equal("See #12 and @bob", string.Concat(
            Inlines("See #12 and @bob", new MarkdownReferences(new ReferenceLinks(null, null, null))).Select(i => Assert.IsType<MarkdownText>(i).Text)));
}
