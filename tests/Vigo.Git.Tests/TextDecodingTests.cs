using System.Text;
using Vigo.Git.Cli;

namespace Vigo.Git.Tests;

public sealed class TextDecodingTests
{
    private const string Chinese = "中文 text，汉字。";

    [Fact]
    public void Utf8_is_decoded_as_is()
    {
        Assert.Equal(Chinese, TextDecoding.Decode(Encoding.UTF8.GetBytes(Chinese)));
    }

    [Fact]
    public void Utf8_byte_order_mark_is_dropped()
    {
        Assert.Equal(Chinese, TextDecoding.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Chinese)]));
    }

    [Fact]
    public void Utf16_with_byte_order_mark_is_decoded()
    {
        Assert.Equal(Chinese, TextDecoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Chinese)]));
    }

    [Fact]
    public void Content_that_is_not_utf8_is_not_replaced_by_replacement_characters()
    {
        // 0xD6 0xD0 is "中" in GBK; 0xE9 followed by an ASCII letter is "é" in Windows-1252: invalid UTF-8 either way.
        var text = TextDecoding.Decode([0xD6, 0xD0, (byte)'a', 0xE9, (byte)'b']);

        Assert.DoesNotContain('�', text);
        Assert.EndsWith("b", text, StringComparison.Ordinal);
    }
}
