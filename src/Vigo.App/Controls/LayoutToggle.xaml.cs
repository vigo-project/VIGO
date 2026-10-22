using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Services;

namespace Vigo.App.Controls;

/// <summary>Unified / side-by-side toggle: clicking the inactive segment runs <see cref="Command"/> (the view model's toggle).</summary>
public sealed partial class LayoutToggle : UserControl
{
    public static readonly DependencyProperty IsSideBySideProperty =
        DependencyProperty.Register(nameof(IsSideBySide), typeof(bool), typeof(LayoutToggle), new PropertyMetadata(false));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(LayoutToggle), new PropertyMetadata(null));

    public LayoutToggle()
    {
        InitializeComponent();
        Label(UnifiedButton, "Diff.Layout.Unified");
        Label(SideBySideButton, "Diff.Layout.SideBySide");
    }

    public bool IsSideBySide
    {
        get => (bool)GetValue(IsSideBySideProperty);
        set => SetValue(IsSideBySideProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private static void Label(Button button, string key)
    {
        var text = Loc.Get(key);
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    private void OnUnifiedClick(object sender, RoutedEventArgs e) => Request(false);

    private void OnSideBySideClick(object sender, RoutedEventArgs e) => Request(true);

    private void Request(bool sideBySide)
    {
        if (IsSideBySide != sideBySide && Command is { } command && command.CanExecute(null)) command.Execute(null);
    }
}
