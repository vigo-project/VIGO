using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Groups of the command palette, in display order.</summary>
public enum PaletteGroup
{
    Commands,
    Branches,
    Files,
    Commits,
}

/// <summary>Something the command palette can run: a command, a branch to check out, a file or commit to show.</summary>
/// <param name="Shortcut">Keyboard shortcut shown on the right ("Ctrl+Maj+L"), if any.</param>
/// <param name="Detail">Secondary text after the label (path, SHA, "checkout"), searched too.</param>
/// <param name="Picker">Instead of running at once, the entry opens this second list in the palette (a file to pick…).</param>
public sealed record PaletteEntry(
    PaletteGroup Group, string IconName, string Label, Func<Task> Run, string Detail = "", string? Shortcut = null, PalettePicker? Picker = null);

/// <summary>Second step of an entry: a list read when the entry is chosen (J5.6: the files of the repository).</summary>
/// <param name="Title">Title of the group of the list.</param>
/// <param name="Placeholder">Text of the empty search box.</param>
public sealed record PalettePicker(string Title, string Placeholder, Func<CancellationToken, Task<IReadOnlyList<PaletteEntry>>> Load);

/// <summary>Row of the palette list: an entry, with the title of its group on the first row of the group.</summary>
public sealed record PaletteItem(PaletteEntry Entry, string? GroupTitle)
{
    public bool HasGroupTitle => GroupTitle is not null;

    public bool HasShortcut => Entry.Shortcut is not null;

    /// <summary>Name read by the narrator (and by UI Automation).</summary>
    public override string ToString() => Entry.Detail.Length > 0 ? $"{Entry.Label}, {Entry.Detail}" : Entry.Label;
}

/// <summary>What the palette searches, provided by the shell when it opens.</summary>
public interface IPaletteSource
{
    /// <summary>Commands, branches and files: small lists, filtered in memory.</summary>
    IReadOnlyList<PaletteEntry> Entries { get; }

    /// <summary>
    /// Commits whose subject or SHA match <paramref name="matches"/>, at most <paramref name="limit"/> (the history can
    /// hold millions of commits: the source bounds how many it reads).
    /// </summary>
    IEnumerable<PaletteEntry> FindCommits(Func<string, bool> matches, int limit);
}

/// <summary>
/// Command palette (Ctrl+P, mockup VIGO.dc.html): fuzzy search over commands, branches, modified files and loaded
/// commits. Prefixes restrict the search: <c>&gt;</c> commands, <c>@</c> branches, <c>#</c> commits.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    // Without a query: every command, then a few of each other group (mockup). With one: more per group.
    private const int BrowseLimit = 6;
    private const int SearchLimit = 30;
    private const int ScopedLimit = 200;

    private readonly IPaletteSource _source;

    public CommandPaletteViewModel(IPaletteSource source, string query = "")
    {
        _source = source;
        Query = query;
        Refresh();
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    /// <summary>Text typed in the search box, prefix included.</summary>
    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool HasNoResult { get; private set; }

    public string NoResultText => _picked is null && _picker is not null
        ? Loc.Get("Palette.Loading")
        : Loc.Format("Palette.NoResult", Query.Trim());

    /// <summary>Text of the empty search box: the one of the picker shown, null for the default one.</summary>
    [ObservableProperty]
    public partial string? Placeholder { get; private set; }

    // Second step of an entry (PaletteEntry.Picker): its list replaces the search over the sources.
    private PalettePicker? _picker;
    private IReadOnlyList<PaletteEntry>? _picked;
    private int _pickings;

    /// <summary>Raised once an entry has been picked: the palette closes, then the entry runs.</summary>
    public event EventHandler? Closed;

    partial void OnQueryChanged(string value)
    {
        Refresh();
        OnPropertyChanged(nameof(NoResultText));
    }

    /// <summary>↑ / ↓: moves the highlighted row, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        SelectedIndex = ((SelectedIndex + delta) % Items.Count + Items.Count) % Items.Count;
    }

    /// <summary>Enter or click: closes the palette and runs the highlighted entry.</summary>
    public Task RunSelectedAsync() =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? RunAsync(Items[SelectedIndex]) : Task.CompletedTask;

    public Task RunAsync(PaletteItem item)
    {
        if (item.Entry.Picker is { } picker) return PickAsync(picker);
        _pickings++;
        Closed?.Invoke(this, EventArgs.Empty);
        return item.Entry.Run();
    }

    /// <summary>Shows the list of <paramref name="picker"/> in place of the results, the search box emptied.</summary>
    private async Task PickAsync(PalettePicker picker)
    {
        var picking = ++_pickings;
        _picker = picker;
        _picked = null;
        Placeholder = picker.Placeholder;
        Query = string.Empty;
        Refresh();
        OnPropertyChanged(nameof(NoResultText));

        IReadOnlyList<PaletteEntry> entries;
        try
        {
            entries = await picker.Load(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // Another entry was picked meanwhile, or the palette ran an entry.
        if (picking != _pickings) return;
        _picked = entries;
        Refresh();
        OnPropertyChanged(nameof(NoResultText));
    }

    private void Refresh()
    {
        var (scope, text) = _picker is null ? ParseQuery(Query) : (null, Query.Trim());
        var entries = _picker is null ? Search(scope, text) : SearchPicked(text);

        // Updated in place, row by row: clearing and refilling the collection rebuilds the whole list at each keystroke
        // (visible flicker). Rows equal to the previous ones (records) are left untouched.
        PaletteGroup? group = null;
        var index = 0;
        foreach (var entry in entries)
        {
            var item = new PaletteItem(entry, entry.Group == group ? null : _picker?.Title ?? TitleOf(entry.Group));
            group = entry.Group;
            if (index < Items.Count)
            {
                if (!Items[index].Equals(item)) Items[index] = item;
            }
            else
            {
                Items.Add(item);
            }
            index++;
        }
        while (Items.Count > index) Items.RemoveAt(Items.Count - 1);
        SelectedIndex = Items.Count > 0 ? 0 : -1;
        HasNoResult = Items.Count == 0;
    }

    private List<PaletteEntry> Search(PaletteGroup? scope, string text)
    {
        var result = new List<PaletteEntry>();
        foreach (var group in Enum.GetValues<PaletteGroup>())
        {
            if (scope is not null && scope != group) continue;
            var limit = scope is not null ? ScopedLimit : group == PaletteGroup.Commands ? int.MaxValue : text.Length == 0 ? BrowseLimit : SearchLimit;
            if (group == PaletteGroup.Commits)
            {
                // Read in history order; a substring match is not ranked above a scattered one here (bounded scan).
                result.AddRange(_source.FindCommits(s => FuzzyMatch(s, text) >= 0, limit));
                continue;
            }

            result.AddRange(_source.Entries
                .Where(e => e.Group == group)
                .Select((e, index) => (Entry: e, Index: index, Score: Score(e, text)))
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Index)
                .Take(limit)
                .Select(x => x.Entry));
        }
        return result;
    }

    /// <summary>Entries of the picker shown that match, best first (a repository can hold tens of thousands of files).</summary>
    private List<PaletteEntry> SearchPicked(string text) =>
        [.. (_picked ?? [])
            .Select((e, index) => (Entry: e, Index: index, Score: Score(e, text)))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(ScopedLimit)
            .Select(x => x.Entry)];

    /// <summary>Splits the scope prefix (<c>&gt;</c>, <c>@</c>, <c>#</c>) from the searched text.</summary>
    internal static (PaletteGroup? Scope, string Text) ParseQuery(string query)
    {
        var trimmed = query.TrimStart();
        PaletteGroup? scope = trimmed.Length > 0 ? trimmed[0] switch
        {
            '>' => PaletteGroup.Commands,
            '@' => PaletteGroup.Branches,
            '#' => PaletteGroup.Commits,
            _ => null,
        } : null;
        return (scope, (scope is null ? trimmed : trimmed[1..]).Trim());
    }

    /// <summary>Rank of an entry for <paramref name="text"/> (lower is better), -1 when it does not match.</summary>
    private static int Score(PaletteEntry entry, string text)
    {
        if (text.Length == 0) return 0;
        var label = FuzzyMatch(entry.Label, text);
        if (label >= 0) return label;
        var detail = FuzzyMatch(entry.Detail, text);
        return detail >= 0 ? detail + 3 : -1;
    }

    /// <summary>
    /// 0 when <paramref name="value"/> starts with <paramref name="text"/>, 1 when it contains it, 2 when its characters
    /// appear in order (mockup: subsequence), -1 otherwise. Case is ignored, and accents in the subsequence test (ordinal
    /// comparisons only: called for thousands of commits per keystroke).
    /// </summary>
    internal static int FuzzyMatch(string value, string text)
    {
        if (text.Length == 0) return 0;
        var index = value.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 0;
        if (index > 0) return 1;

        var position = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            position = IndexOfIgnoringCase(value, c, position);
            if (position < 0) return -1;
            position++;
        }
        return 2;
    }

    private static int IndexOfIgnoringCase(string value, char c, int start)
    {
        var lower = char.ToLowerInvariant(RemoveAccent(c));
        for (var i = start; i < value.Length; i++)
        {
            if (char.ToLowerInvariant(RemoveAccent(value[i])) == lower) return i;
        }
        return -1;
    }

    private static char RemoveAccent(char c) => c < 128 ? c
        : "àâäáãå".Contains(c) ? 'a' : "éèêë".Contains(c) ? 'e' : "îïíì".Contains(c) ? 'i' : "ôöóòõ".Contains(c) ? 'o'
        : "ùûüú".Contains(c) ? 'u' : c == 'ç' ? 'c' : c;

    private static string TitleOf(PaletteGroup group) => group switch
    {
        PaletteGroup.Commands => Loc.Get("Palette.Group.Commands"),
        PaletteGroup.Branches => Loc.Get("Palette.Group.Branches"),
        PaletteGroup.Files => Loc.Get("Palette.Group.Files"),
        PaletteGroup.Commits => Loc.Get("Palette.Group.Commits"),
        _ => string.Empty,
    };
}
