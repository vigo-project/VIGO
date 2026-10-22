using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class JsonLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-layout-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "layout.json");

    private JsonLayoutStore Store() => new(FilePath, NullLogger<JsonLayoutStore>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_the_defaults()
    {
        var layout = Store().Load();

        Assert.Equal(new LayoutSettings(), layout);
        Assert.Null(layout.Window);
        Assert.Equal(264, layout.LeftPanelWidth);
        Assert.Equal(360, layout.RightPanelWidth);
    }

    [Fact]
    public async Task Saved_layout_is_read_back()
    {
        var saved = new LayoutSettings
        {
            Window = new WindowBounds(-1900, 40, 1600, 1000),
            IsMaximized = true,
            LeftPanelWidth = 300,
            RightPanelWidth = 500,
            GraphColumnWidth = 140,
            AuthorColumnWidth = 200,
            DateColumnWidth = 90,
            ShaColumnWidth = 100,
            CommitMessageHeight = 320,
            FileListAsTree = true,
        };

        await Store().SaveAsync(saved, Ct);

        Assert.Equal(saved, Store().Load());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Corrupted_file_gives_the_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Equal(new LayoutSettings(), Store().Load());
    }

    [Fact]
    public void File_from_a_newer_version_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "version": 99, "layout": { "leftPanelWidth": 300 } }""");

        Assert.Equal(new LayoutSettings(), Store().Load());
    }

    [Fact]
    public void Out_of_range_values_are_brought_back_within_limits()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            { "version": 1, "layout": {
                "window": { "x": 0, "y": 0, "width": 0, "height": 500 },
                "leftPanelWidth": 5000, "rightPanelWidth": -3, "graphColumnWidth": 1,
                "authorColumnWidth": 10 } }
            """);

        var layout = Store().Load();

        Assert.Null(layout.Window);
        Assert.Equal(LayoutLimits.LeftPanel.Maximum, layout.LeftPanelWidth);
        Assert.Equal(LayoutLimits.RightPanel.Minimum, layout.RightPanelWidth);
        Assert.Equal(LayoutLimits.GraphColumn.Minimum, layout.GraphColumnWidth);
        Assert.Equal(LayoutLimits.AuthorColumn.Minimum, layout.AuthorColumnWidth);
        // Missing values keep their defaults.
        Assert.Equal(LayoutLimits.DateColumn.Default, layout.DateColumnWidth);
    }
}

public sealed class LayoutStateTests
{
    private sealed class MemoryStore : ILayoutStore
    {
        public LayoutSettings? Saved { get; private set; }

        public LayoutSettings Load() => Saved ?? new LayoutSettings();

        public Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private static LayoutState State(double left = 264, double right = 360) =>
        new(new MemoryStore(), new LayoutSettings { LeftPanelWidth = left, RightPanelWidth = right });

    [Fact]
    public void Widths_are_kept_within_their_limits()
    {
        var state = State();

        state.LeftPanelWidth = 10;
        state.RightPanelWidth = 10_000;
        state.GraphColumnWidth = 2;
        state.ShaColumnWidth = double.NaN;

        Assert.Equal(LayoutLimits.LeftPanel.Minimum, state.LeftPanelWidth);
        Assert.Equal(LayoutLimits.RightPanel.Maximum, state.RightPanelWidth);
        Assert.Equal(LayoutLimits.GraphColumn.Minimum, state.GraphColumnWidth);
        Assert.Equal(LayoutLimits.ShaColumn.Default, state.ShaColumnWidth);
    }

    [Fact]
    public void Panels_keep_their_width_when_the_window_is_wide_enough()
    {
        Assert.Equal((300d, 500d), State(300, 500).FitPanels(1400));
    }

    [Fact]
    public void Narrow_window_shrinks_the_right_panel_first_to_leave_room_for_the_graph()
    {
        var state = State(300, 500);

        // 300 + 500 + 360 = 1160: 160 too many, taken from the right panel.
        Assert.Equal((300d, 340d), state.FitPanels(1000));
        // Right panel at its minimum (300): the rest from the left panel.
        Assert.Equal((240d, 300d), state.FitPanels(900));
        // Both at their minimum: the graph gets less than its minimum rather than panels below theirs.
        Assert.Equal((180d, 300d), state.FitPanels(600));
        // Saved widths unchanged: they come back when the window grows.
        Assert.Equal(300, state.LeftPanelWidth);
        Assert.Equal(500, state.RightPanelWidth);
    }

    [Fact]
    public void A_panel_cannot_be_dragged_over_the_graph_minimum()
    {
        var state = State(264, 360);

        Assert.Equal(1200 - 360 - 360, state.MaxLeftPanelWidth(1200));
        Assert.Equal(1200 - 360 - 264, state.MaxRightPanelWidth(1200));
    }

    [Fact]
    public async Task Saves_the_current_values()
    {
        var store = new MemoryStore();
        var state = new LayoutState(store, new LayoutSettings())
        {
            WindowBounds = new WindowBounds(10, 20, 1500, 900),
            IsMaximized = true,
            AuthorColumnWidth = 220,
        };

        await state.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LayoutSettings { Window = new WindowBounds(10, 20, 1500, 900), IsMaximized = true, AuthorColumnWidth = 220 }, store.Saved);
    }
}

public sealed class GraphColumnLayoutTests
{
    [Fact]
    public void Message_column_takes_the_remaining_width()
    {
        var layout = GraphColumnLayout.Compute(1000, graph: 64, author: 170, date: 120, sha: 76);

        Assert.Equal(1000 - GraphColumnLayout.RightPadding - 64 - 170 - 120 - 76, layout.Message);
        Assert.Equal(64, layout.MessageX);
        Assert.Equal(1000 - GraphColumnLayout.RightPadding, layout.ShaX + layout.Sha);
    }

    [Fact]
    public void Narrow_view_pushes_the_right_columns_out_instead_of_over_the_graph()
    {
        var layout = GraphColumnLayout.Compute(300, graph: 64, author: 170, date: 120, sha: 76);

        Assert.Equal(GraphColumnLayout.MinMessageWidth, layout.Message);
        Assert.Equal(64 + GraphColumnLayout.MinMessageWidth, layout.AuthorX);
        Assert.True(layout.DateX > 300 - GraphColumnLayout.RightPadding);
    }

    [Fact]
    public void Wide_graph_column_never_overlaps_the_text_columns()
    {
        var layout = GraphColumnLayout.Compute(500, graph: 400, author: 170, date: 120, sha: 76);

        Assert.True(layout.AuthorX >= layout.Graph + GraphColumnLayout.MinMessageWidth);
        Assert.Equal(layout.AuthorX + layout.Author, layout.DateX);
        Assert.Equal(layout.DateX + layout.Date, layout.ShaX);
    }
}
