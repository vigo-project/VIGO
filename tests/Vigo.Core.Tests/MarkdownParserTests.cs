using Vigo.Core.Markdown;

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
}
