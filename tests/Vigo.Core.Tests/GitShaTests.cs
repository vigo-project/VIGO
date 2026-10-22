using System.Text;
using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class GitShaTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Sha1, 20)]
    [InlineData(Sha256, 32)]
    public void Parse_round_trips_to_lowercase_hex(string hex, int byteLength)
    {
        var sha = GitSha.Parse(hex.ToUpperInvariant());

        Assert.Equal(hex, sha.ToString());
        Assert.Equal(byteLength, sha.ByteLength);
        Assert.Equal(hex, $"{sha}");
    }

    [Fact]
    public void Utf8_and_char_parsing_agree()
    {
        Assert.True(GitSha.TryParse(Encoding.ASCII.GetBytes(Sha1), out var fromBytes));

        Assert.Equal(GitSha.Parse(Sha1), fromBytes);
        Assert.Equal(GitSha.Parse(Sha1).GetHashCode(), fromBytes.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456")]
    [InlineData("g123456789abcdef0123456789abcdef01234567")]
    public void TryParse_rejects_invalid_ids(string hex)
    {
        Assert.False(GitSha.TryParse(hex, out var sha));
        Assert.True(sha.IsEmpty);
    }

    [Fact]
    public void Ids_of_different_lengths_are_not_equal()
    {
        // A SHA-256 id starting with the same 20 bytes as a SHA-1 id is still a different object.
        var sha1 = GitSha.Parse(Sha1);
        var sha256 = GitSha.Parse(Sha1 + "000000000000000000000000");

        Assert.NotEqual(sha1, sha256);
        Assert.True(sha1 != sha256);
    }

    [Fact]
    public void ToShortString_abbreviates()
    {
        Assert.Equal("0123456", GitSha.Parse(Sha1).ToShortString());
        Assert.Equal(string.Empty, default(GitSha).ToString());
    }
}
