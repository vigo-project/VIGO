using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Graph;

namespace Vigo.Core.ViewModels;

// "Palette du graphe" of the appearance section: one swatch per lane color, edited in a color picker flyout.
public sealed partial class PreferencesTabViewModel
{
    public ObservableCollection<LaneColorItem> LaneColorItems { get; } = [.. Enumerable.Range(0, LanePalette.Count).Select(i => new LaneColorItem(i))];

    /// <summary>At least one color differs from the design system: "Rétablir la palette" is useful.</summary>
    [ObservableProperty]
    public partial bool HasCustomLaneColors { get; private set; }

    /// <summary>The picker of a swatch chose <paramref name="hex"/> (<c>#RRGGBB</c>).</summary>
    public void SetLaneColor(LaneColorItem item, string hex)
    {
        if (LaneColors.TryParse(hex, out var r, out var g, out var b))
            Update(p => p with { LaneColors = LaneColors.With(p.LaneColors, item.Index, LaneColors.Format(r, g, b)) });
    }

    [RelayCommand]
    private void ResetLaneColor(LaneColorItem item) =>
        Update(p => p with { LaneColors = LaneColors.With(p.LaneColors, item.Index, string.Empty) });

    [RelayCommand]
    private void ResetLaneColors() => Update(p => p with { LaneColors = [] });

    private void SyncLaneColors(UserPreferences current)
    {
        var colors = current.LaneColors;
        foreach (var item in LaneColorItems)
            item.Hex = !colors.IsDefaultOrEmpty && item.Index < colors.Length ? colors[item.Index] : string.Empty;
        HasCustomLaneColors = !colors.IsDefaultOrEmpty;
    }
}

/// <summary>Swatch of the graph palette; <see cref="Hex"/> is empty while the color is the default one.</summary>
public sealed partial class LaneColorItem(int index) : ObservableObject
{
    public int Index { get; } = index;

    public string Label => Loc.Format("LaneColor.Label", Index + 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomized))]
    public partial string Hex { get; set; } = string.Empty;

    public bool IsCustomized => Hex.Length > 0;
}
