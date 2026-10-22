using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Controls;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

public sealed partial class WelcomeView : UserControl
{
    public WelcomeView(WelcomeTabViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // Secondary data loaded after the first render, off the critical path.
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public WelcomeTabViewModel ViewModel { get; }

    public static string IdentityInitials(GitIdentity? identity) => identity?.Initials ?? "?";

    public static string IdentityName(GitIdentity? identity) => identity?.Name ?? Loc.Get("Welcome.Identity.NotSet");

    public static string IdentityEmail(GitIdentity? identity) => identity?.Email ?? "git config --global user.email";

    public static SolidColorBrush LaneBrush(int colorIndex) => LaneBrushes.For(colorIndex);

    public static string PinLabel(bool isPinned) => Loc.Get(isPinned ? "Welcome.Unpin" : "Welcome.Pin");

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"Changer de profil": menu of the profiles, the active one checked.</summary>
    private void OnSwitchProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var (id, label, isActive) in ViewModel.ProfileChoices)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = isActive };
            item.Click += (_, _) =>
            {
                if (!isActive) _ = ViewModel.SwitchProfileAsync(id);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private async void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await ViewModel.OpenRecentCommand.ExecuteAsync(item);
    }

    private async void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await ViewModel.TogglePinCommand.ExecuteAsync(item);
    }

    private async void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await ViewModel.RemoveRecentCommand.ExecuteAsync(item);
    }

    private static RecentRepositoryItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as RecentRepositoryItem;
}
