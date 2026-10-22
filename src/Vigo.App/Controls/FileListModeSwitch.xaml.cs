using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>Path / Tree switch of the changed files lists, bound to <see cref="LayoutState.FileListAsTree"/>.</summary>
public sealed partial class FileListModeSwitch : UserControl, INotifyPropertyChanged
{
    public FileListModeSwitch()
    {
        InitializeComponent();
        // Subscribed while shown only: the layout outlives the tabs.
        Loaded += (_, _) =>
        {
            Layout?.PropertyChanged += OnLayoutPropertyChanged;
            RaiseIsTreeChanged();
        };
        Unloaded += (_, _) => Layout?.PropertyChanged -= OnLayoutPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Shared layout; set once by the panel that shows the switch, before it is loaded.</summary>
    public LayoutState? Layout { get; set; }

    public bool IsTree => Layout?.FileListAsTree ?? false;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutState.FileListAsTree)) RaiseIsTreeChanged();
    }

    private void RaiseIsTreeChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTree)));

    private void OnPathClick(object sender, RoutedEventArgs e) => SetTree(false);

    private void OnTreeClick(object sender, RoutedEventArgs e) => SetTree(true);

    private void SetTree(bool tree)
    {
        if (Layout is null || Layout.FileListAsTree == tree) return;
        Layout.FileListAsTree = tree;
        _ = Layout.SaveAsync();
    }
}
