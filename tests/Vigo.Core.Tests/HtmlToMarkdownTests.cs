using Vigo.Core.Markdown;

namespace Vigo.Core.Tests;

/// <summary>HTML of Azure DevOps rich text fields turned into Markdown (J6.14).</summary>
public sealed class HtmlToMarkdownTests
{
    [Theory]
    [InlineData("<div>One</div><div>Two</div>", "One\n\nTwo")]
    [InlineData("<p>One</p><p>Two</p>", "One\n\nTwo")]
    [InlineData("One<br>Two", "One  \nTwo")]
    [InlineData("<h2>Title</h2><p>Body</p>", "## Title\n\nBody")]
    [InlineData("a <b>bold</b>, <i>italic</i> and <code>code</code>", "a **bold**, *italic* and `code`")]
    [InlineData("1 &lt; 2 &amp;&amp; 3&nbsp;&gt; 2", "1 \\< 2 && 3 > 2")]
    [InlineData("<span>  spaced\n   out  </span>", "spaced out")]
    public void Converts_text_blocks_and_inline_emphasis(string html, string expected)
    {
        Assert.Equal(expected, HtmlToMarkdown.Convert(html));
    }

    [Fact]
    public void Converts_lists_with_nesting_and_numbering()
    {
        var markdown = HtmlToMarkdown.Convert("<ul><li>One</li><li>Two<ul><li>Inner</li></ul></li></ul><ol><li>First</li><li>Second</li></ol>");

        Assert.Equal("- One\n- Two\n    - Inner\n\n1. First\n2. Second", markdown);
    }

    [Fact]
    public void Converts_links_and_pictures_and_drops_useless_anchors()
    {
        var markdown = HtmlToMarkdown.Convert(
            "<a href=\"https://example.com/a b\">site</a> <a href=\"#\">top</a> <img src=\"https://dev.azure.com/x/_apis/wit/attachments/1?fileName=a.png\" alt=\"shot\">");

        Assert.Equal("[site](https://example.com/a%20b) top ![shot](https://dev.azure.com/x/_apis/wit/attachments/1?fileName=a.png)", markdown);
    }

    [Fact]
    public void Keeps_preformatted_text_verbatim()
    {
        var markdown = HtmlToMarkdown.Convert("<pre>line 1\n  line *2*</pre>after");

        Assert.Equal("```\nline 1\n  line *2*\n```\n\nafter", markdown);
    }

    [Fact]
    public void Prefixes_quoted_lines()
    {
        Assert.Equal("> quoted\n\nnext", HtmlToMarkdown.Convert("<blockquote>quoted</blockquote><p>next</p>"));
    }

    [Fact]
    public void Converts_tables_to_pipe_tables()
    {
        var markdown = HtmlToMarkdown.Convert("<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>x|y</td></tr></table>");

        Assert.Equal("| A | B |\n| --- | --- |\n| 1 | x\\|y |", markdown);
    }

    [Fact]
    public void Skips_scripts_styles_and_comments()
    {
        Assert.Equal("visible", HtmlToMarkdown.Convert("<style>p { color: red }</style><!-- note --><script>alert(1)</script>visible"));
    }

    [Theory]
    [InlineData("plain text", false)]
    [InlineData("a < b and c > d", false)]
    [InlineData("1 <3 you", false)]
    [InlineData("<div>x</div>", true)]
    [InlineData("text<br>more", true)]
    public void Tells_html_from_markdown(string text, bool expected)
    {
        Assert.Equal(expected, HtmlToMarkdown.LooksLikeHtml(text));
    }

    [Fact]
    public void Markdown_text_is_left_alone()
    {
        Assert.Equal("Some **bold** text", HtmlToMarkdown.ConvertIfHtml("  Some **bold** text\n"));
    }
}
