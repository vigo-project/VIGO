using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.App.Controls;
using Vigo.App.Services;
using Vigo.Core.Services;

namespace Vigo.App.Views;

/// <summary>An icon of the picker: its Fluent System Icons name and its glyph.</summary>
public sealed record IconEntry(string Name, string Glyph);

/// <summary>Result of the icon dialog: the chosen name, or null to restore the default icon.</summary>
public readonly record struct IconPick(string? Name);

/// <summary>Search box and scrolling grid of every Fluent icon; <see cref="IconChosen"/> fires with the name of the clicked one.</summary>
public sealed partial class TabIconPicker : UserControl
{
    public event Action<string>? IconChosen;

    public TabIconPicker()
    {
        InitializeComponent();
        Fill();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Fill();

    private void OnIconClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is IconEntry entry) IconChosen?.Invoke(entry.Name);
    }

    // Names starting with the query come first, then the ones containing it; no search lists them all.
    private void Fill()
    {
        var query = QueryBox.Text.Trim().ToLowerInvariant().Replace(' ', '_');
        IEnumerable<string> names = query.Length == 0
            ? FluentIconCatalog.Names
            : FluentIconCatalog.Names.Where(n => n.StartsWith(query, StringComparison.Ordinal))
                .Concat(FluentIconCatalog.Names.Where(n => n.Contains(query, StringComparison.Ordinal) && !n.StartsWith(query, StringComparison.Ordinal)));
        var entries = new ObservableCollection<IconEntry>(names.Select(n => new IconEntry(n, FluentIconCatalog.GlyphOf(n)!)));
        IconGrid.ItemsSource = entries;
        EmptyNote.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the picker in a dialog; null when cancelled, a pick with a null name for the secondary "default icon" button.</summary>
    public static async Task<IconPick?> ShowDialogAsync(XamlRoot root, string title)
    {
        var picker = new TabIconPicker();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = Appearance.Current.Theme,
            Title = title,
            Content = picker,
            SecondaryButtonText = Loc.Get("TabIcon.Default"),
            CloseButtonText = Loc.Get("TabIcon.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        string? chosen = null;
        picker.IconChosen += name =>
        {
            chosen = name;
            dialog.Hide();
        };
        var result = await dialog.ShowAsync();
        if (chosen is not null) return new IconPick(chosen);
        return result == ContentDialogResult.Secondary ? new IconPick(null) : null;
    }
}
