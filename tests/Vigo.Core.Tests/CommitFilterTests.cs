using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class CommitFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("author:")]
    [InlineData("file: sha:")]
    public void Nothing_to_search_gives_no_filter(string? text) => Assert.Null(CommitFilter.Parse(text));

    [Fact]
    public void Plain_words_are_message_terms()
    {
        var filter = CommitFilter.Parse("fix  crash")!;

        Assert.Equal(["fix", "crash"], filter.Terms);
        Assert.Empty(filter.Authors);
        Assert.Empty(filter.Files);
        Assert.Null(filter.Sha);
    }

    [Fact]
    public void Operators_fill_their_own_list_whatever_their_case()
    {
        var filter = CommitFilter.Parse("Author:alice FILE:README.md sha:1A2B3C4 crash")!;

        Assert.Equal(["alice"], filter.Authors);
        Assert.Equal(["README.md"], filter.Files);
        Assert.Equal("1a2b3c4", filter.Sha);
        Assert.Equal(["crash"], filter.Terms);
    }

    [Fact]
    public void Quotes_keep_spaces_in_a_value_or_a_phrase()
    {
        var filter = CommitFilter.Parse("author:\"Jean Martin\" \"fix the crash\"")!;

        Assert.Equal(["Jean Martin"], filter.Authors);
        Assert.Equal(["fix the crash"], filter.Terms);
    }

    [Theory]
    [InlineData("sha:xyz123")]
    [InlineData("sha:abc")]
    public void A_sha_that_is_no_id_is_searched_as_a_word(string text)
    {
        var filter = CommitFilter.Parse(text)!;

        Assert.Null(filter.Sha);
        Assert.Equal([text[4..]], filter.Terms);
    }

    [Fact]
    public void An_unknown_operator_is_a_word_as_typed() =>
        Assert.Equal(["foo:bar"], CommitFilter.Parse("foo:bar")!.Terms);

    [Fact]
    public void French_operator_names_are_accepted()
    {
        var filter = CommitFilter.Parse("auteur:alice fichier:a.txt")!;

        Assert.Equal(["alice"], filter.Authors);
        Assert.Equal(["a.txt"], filter.Files);
    }
}
