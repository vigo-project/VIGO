using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Controls;
using Vigo.App.Services;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Vigo.App.Views;

public sealed partial class PreferencesView : UserControl
{
    public PreferencesView(PreferencesTabViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        FillIcons();
        SectionList.ContainerContentChanging += OnSectionContainerChanging;
    }

    public PreferencesTabViewModel ViewModel { get; }

    /// <summary>Sections of later milestones are listed but cannot be selected (their tooltip names the milestone).</summary>
    private static void OnSectionContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is PreferencesSectionItem item) args.ItemContainer.IsEnabled = item.IsEnabled;
    }

    // "Palette du graphe": the color picker of a swatch edits the lane color at once; the item is carried by Tag.
    private void OnLaneColorPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ColorPicker { Tag: LaneColorItem item } picker) picker.Color = Appearance.Current.Lanes[item.Index];
    }

    private void OnLaneColorChanged(ColorPicker sender, ColorChangedEventArgs e)
    {
        // Also raised when the picker is given the current color on opening: nothing to save then.
        if (sender.Tag is LaneColorItem item && e.NewColor != Appearance.Current.Lanes[item.Index])
            ViewModel.SetLaneColor(item, LaneColors.Format(e.NewColor.R, e.NewColor.G, e.NewColor.B));
    }

    private void OnLaneColorResetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LaneColorItem item) ViewModel.ResetLaneColorCommand.Execute(item);
    }

    // "Raccourcis": a click on the gesture waits for the next key combination, taken here before the accelerators of
    // the window (the app's own shortcuts must not run while the user chooses one). The item is carried by Tag.
    private static ShortcutItem? ShortcutOf(object sender) => (sender as FrameworkElement)?.Tag as ShortcutItem;

    private void OnShortcutCaptureClick(object sender, RoutedEventArgs e)
    {
        if (ShortcutOf(sender) is { IsCapturing: false } item) ViewModel.StartShortcutCaptureCommand.Execute(item);
    }

    private void OnShortcutCaptureLostFocus(object sender, RoutedEventArgs e)
    {
        if (ShortcutOf(sender) is { IsCapturing: true } item) PreferencesTabViewModel.CancelShortcutCapture(item);
    }

    private void OnShortcutCapturePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShortcutOf(sender) is not { IsCapturing: true } item) return;
        var key = (int)e.Key;
        if (KeyGesture.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            PreferencesTabViewModel.CancelShortcutCapture(item);
            return;
        }
        var modifiers = GestureModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= GestureModifiers.Ctrl;
        if (IsDown(VirtualKey.Menu)) modifiers |= GestureModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= GestureModifiers.Shift;
        _ = ViewModel.AssignShortcutAsync(item, new KeyGesture(key, modifiers));
    }

    // Keeps the window's accelerators (Ctrl+T, Ctrl+P…) from running on the combination being chosen.
#pragma warning disable CA1822 // An event handler named in XAML cannot be static.
    private void OnShortcutCaptureProcessAccelerators(UIElement sender, ProcessKeyboardAcceleratorEventArgs e)
#pragma warning restore CA1822
    {
        if (ShortcutOf(sender) is { IsCapturing: true }) e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnShortcutClearClick(object sender, RoutedEventArgs e)
    {
        if (ShortcutOf(sender) is { } item) ViewModel.ClearShortcutCommand.Execute(item);
    }

    private void OnShortcutResetClick(object sender, RoutedEventArgs e)
    {
        if (ShortcutOf(sender) is { } item) ViewModel.ResetShortcutCommand.Execute(item);
    }

    private void OnShortcutResetAllClick(object sender, RoutedEventArgs e) => ViewModel.ResetAllShortcutsCommand.Execute(null);

    // Hosting cards: the item is carried by Tag (x:Bind), like the SSH key cards.
    private static HostConnectionItem? HostOf(object sender) => (sender as FrameworkElement)?.Tag as HostConnectionItem;

    private void OnSignInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is { } host) ViewModel.SignInWithBrowserCommand.Execute(host);
    }

    private void OnTokenEntryClick(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is { } host) ViewModel.BeginTokenEntryCommand.Execute(host);
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is { } host) ViewModel.SignOutCommand.Execute(host);
    }

    private async void OnOpenVerificationClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingCode is not { } code || !Uri.TryCreate(code.VerificationUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void OnCopyUserCodeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingCode is not { } code) return;
        var package = new DataPackage();
        package.SetText(code.UserCode);
        Clipboard.SetContent(package);
    }

    // Key cards: the item is carried by Tag (x:Bind), the flyout items being outside the visual tree of the card.
    private static SshKeyItem? KeyOf(object sender) => (sender as FrameworkElement)?.Tag as SshKeyItem;

    private void OnCopyPublicKeyClick(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is not { } item) return;
        var package = new DataPackage();
        package.SetText(item.Key.PublicKey);
        Clipboard.SetContent(package);
        ViewModel.OnPublicKeyCopied(item);
    }

    private void OnToggleDefaultClick(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is { } item) ViewModel.ToggleDefaultSshKeyCommand.Execute(item);
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is { } item) ViewModel.RemoveSshKeyCommand.Execute(item);
    }

    private void OnDeleteKeyClick(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is { } item) ViewModel.DeleteSshKeyCommand.Execute(item);
    }

    // Profile cards: the item is carried by Tag (x:Bind) like the key cards.
    private static GitProfileItem? ProfileOf(object sender) => (sender as FrameworkElement)?.Tag as GitProfileItem;

    // "Projets": the color picker of a project edits its color at once; the item is carried by Tag.
    private void OnProjectColorPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ColorPicker { Tag: TabProjectItem item } picker && ProjectBrushes.ColorOf(item.ColorIndex, item.Color) is { } color) picker.Color = color;
    }

    private void OnProjectColorChanged(ColorPicker sender, ColorChangedEventArgs e)
    {
        // Also raised when the picker is given the current color on opening: nothing to save then.
        if (sender.Tag is TabProjectItem item && e.NewColor != ProjectBrushes.ColorOf(item.ColorIndex, item.Color))
            ViewModel.SetProjectColor(item, LaneColors.Format(e.NewColor.R, e.NewColor.G, e.NewColor.B));
    }

    private void OnProjectColorResetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabProjectItem item }) ViewModel.ResetProjectColorCommand.Execute(item);
    }

    private void OnProjectRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabProjectItem item }) ViewModel.RemoveProjectCommand.Execute(item);
    }

    private void OnProfileToggleClick(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) item.IsOpen = !item.IsOpen;
    }

    private void OnProfileActivateClick(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) ViewModel.ActivateProfileCommand.Execute(item);
    }

    // "Icônes" section: the grid is filled once, then on each search (virtualized, so every icon is listed).
    private string? _selectedIcon;

    private void OnIconsQueryChanged(object sender, TextChangedEventArgs e) => FillIcons();

    private void FillIcons()
    {
        var query = IconsQuery.Text.Trim().ToLowerInvariant().Replace(' ', '_');
        IEnumerable<string> names = query.Length == 0
            ? FluentIconCatalog.Names
            : FluentIconCatalog.Names.Where(n => n.StartsWith(query, StringComparison.Ordinal))
                .Concat(FluentIconCatalog.Names.Where(n => n.Contains(query, StringComparison.Ordinal) && !n.StartsWith(query, StringComparison.Ordinal)));
        var entries = new System.Collections.ObjectModel.ObservableCollection<IconEntry>(names.Select(n => new IconEntry(n, FluentIconCatalog.GlyphOf(n)!)));
        IconsGrid.ItemsSource = entries;
        IconsEmptyNote.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIconsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconsGrid.SelectedItem is not IconEntry entry) return;
        _selectedIcon = entry.Name;
        SelectedIconGlyph.Glyph = entry.Glyph;
        SelectedIconName.Text = entry.Name;
        AssignProfileButton.IsEnabled = ViewModel.Profiles.Count > 0;
        AssignRepositoryButton.IsEnabled = true;
    }

    private void OnIconAssignProfileClick(object sender, RoutedEventArgs e)
    {
        if (_selectedIcon is not { } icon || sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var profile in ViewModel.Profiles)
        {
            var target = profile;
            var item = new ToggleMenuFlyoutItem { Text = profile.Label, IsChecked = profile.Icon == icon };
            item.Click += (_, _) => target.Icon = icon;
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void OnIconAssignRepositoryClick(object sender, RoutedEventArgs e)
    {
        if (_selectedIcon is not { } icon || sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var repository in ViewModel.KnownRepositories)
        {
            var path = repository.Path;
            var item = new MenuFlyoutItem { Text = repository.Name };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) => ViewModel.AssignRepositoryIcon?.Invoke(path, icon);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("PrefIcons.NoRepository"), IsEnabled = false });
        menu.ShowAt(anchor);
    }

    private bool _pickingProfileIcon;

    private async void OnProfileIconClick(object sender, RoutedEventArgs e)
    {
        if (_pickingProfileIcon || ProfileOf(sender) is not { } item || XamlRoot is not { } root) return;
        _pickingProfileIcon = true;
        try
        {
            if (await TabIconPicker.ShowDialogAsync(root, Loc.Get("ProfileIcon.Title")) is { } pick) item.Icon = pick.Name;
        }
        finally
        {
            _pickingProfileIcon = false;
        }
    }

    private void OnProfileRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) ViewModel.RemoveProfileCommand.Execute(item);
    }

    /// <summary>"Choisir une clé…" (J7.7): the keys found on this machine in a menu built when it opens; picking one fills the key and the format.</summary>
    private async void OnProfileSigningKeyClick(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } item || sender is not FrameworkElement anchor) return;
        var choices = await ViewModel.ListSigningKeysAsync(item.SigningFormat);

        var flyout = new MenuFlyout();
        foreach (var choice in choices)
        {
            var entry = new MenuFlyoutItem { Text = choice.Label };
            entry.Click += (_, _) =>
            {
                item.SigningKey = choice.Value;
                item.SigningFormat = choice.Format;
            };
            flyout.Items.Add(entry);
        }
        if (choices.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = Loc.Get("SigningKey.None"), IsEnabled = false });
        if (ViewModel.CanGenerateGpgKey && item.SigningFormat is null or SigningFormats.OpenPgp)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var generateGpg = new MenuFlyoutItem { Text = Loc.Get("SigningKey.GenerateGpg") };
            generateGpg.Click += async (_, _) =>
            {
                if (await ViewModel.GenerateGpgSigningKeyAsync(item.Name, item.Email) is not { } created) return;
                item.SigningKey = created.Value;
                item.SigningFormat = created.Format;
            };
            flyout.Items.Add(generateGpg);
        }
        if (choices.Any(c => c.Format == SigningFormats.OpenPgp))
        {
            var copyGpg = new MenuFlyoutItem { Text = Loc.Get("SigningKey.CopyGpg") };
            copyGpg.Click += async (_, _) =>
            {
                var keyId = choices.FirstOrDefault(c => c.Format == SigningFormats.OpenPgp && c.Value == item.SigningKey)?.Value;
                if (keyId is null || await ViewModel.ExportGpgPublicKeyAsync(keyId) is not { } armored) return;
                var package = new DataPackage();
                package.SetText(armored);
                Clipboard.SetContent(package);
            };
            copyGpg.IsEnabled = choices.Any(c => c.Format == SigningFormats.OpenPgp && c.Value == item.SigningKey);
            flyout.Items.Add(copyGpg);
        }
        if (ViewModel.CanGenerateSshKey && item.SigningFormat is null or SigningFormats.Ssh)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var generate = new MenuFlyoutItem { Text = Loc.Get("SigningKey.Generate") };
            generate.Click += async (_, _) =>
            {
                if (await ViewModel.GenerateSshSigningKeyAsync() is not { } created) return;
                item.SigningKey = created.Value;
                item.SigningFormat = created.Format;
            };
            flyout.Items.Add(generate);
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>Private key (or its .pub) of another tool, e.g. one generated for GitHub.</summary>
    private async void OnImportKeyClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } environment) return;
        var picker = new FileOpenPicker(environment.AppWindowId)
        {
            Title = Loc.Get("SshKeys.ImportPicker.Title"),
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        if (ViewModel.SshKeyDirectory is { } directory && Directory.Exists(directory)) picker.SuggestedFolder = directory;
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.ImportSshKeyAsync(file.Path);
    }
}
