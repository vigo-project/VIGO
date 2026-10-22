using System.Collections.Immutable;
using Vigo.Core.Services;

namespace Vigo.Core.Models;

/// <summary>Where a configurable shortcut is active.</summary>
public enum ShortcutScope
{
    /// <summary>Anywhere in the window (tabs, palette, preferences…).</summary>
    Window,

    /// <summary>While a repository tab is shown (network operations, branches, undo…).</summary>
    Repository,
}

/// <summary>A command whose shortcut the user can change (preferences › Raccourcis).</summary>
/// <param name="Id">Stable identifier stored in <c>preferences.json</c>.</param>
/// <param name="LabelKey">Resource key of the name shown in the list.</param>
public sealed record ShortcutDefinition(string Id, string LabelKey, ShortcutScope Scope, KeyGesture Default)
{
    /// <summary>Localized name, read when shown (the catalog is built before the texts are guaranteed to be installed).</summary>
    public string Label => Loc.Get(LabelKey);
}

/// <summary>Identifiers of the configurable shortcuts.</summary>
public static class ShortcutIds
{
    public const string NewTab = "tab.new";
    public const string CloseTab = "tab.close";
    public const string OpenRepository = "repo.open";
    public const string CloneRepository = "repo.clone";
    public const string InitRepository = "repo.init";
    public const string Palette = "palette";
    public const string NewWindow = "window.new";
    public const string Quit = "app.quit";
    public const string Preferences = "app.preferences";
    public const string OnlineHelp = "app.help";
    public const string Fetch = "git.fetch";
    public const string Pull = "git.pull";
    public const string Push = "git.push";
    public const string NewBranch = "git.branch";
    public const string Stash = "git.stash";
    public const string Undo = "git.undo";
    public const string Redo = "git.redo";
    public const string Terminal = "terminal.toggle";
}

/// <summary>The configurable shortcuts with their defaults, and the fixed ones they must not collide with.</summary>
public static class ShortcutCatalog
{
    private const GestureModifiers Ctrl = GestureModifiers.Ctrl, Alt = GestureModifiers.Alt, Shift = GestureModifiers.Shift;

    public static ImmutableArray<ShortcutDefinition> All { get; } =
    [
        new(ShortcutIds.NewTab, "Shortcuts.Name.NewTab", ShortcutScope.Window, new('T', Ctrl)),
        new(ShortcutIds.CloseTab, "Shortcuts.Name.CloseTab", ShortcutScope.Window, new('W', Ctrl)),
        new(ShortcutIds.OpenRepository, "Shortcuts.Name.OpenRepository", ShortcutScope.Window, new('O', Ctrl)),
        new(ShortcutIds.CloneRepository, "Shortcuts.Name.CloneRepository", ShortcutScope.Window, new('O', Ctrl | Shift)),
        new(ShortcutIds.InitRepository, "Shortcuts.Name.InitRepository", ShortcutScope.Window, new('N', Ctrl | Alt)),
        new(ShortcutIds.Palette, "Shortcuts.Name.Palette", ShortcutScope.Window, new('P', Ctrl)),
        new(ShortcutIds.NewWindow, "Shortcuts.Name.NewWindow", ShortcutScope.Window, new('N', Ctrl | Shift)),
        new(ShortcutIds.Preferences, "Shortcuts.Name.Preferences", ShortcutScope.Window, new(188, Ctrl)),
        new(ShortcutIds.Quit, "Shortcuts.Name.Quit", ShortcutScope.Window, new('Q', Ctrl)),
        new(ShortcutIds.OnlineHelp, "Shortcuts.Name.OnlineHelp", ShortcutScope.Window, new(112, GestureModifiers.None)),
        new(ShortcutIds.Fetch, "Shortcuts.Name.Fetch", ShortcutScope.Repository, new(116, GestureModifiers.None)),
        new(ShortcutIds.Pull, "Shortcuts.Name.Pull", ShortcutScope.Repository, new('L', Ctrl | Shift)),
        new(ShortcutIds.Push, "Shortcuts.Name.Push", ShortcutScope.Repository, new('P', Ctrl | Shift)),
        new(ShortcutIds.NewBranch, "Shortcuts.Name.NewBranch", ShortcutScope.Repository, new('B', Ctrl)),
        new(ShortcutIds.Stash, "Shortcuts.Name.Stash", ShortcutScope.Repository, new('S', Ctrl | Shift)),
        new(ShortcutIds.Undo, "Shortcuts.Name.Undo", ShortcutScope.Repository, new('Z', Ctrl)),
        new(ShortcutIds.Redo, "Shortcuts.Name.Redo", ShortcutScope.Repository, new('Y', Ctrl)),
        new(ShortcutIds.Terminal, "Shortcuts.Name.Terminal", ShortcutScope.Repository, new(192, Ctrl)),
    ];

    /// <summary>
    /// Gestures the application uses itself (text editing, commit form, editors) and that cannot be reassigned,
    /// with what they do.
    /// </summary>
    public static ImmutableArray<(KeyGesture Gesture, string Use)> Reserved =>
    [
        (new('C', Ctrl), Loc.Get("Shortcuts.Reserved.Copy")),
        (new('V', Ctrl), Loc.Get("Shortcuts.Reserved.Paste")),
        (new('X', Ctrl), Loc.Get("Shortcuts.Reserved.Cut")),
        (new('A', Ctrl), Loc.Get("Shortcuts.Reserved.SelectAll")),
        (new('F', Ctrl), Loc.Get("Shortcuts.Reserved.EditorFind")),
        (new('S', Ctrl), Loc.Get("Shortcuts.Reserved.SaveConflict")),
        (new('M', Ctrl | Shift), Loc.Get("Shortcuts.Reserved.ToggleDiff")),
        (new(KeyGesture.Enter, Ctrl), Loc.Get("Shortcuts.Reserved.Commit")),
        (new(KeyGesture.Enter, Ctrl | Shift), Loc.Get("Shortcuts.Reserved.CommitPush")),
        (new(115, Alt), Loc.Get("Shortcuts.Reserved.CloseWindow")),
    ];

    public static ShortcutDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}
