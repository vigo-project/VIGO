using Vigo.Core.Models;
using Vigo.Git.Cli;

namespace Vigo.Git.Tests;

public sealed class ProgressParserTests
{
    [Fact]
    public void Receiving_line_with_throughput()
    {
        var p = ProgressParser.Parse("Receiving objects:  42% (123/456), 1.20 MiB | 4.20 MiB/s");

        Assert.NotNull(p);
        Assert.Equal((GitProgressPhase.Receiving, 42, 123L, 456L, "4.20 MiB/s", false), (p.Phase, p.Percent, p.Current, p.Total, p.Throughput, p.IsRemote));
        Assert.Equal(0.8 * 0.42, p.Fraction!.Value, 6);
    }

    [Fact]
    public void Remote_counting_line_has_no_overall_fraction()
    {
        var p = ProgressParser.Parse("remote: Counting objects: 100% (4/4), done.");

        Assert.NotNull(p);
        Assert.Equal((GitProgressPhase.Counting, 100, true), (p.Phase, p.Percent, p.IsRemote));
        Assert.Equal("Counting objects: 100% (4/4), done.", p.Text);
        Assert.Null(p.Fraction);
    }

    [Theory]
    [InlineData("Resolving deltas: 50% (1/2)", GitProgressPhase.Resolving, 0.9)]
    [InlineData("Writing objects: 100% (3/3), 250 bytes | 250.00 KiB/s, done.", GitProgressPhase.Writing, 1.0)]
    [InlineData("Unpacking objects:  25% (1/4)", GitProgressPhase.Unpacking, 0.2)]
    public void Phases_map_to_an_overall_fraction(string line, GitProgressPhase phase, double fraction)
    {
        var p = ProgressParser.Parse(line);

        Assert.NotNull(p);
        Assert.Equal(phase, p.Phase);
        Assert.Equal(fraction, p.Fraction!.Value, 6);
    }

    [Theory]
    [InlineData("Fetching origin")]
    [InlineData("remote: Enumerating objects: 5, done.")]
    [InlineData("To ../remote.git")]
    public void Other_lines_have_no_percentage(string line)
    {
        var p = ProgressParser.Parse(line);

        Assert.NotNull(p);
        Assert.Null(p.Percent);
        Assert.Null(p.Fraction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("remote:")]
    public void Empty_lines_are_ignored(string line) => Assert.Null(ProgressParser.Parse(line));
}
