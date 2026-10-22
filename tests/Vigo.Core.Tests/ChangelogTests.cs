using Vigo.Core.Services;

namespace Vigo.Core.Tests;

public sealed class ChangelogTests
{
    private const string Notes = "## 0.10.0 — 2026-10-13\n- Ten.\n\n## 0.1.0 — 2026-10-09\n- One.\n- Two.\n";

    [Fact]
    public void Section_of_a_version_stops_at_the_next_heading()
    {
        Assert.Equal("- Ten.", Changelog.SectionOf(Notes, "0.10.0"));
        Assert.Equal("- One.\n- Two.", Changelog.SectionOf(Notes, "0.1.0"));
    }

    [Fact]
    public void A_version_prefix_is_not_a_match()
    {
        Assert.Null(Changelog.SectionOf(Notes, "0.1"));
        Assert.Null(Changelog.SectionOf(Notes, "9.9.9"));
    }
}
