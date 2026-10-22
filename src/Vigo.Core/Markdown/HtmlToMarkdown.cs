using System.Net;
using System.Text;

namespace Vigo.Core.Markdown;

/// <summary>
/// Turns the HTML an Azure DevOps rich text field stores (work item description, acceptance criteria, comments…) into Markdown,
/// which <see cref="MarkdownParser"/> then draws. Covers what the service's editor produces: paragraphs, line breaks, headings,
/// lists, emphasis, code, links, pictures, quotes and tables; any other tag keeps its text.
/// </summary>
public static class HtmlToMarkdown
{
    /// <summary>The text contains an HTML tag (a field written in Markdown does not).</summary>
    public static bool LooksLikeHtml(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '<' && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!')) return true;
        }
        return false;
    }

    /// <summary>The Markdown of <paramref name="html"/>; the text itself when it holds no tag.</summary>
    public static string Convert(string html) => string.IsNullOrWhiteSpace(html) ? string.Empty : new Converter(html).Run();

    /// <summary>Same, for a field that may already be Markdown: only text with tags is converted.</summary>
    public static string ConvertIfHtml(string text) => LooksLikeHtml(text) ? Convert(text) : text.Trim();

    private sealed class ListState(bool ordered)
    {
        public bool Ordered { get; } = ordered;

        public int Count { get; set; }
    }

    private sealed class Converter(string html)
    {
        private readonly StringBuilder _root = new();
        private readonly Stack<ListState> _lists = new();
        private readonly Stack<int> _quotes = new();
        private readonly Stack<string?> _links = new();
        private readonly Stack<StringBuilder> _cellParents = new();
        private readonly List<string> _row = [];
        private StringBuilder _sb = null!;
        private int _pre;
        private int _code;
        private int _skip;
        private int _tableRows;

        public string Run()
        {
            _sb = _root;
            var i = 0;
            while (i < html.Length)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0)
                {
                    Text(html[i..]);
                    break;
                }
                if (lt > i) Text(html[i..lt]);

                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }
                if (lt + 1 >= html.Length || !(char.IsAsciiLetter(html[lt + 1]) || html[lt + 1] is '/' or '!'))
                {
                    Text("<");
                    i = lt + 1;
                    continue;
                }

                var gt = html.IndexOf('>', lt + 1);
                if (gt < 0)
                {
                    Text(html[lt..]);
                    break;
                }
                Tag(html.Substring(lt + 1, gt - lt - 1));
                i = gt + 1;
            }
            return Collapse(_root.ToString().Trim());
        }

        // Three or more line breaks in a row never carry meaning in Markdown.
        private static string Collapse(string text)
        {
            var builder = new StringBuilder(text.Length);
            var newlines = 0;
            foreach (var c in text)
            {
                newlines = c == '\n' ? newlines + 1 : 0;
                if (newlines <= 2) builder.Append(c);
            }
            return builder.ToString();
        }

        private void Text(string raw)
        {
            if (_skip > 0 || raw.Length == 0) return;
            var text = WebUtility.HtmlDecode(raw);
            if (_pre > 0)
            {
                _sb.Append(text);
                return;
            }
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (_sb.Length > 0 && _sb[^1] is not (' ' or '\n')) _sb.Append(' ');
                    continue;
                }
                if (_code == 0 && c is '\\' or '*' or '`' or '[' or ']' or '<') _sb.Append('\\');
                _sb.Append(c);
            }
        }

        private void TrimSpaces()
        {
            while (_sb.Length > 0 && _sb[^1] == ' ') _sb.Length--;
        }

        private void LineBreak()
        {
            TrimSpaces();
            if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append('\n');
        }

        private void BlankLine()
        {
            TrimSpaces();
            if (_sb.Length == 0) return;
            var newlines = 0;
            for (var k = _sb.Length - 1; k >= 0 && _sb[k] == '\n'; k--) newlines++;
            _sb.Append('\n', Math.Max(0, 2 - newlines));
        }

        // Inside a list item a block only breaks the line: a blank line would end the item.
        private void Block()
        {
            if (_lists.Count > 0) LineBreak();
            else BlankLine();
        }

        private void Tag(string inner)
        {
            var closing = inner.StartsWith('/');
            var start = closing ? 1 : 0;
            var end = start;
            while (end < inner.Length && char.IsAsciiLetterOrDigit(inner[end])) end++;
            var name = inner[start..end].ToLowerInvariant();
            if (name.Length == 0) return;

            if (name is "script" or "style")
            {
                if (closing) _skip = Math.Max(0, _skip - 1);
                else if (!inner.EndsWith('/')) _skip++;
                return;
            }
            if (_skip > 0) return;

            switch (name)
            {
                case "br":
                    _sb.Append(_pre > 0 ? "\n" : "  \n");
                    break;
                case "p" or "div" or "section" or "article" or "header" or "footer" or "figure" or "center" or "address":
                    Block();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    BlankLine();
                    if (!closing) _sb.Append('#', name[1] - '0').Append(' ');
                    break;
                case "ul" or "ol":
                    OnList(closing, name == "ol");
                    break;
                case "li":
                    OnListItem(closing);
                    break;
                case "b" or "strong":
                    _sb.Append("**");
                    break;
                case "i" or "em":
                    _sb.Append('*');
                    break;
                case "s" or "strike" or "del":
                    _sb.Append("~~");
                    break;
                case "code":
                    if (_pre == 0)
                    {
                        _sb.Append('`');
                        _code += closing ? -1 : 1;
                        _code = Math.Max(0, _code);
                    }
                    break;
                case "pre":
                    OnPre(closing);
                    break;
                case "blockquote":
                    OnQuote(closing);
                    break;
                case "a":
                    OnLink(closing, inner);
                    break;
                case "img":
                    OnImage(inner);
                    break;
                case "hr":
                    BlankLine();
                    _sb.Append("---");
                    BlankLine();
                    break;
                case "table":
                    BlankLine();
                    _tableRows = 0;
                    break;
                case "tr":
                    OnRow(closing);
                    break;
                case "td" or "th":
                    OnCell(closing);
                    break;
            }
        }

        private void OnList(bool closing, bool ordered)
        {
            if (!closing)
            {
                if (_lists.Count == 0) BlankLine();
                else LineBreak();
                _lists.Push(new ListState(ordered));
                return;
            }
            if (_lists.Count > 0) _lists.Pop();
            if (_lists.Count == 0) BlankLine();
            else LineBreak();
        }

        private void OnListItem(bool closing)
        {
            LineBreak();
            if (closing || _lists.Count == 0) return;
            var list = _lists.Peek();
            _sb.Append(' ', 4 * (_lists.Count - 1));
            _sb.Append(list.Ordered ? $"{++list.Count}. " : "- ");
        }

        private void OnPre(bool closing)
        {
            if (!closing)
            {
                BlankLine();
                _sb.Append("```\n");
                _pre++;
                return;
            }
            if (_pre == 0) return;
            _pre--;
            LineBreak();
            _sb.Append("```");
            BlankLine();
        }

        private void OnQuote(bool closing)
        {
            if (!closing)
            {
                BlankLine();
                _quotes.Push(_sb.Length);
                return;
            }
            if (_quotes.Count == 0) return;
            var from = _quotes.Pop();
            var quoted = _sb.ToString(from, _sb.Length - from).Trim();
            _sb.Length = from;
            foreach (var line in quoted.Split('\n')) _sb.Append("> ").Append(line.TrimEnd()).Append('\n');
            BlankLine();
        }

        private void OnLink(bool closing, string inner)
        {
            if (!closing)
            {
                var href = Attribute(inner, "href");
                var usable = !string.IsNullOrWhiteSpace(href) && href != "#" && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
                _links.Push(usable ? Address(WebUtility.HtmlDecode(href!)) : null);
                if (usable) _sb.Append('[');
                return;
            }
            if (_links.Count == 0) return;
            if (_links.Pop() is { } address) _sb.Append("](").Append(address).Append(')');
        }

        private void OnImage(string inner)
        {
            var source = Attribute(inner, "src");
            if (string.IsNullOrWhiteSpace(source)) return;
            var alt = WebUtility.HtmlDecode(Attribute(inner, "alt") ?? string.Empty).Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
            _sb.Append("![").Append(alt).Append("](").Append(Address(WebUtility.HtmlDecode(source))).Append(')');
        }

        private static string Address(string url) => url.Trim().Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);

        private void OnRow(bool closing)
        {
            if (!closing)
            {
                _row.Clear();
                return;
            }
            if (_row.Count == 0) return;
            LineBreak();
            _sb.Append("| ").Append(string.Join(" | ", _row)).Append(" |\n");
            if (_tableRows++ == 0) _sb.Append("| ").Append(string.Join(" | ", _row.Select(_ => "---"))).Append(" |\n");
            _row.Clear();
        }

        private void OnCell(bool closing)
        {
            if (!closing)
            {
                _cellParents.Push(_sb);
                _sb = new StringBuilder();
                return;
            }
            if (_cellParents.Count == 0) return;
            var cell = _sb.ToString().Trim().Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
            _sb = _cellParents.Pop();
            _row.Add(cell);
        }

        // Reads attribute <paramref name="name"/> of the tag text; null when it has none.
        private static string? Attribute(string tag, string name)
        {
            var from = 0;
            while (from < tag.Length)
            {
                var at = tag.IndexOf(name, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                from = at + name.Length;
                if (at == 0 || !char.IsWhiteSpace(tag[at - 1])) continue;

                var k = from;
                while (k < tag.Length && char.IsWhiteSpace(tag[k])) k++;
                if (k >= tag.Length || tag[k] != '=') continue;
                k++;
                while (k < tag.Length && char.IsWhiteSpace(tag[k])) k++;
                if (k >= tag.Length) return string.Empty;

                if (tag[k] is '"' or '\'')
                {
                    var close = tag.IndexOf(tag[k], k + 1);
                    return close < 0 ? tag[(k + 1)..] : tag[(k + 1)..close];
                }
                var stop = k;
                while (stop < tag.Length && !char.IsWhiteSpace(tag[stop]) && tag[stop] != '>') stop++;
                return tag[k..stop];
            }
            return null;
        }
    }
}
