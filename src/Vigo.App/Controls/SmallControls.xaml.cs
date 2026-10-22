using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Services;
using Windows.UI;

namespace Vigo.App.Controls;

// CLR properties set once by the XAML parser then read by x:Bind (OneTime):
// no DependencyProperty needed for these static controls.

public sealed partial class ActionTileContent : UserControl
{
    public ActionTileContent() => InitializeComponent();

    public string Icon { get; set; } = "circle";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Shortcut { get; set; } = string.Empty;
}

public sealed partial class HostRow : UserControl
{
    public HostRow() => InitializeComponent();

    public string Initials { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public Color Color { get; set; }

    // Sign-in state, bound to the view model for the services that support sign-in (J6.2): dependency properties.

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(HostRow), new PropertyMetadata(Loc.Get("Welcome.Host.NotConnected")));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(HostRow), new PropertyMetadata(Loc.Get("Welcome.Host.Connect")));

    public static readonly DependencyProperty ActionToolTipProperty = DependencyProperty.Register(
        nameof(ActionToolTip), typeof(string), typeof(HostRow), new PropertyMetadata(Loc.Get("Welcome.Host.SoonAvailable")));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(HostRow), new PropertyMetadata(null));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public string ActionToolTip
    {
        get => (string)GetValue(ActionToolTipProperty);
        set => SetValue(ActionToolTipProperty, value);
    }

    /// <summary>Command of the button; null (service not supported yet) leaves the button disabled.</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public static bool HasAction(ICommand? action) => action is not null;

    public static string? ToolTipOf(ICommand? action, string toolTip) => action is null ? toolTip : null;
}

public sealed partial class ToolbarButtonContent : UserControl
{
    public ToolbarButtonContent() => InitializeComponent();

    public string Icon { get; set; } = "circle";
    public string Label { get; set; } = string.Empty;
}
