using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class CommitMessageTests
{
    [Theory]
    [InlineData("Summary", "", "Summary\n")]
    [InlineData("  Summary  ", "  ", "Summary\n")]
    [InlineData("Two\r\nlines", null, "Two lines\n")]
    [InlineData("Summary", "Body\r\n\r\nMore  \n\n", "Summary\n\nBody\n\nMore\n")]
    [InlineData("Summary", "\n\nBody", "Summary\n\nBody\n")]
    public void Compose_joins_summary_and_description(string summary, string? description, string expected) =>
        Assert.Equal(expected, CommitMessage.Compose(summary, description));

    [Theory]
    [InlineData("Summary\n", "Summary", "")]
    [InlineData("Summary\n\nBody\n\nMore\n", "Summary", "Body\n\nMore")]
    [InlineData("Summary\r\nBody directly\r\n", "Summary", "Body directly")]
    [InlineData("\n\nSummary  \n", "Summary", "")]
    public void Split_separates_the_first_line(string message, string summary, string description) =>
        Assert.Equal((summary, description), CommitMessage.Split(message));
}
