using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Services;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>
/// Brushes of the graph lane palette, shared by badges and avatars (UI thread only). The instances are kept when the
/// palette changes in the preferences: their color is updated in place, so every control that holds one follows.
/// </summary>
public static class LaneBrushes
{
    private static readonly SolidColorBrush[] Brushes = [.. Appearance.Current.Lanes.Select(c => new SolidColorBrush(c))];

    private static readonly SolidColorBrush[] SoftBrushes =
        [.. Appearance.Current.Lanes.Select(c => new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)))];

    static LaneBrushes() => Appearance.Current.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName != nameof(Appearance.Lanes)) return;
        var lanes = Appearance.Current.Lanes;
        for (var i = 0; i < Brushes.Length; i++)
        {
            Brushes[i].Color = lanes[i];
            SoftBrushes[i].Color = Color.FromArgb(0x30, lanes[i].R, lanes[i].G, lanes[i].B);
        }
    };

    public static SolidColorBrush For(int colorIndex) => Brushes[Index(colorIndex)];

    /// <summary>Translucent tint of a lane color, for a row background.</summary>
    public static SolidColorBrush SoftFor(int colorIndex) => SoftBrushes[Index(colorIndex)];

    private static int Index(int colorIndex) => (int)((uint)colorIndex % Brushes.Length);
}

/// <summary>
/// Kind of change of a file (A, M, D, R…) as a colored icon: pen for a modification, + for an addition, − for a
/// deletion, arrow for a rename… Colors come from <see cref="DesignTokens"/> for the current theme (resources are
/// never cast in C#, see CLAUDE.md).
/// </summary>
public sealed partial class StatusBadge : UserControl
{
    private const string IconFont = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    private readonly FontIcon _icon = new()
    {
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string _letter = string.Empty;

    public StatusBadge()
    {
        _icon.FontFamily = new FontFamily(IconFont);
        Width = 16;
        Height = 16;
        Content = _icon;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Update();
        // Before it is in the tree, ActualTheme is the system theme, not the one forced on the window root: the pale
        // dark-theme colors would then stay on a light background.
        Loaded += (_, _) => Update();
    }

    /// <summary>Status letter, as in git; set by x:Bind (also when an ItemsRepeater recycles the element).</summary>
    public string Letter
    {
        get => _letter;
        set
        {
            _letter = value;
            Update();
        }
    }

    private void Update()
    {
        var light = ActualTheme == ElementTheme.Light;
        var neutral = light ? DesignTokens.Light.Text2 : DesignTokens.Dark.Text2;
        var accent = light ? DesignTokens.Light.AccentText : DesignTokens.Dark.AccentText;
        var danger = light ? DesignTokens.Light.Danger : DesignTokens.Dark.Danger;
        var (glyph, color, name) = _letter switch
        {
            "A" => (FluentIcons.Add, light ? DesignTokens.Light.Success : DesignTokens.Dark.Success, Loc.Get("StatusBadge.Added")),
            "M" => (FluentIcons.Edit, light ? DesignTokens.Light.Warning : DesignTokens.Dark.Warning, Loc.Get("StatusBadge.Modified")),
            "D" => (FluentIcons.Subtract, danger, Loc.Get("StatusBadge.Deleted")),
            "U" => (FluentIcons.Warning, danger, Loc.Get("StatusBadge.Conflict")),
            "R" => (FluentIcons.ArrowRight, accent, Loc.Get("StatusBadge.Renamed")),
            "C" => (FluentIcons.DocumentCopy, accent, Loc.Get("StatusBadge.Copied")),
            "T" => (FluentIcons.ArrowSwap, neutral, Loc.Get("StatusBadge.TypeChanged")),
            "" => (string.Empty, neutral, string.Empty),
            _ => (FluentIcons.Circle, neutral, _letter),
        };
        _icon.Glyph = glyph;
        _icon.Foreground = new SolidColorBrush(color);
        AutomationProperties.SetName(this, name);
        ToolTipService.SetToolTip(this, name.Length == 0 ? null : name);
    }
}
