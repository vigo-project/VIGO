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

    private const string Three = "## 0.3.0 — 2026-10-03\n- C.\n\n## 0.2.0 — 2026-10-02\n- B.\n\n## 0.1.0 — 2026-10-01\n- A.\n";

    [Fact]
    public void Notes_since_the_last_seen_version_gather_every_missed_section()
    {
        Assert.Equal("## 0.3.0 — 2026-10-03\n- C.\n\n## 0.2.0 — 2026-10-02\n- B.", Changelog.NotesSince(Three, "0.3.0", "0.1.0"));
        Assert.Equal("- C.", Changelog.NotesSince(Three, "0.3.0", "0.2.0"));
        Assert.Equal("- B.", Changelog.NotesSince(Three, "0.2.0", "0.1.0"));
    }

    [Fact]
    public void Notes_since_nothing_seen_are_the_current_section_only()
    {
        Assert.Equal("- C.", Changelog.NotesSince(Three, "0.3.0", null));
        Assert.Null(Changelog.NotesSince(Three, "0.3.0", "0.3.0"));
    }
}
