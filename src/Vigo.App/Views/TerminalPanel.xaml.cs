using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.App.Controls;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.UI.Text;

namespace Vigo.App.Views;

/// <summary>
/// Integrated terminal of a repository tab (J7.10, ADR-071): follows <see cref="TerminalPanelViewModel.Tabs"/> with one
/// <see cref="TerminalView"/> per terminal (the shell of a terminal ends when its tab is closed, or with the repository
/// tab), shows the selected one and gives it the keyboard focus when the panel opens.
/// </summary>
public sealed partial class TerminalPanel : UserControl
{
    private readonly Dictionary<TerminalTabViewModel, TerminalView> _views = [];

    public TerminalPanel() => InitializeComponent();

    /// <summary>Set once by the repository view, right after its InitializeComponent.</summary>
    public TerminalPanelViewModel ViewModel
    {
        get;
        set
        {
            field = value;
            // The x:Bind bindings were evaluated with a null view model during InitializeComponent.
            Bindings.Update();
            GitLogView.ViewModel = value.GitLog;
            value.Tabs.CollectionChanged += OnTabsChanged;
            value.PropertyChanged += OnViewModelPropertyChanged;
            UpdateHint();
            ShortcutMap.Current.Changed += OnShortcutsChanged;
        }
    } = null!;

    public static FontWeight WeightOf(bool selected) => selected ? FontWeights.SemiBold : FontWeights.Normal;

    private void OnShortcutsChanged(object? sender, EventArgs e) => UpdateHint();

    private void UpdateHint() =>
        HintText.Text = ShortcutMap.Current.Display(ShortcutIds.Terminal) is { } shortcut ? Loc.Format("Terminal.Hint", shortcut) : string.Empty;

    private void OnGitTabClick(object sender, RoutedEventArgs e) => ViewModel.ShowGitLogCommand.Execute(null);

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TerminalTabViewModel tab) ViewModel.SelectedTab = tab;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TerminalTabViewModel tab) ViewModel.CloseTabCommand.Execute(tab);
    }

    /// <summary>The "+" menu lists the shells found on the machine.</summary>
    private void OnShellMenuOpening(object? sender, object e)
    {
        ShellMenu.Items.Clear();
        foreach (var shell in ViewModel.Shells)
        {
            var item = new MenuFlyoutItem { Text = shell.Name };
            var chosen = shell;
            item.Click += (_, _) => ViewModel.OpenTab(chosen);
            ShellMenu.Items.Add(item);
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var tab in e.OldItems.OfType<TerminalTabViewModel>().Where(_views.ContainsKey))
            {
                var view = _views[tab];
                _views.Remove(tab);
                Host.Children.Remove(view);
                view.Release();
            }
        }
        if (e.NewItems is not null && ViewModel.IsAvailable)
        {
            foreach (var tab in e.NewItems.OfType<TerminalTabViewModel>())
            {
                var view = new TerminalView(tab) { Visibility = Visibility.Collapsed };
                view.LinkClicked += OnLinkClicked;
                _views[tab] = view;
                Host.Children.Add(view);
            }
        }
    }

    private static void OnLinkClicked(object? sender, Uri url) =>
        _ = App.Services.GetRequiredService<IUrlLauncher>().OpenAsync(url.AbsoluteUri);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalPanelViewModel.SelectedTab) or nameof(TerminalPanelViewModel.IsOpen)
            or nameof(TerminalPanelViewModel.IsGitLogSelected)) ShowSelected();
    }

    private void ShowSelected()
    {
        GitLogView.Visibility = ViewModel.IsGitLogSelected ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tab, view) in _views) view.Visibility = ReferenceEquals(tab, ViewModel.SelectedTab) ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.IsOpen && ViewModel.SelectedTab is { } selected && _views.TryGetValue(selected, out var current))
            DispatcherQueue.TryEnqueue(current.FocusTerminal);
    }

    /// <summary>Called when the repository tab is closed: ends every shell.</summary>
    public void Release()
    {
        ShortcutMap.Current.Changed -= OnShortcutsChanged;
        foreach (var view in _views.Values) view.Release();
        _views.Clear();
        Host.Children.Clear();
    }
}
