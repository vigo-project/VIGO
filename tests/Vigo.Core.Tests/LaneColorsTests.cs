using System.Collections.Immutable;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class LaneColorsTests : IDisposable
{
    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());

    public void Dispose() => _preferences.Dispose();

    [Fact]
    public void Normalize_keeps_valid_colors_and_drops_the_others()
    {
        var result = LaneColors.Normalize(["#3b8eea", "nonsense", "", "12345", "0A0b0C"]);

        Assert.Equal(10, result.Length);
        Assert.Equal("#3B8EEA", result[0]);
        Assert.Equal("#0A0B0C", result[4]);
        foreach (var i in (ReadOnlySpan<int>)[1, 2, 3, 5, 9]) Assert.Equal(string.Empty, result[i]);
    }

    [Fact]
    public void Normalize_is_empty_when_nothing_is_customized()
    {
        Assert.Empty(LaneColors.Normalize([]));
        Assert.Empty(LaneColors.Normalize(["", "bad"]));
        Assert.Empty(new UserPreferences { LaneColors = ["zz"] }.Normalize().LaneColors);
    }

    [Fact]
    public void With_sets_and_clears_one_color()
    {
        var one = LaneColors.With([], 2, "#112233");
        Assert.Equal("#112233", one[2]);
        Assert.Equal(10, one.Length);

        Assert.Empty(LaneColors.With(one, 2, string.Empty));
    }

    [Fact]
    public async Task Choosing_a_color_is_saved_and_shown_by_the_section()
    {
        using var sut = new PreferencesTabViewModel(_preferences);
        Assert.False(sut.HasCustomLaneColors);

        sut.SetLaneColor(sut.LaneColorItems[3], "#ff8800");
        await Task.Yield();

        Assert.Equal("#FF8800", _preferences.Current.LaneColors[3]);
        Assert.Equal("#FF8800", sut.LaneColorItems[3].Hex);
        Assert.True(sut.LaneColorItems[3].IsCustomized);
        Assert.False(sut.LaneColorItems[2].IsCustomized);
        Assert.True(sut.HasCustomLaneColors);
    }

    [Fact]
    public void Resetting_restores_the_default_colors()
    {
        using var sut = new PreferencesTabViewModel(_preferences);
        sut.SetLaneColor(sut.LaneColorItems[0], "#010203");
        sut.SetLaneColor(sut.LaneColorItems[1], "#040506");

        sut.ResetLaneColorCommand.Execute(sut.LaneColorItems[0]);
        Assert.Equal(string.Empty, sut.LaneColorItems[0].Hex);
        Assert.Equal("#040506", sut.LaneColorItems[1].Hex);

        sut.ResetLaneColorsCommand.Execute(null);
        Assert.Equal(ImmutableArray<string>.Empty, _preferences.Current.LaneColors);
        Assert.False(sut.HasCustomLaneColors);
    }

    [Fact]
    public void An_invalid_color_is_ignored()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        sut.SetLaneColor(sut.LaneColorItems[0], "red");

        Assert.Empty(_preferences.Current.LaneColors);
    }
}
