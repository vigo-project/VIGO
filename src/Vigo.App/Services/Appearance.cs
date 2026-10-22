using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Windows.UI;

namespace Vigo.App.Services;

/// <summary>
/// Theme and density currently applied (preferences, J4.6), changed live. A single static instance so that XAML can
/// bind to it from any template (<c>{x:Bind services:Appearance.Current.RowHeight, Mode=OneWay}</c>: a StaticResource
/// is read only once); the Win2D controls listen to <see cref="ObservableObject.PropertyChanged"/>. UI thread only.
/// </summary>
public sealed partial class Appearance : ObservableObject
{
    public static Appearance Current { get; } = new();

    private Appearance()
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight), nameof(ToolbarButtonWidth), nameof(ToolbarButtonHeight), nameof(ToolbarLabelVisibility), nameof(IsCompact))]
    public partial DensityMetrics Metrics { get; private set; } = DensityMetrics.Normal;

    /// <summary>Theme requested for the window content and the dialogs (Default: follows Windows).</summary>
    [ObservableProperty]
    public partial ElementTheme Theme { get; private set; } = ElementTheme.Default;

    /// <summary>Editor and terminal preferences in use (the pages receive them with the theme, changed live).</summary>
    [ObservableProperty]
    public partial EditorSettings Editor { get; private set; } = EditorSettings.Default;

    [ObservableProperty]
    public partial TerminalSettings Terminal { get; private set; } = TerminalSettings.Default;

    /// <summary>Graph palette in use: the design system colors, or the ones chosen in the preferences.</summary>
    public Color[] Lanes { get; private set; } = DesignTokens.Lanes;

    /// <summary>
    /// Brush of a graph palette color, for <c>x:Bind</c> (a new brush: not updated afterwards). <paramref name="hex"/>
    /// is unused: it is bound only so that a <c>OneWay</c> binding evaluates the function again when the color changes.
    /// </summary>
    public static Brush LaneBrush(int index, string hex) => new SolidColorBrush(Current.Lanes[(int)((uint)index % (uint)Current.Lanes.Length)]);

    public bool IsCompact => Metrics == DensityMetrics.Compact;

    /// <summary>Row height of the lists: the density's, grown with the text size.</summary>
    public double RowHeight => Math.Round(Metrics.RowHeight * TextScale);

    /// <summary>
    /// Text size factor and interface font chosen in the preferences, as the application started with them (they apply at
    /// the next launch: the XAML reads them once, see <see cref="UiText"/>); Win2D and code-built text use them too.
    /// </summary>
    public double TextScale { get; private set; } = 1;

    public string UiFontFamily { get; private set; } = "Segoe UI Variable Text";

    /// <summary>A size of the design, grown with the text size (code-built text; the XAML uses the VigoFontSize keys).</summary>
    public static float Scaled(double size) => (float)(size * Current.TextScale);

    /// <summary>Called once at launch, before any view exists.</summary>
    public void InitializeText(UserPreferences preferences)
    {
        TextScale = preferences.UiTextScale;
        if (preferences.UiFontFamily is { } family) UiFontFamily = family;
    }

    public double ToolbarButtonWidth => Metrics.ToolbarButtonWidth;

    public double ToolbarButtonHeight => Metrics.ToolbarButtonHeight;

    public Visibility ToolbarLabelVisibility => Metrics.ShowToolbarLabels ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(UserPreferences preferences)
    {
        Metrics = DensityMetrics.For(preferences.Density);
        Theme = preferences.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyLanes(preferences.LaneColors);
        Editor = preferences.Editor;
        Terminal = preferences.Terminal;
    }

    private void ApplyLanes(System.Collections.Immutable.ImmutableArray<string> custom)
    {
        var lanes = new Color[DesignTokens.Lanes.Length];
        for (var i = 0; i < lanes.Length; i++)
        {
            lanes[i] = !custom.IsDefaultOrEmpty && i < custom.Length && LaneColors.TryParse(custom[i], out var r, out var g, out var b)
                ? Color.FromArgb(0xFF, r, g, b)
                : DesignTokens.Lanes[i];
        }
        if (lanes.AsSpan().SequenceEqual(Lanes)) return;
        Lanes = lanes;
        OnPropertyChanged(nameof(Lanes));
    }
}
