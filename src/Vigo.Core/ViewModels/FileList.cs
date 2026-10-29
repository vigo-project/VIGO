using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Row of a changed-files list (commit details, working directory): a file, or a folder when the list is shown as a
/// tree. <see cref="Depth"/> and <see cref="InTree"/> are set by <see cref="FileListLayout"/> before the row is shown.
/// </summary>
public abstract class FileListRow : ObservableObject
{
    /// <summary>Indentation width of one tree level, in device-independent pixels.</summary>
    public const double IndentStep = 16;

    /// <summary>Repository-relative path, '/'-separated.</summary>
    public abstract string Path { get; }

    /// <summary>Nesting level in tree mode; 0 in path mode. Observable: a row kept across a mode change moves.</summary>
    public int Depth
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(Indent));
        }
    }

    /// <summary>The list is shown as a tree: a file row shows its name only, its folder being the parent row.</summary>
    public bool InTree
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(DirectoryLabel));
        }
    }

    /// <summary>Folder shown after a file name in path mode; empty in tree mode (the parent row shows it).</summary>
    public string DirectoryLabel => InTree || Path.LastIndexOf('/') is not (var i and > 0) ? string.Empty : Path[..i];

    public double Indent => Depth * IndentStep;

    /// <summary>Identity of the row across refreshes (a folder and a file may share a path).</summary>
    internal virtual string Key => Path;
}

/// <summary>
/// Folder of a list shown as a tree. Chains of folders holding a single folder are merged into one row
/// ("src/Vigo.App/Views").
/// </summary>
public sealed class FolderRow(string path, string name, int fileCount, bool isExpanded) : FileListRow
{
    public override string Path { get; } = path;

    /// <summary>Displayed name: the folder name, or the merged chain of single folders.</summary>
    public string Name { get; } = name;

    /// <summary>Number of files below the folder, nested folders included.</summary>
    public int FileCount { get; } = fileCount;

    public string FileCountText => FileCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public bool IsExpanded { get; } = isExpanded;

    /// <summary>Group of the working-directory panel that shows the folder (set by the panel; unused elsewhere).</summary>
    public FileGroup Group { get; internal set; }

    internal override string Key => Path + "/";
}

/// <summary>Group of files of the working-directory panel.</summary>
public enum FileGroup
{
    Conflicted,
    Unstaged,
    Staged,
}

/// <summary>How a click changes the selection of the files of a group (no key / Ctrl / Shift held).</summary>
public enum FileSelectionMode
{
    Replace,
    Toggle,
    Range,
}

/// <summary>Arranges the files of a list as rows: sorted paths, or a tree of folders.</summary>
public static class FileListLayout
{
    /// <summary>
    /// Rows of <paramref name="files"/>: in path mode, the files sorted by path; in tree mode, each folder followed by
    /// its sub-folders then its files (both sorted by name), the files of a collapsed folder being left out.
    /// </summary>
    public static List<FileListRow> Arrange(IEnumerable<FileListRow> files, bool asTree, IReadOnlySet<string> collapsed)
    {
        var sorted = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (!asTree)
        {
            foreach (var file in sorted)
                (file.Depth, file.InTree) = (0, false);
            return sorted;
        }

        var root = new Folder(string.Empty, string.Empty);
        foreach (var file in sorted)
        {
            var folder = root;
            var parts = file.Path.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!folder.Folders.TryGetValue(parts[i], out var child))
                {
                    child = new Folder(folder.Path.Length == 0 ? parts[i] : $"{folder.Path}/{parts[i]}", parts[i]);
                    folder.Folders.Add(parts[i], child);
                }

                folder = child;
            }

            folder.Files.Add(file);
        }

        var rows = new List<FileListRow>();
        Emit(root, -1, collapsed, rows);
        return rows;
    }

    /// <summary>Every folder that holds a file of <paramref name="files"/>, ancestors included ("a", "a/b"): what "collapse all" collapses.</summary>
    public static IEnumerable<string> FolderPaths(IEnumerable<FileListRow> files) => files
        .SelectMany(f =>
        {
            var parts = f.Path.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(n => string.Join('/', parts.Take(n)));
        })
        .Distinct(StringComparer.Ordinal);

    /// <summary>Order of two paths as <see cref="Arrange"/> shows them: in a tree, a folder's subfolders come before its files.</summary>
    public static int Compare(string a, string b, bool asTree)
    {
        if (!asTree) return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        string[] x = a.Split('/'), y = b.Split('/');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var folderX = i < x.Length - 1;
            var folderY = i < y.Length - 1;
            if (folderX != folderY) return folderX ? -1 : 1;
            var order = StringComparer.OrdinalIgnoreCase.Compare(x[i], y[i]);
            if (order != 0) return order;
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int Emit(Folder folder, int depth, IReadOnlySet<string> collapsed, List<FileListRow> rows)
    {
        var count = folder.Files.Count;
        foreach (var child in folder.Folders.Values)
        {
            // Merges "a" → "a/b" → "a/b/c" while a folder holds a single folder and no file.
            var shown = child;
            var name = child.Name;
            while (shown.Files.Count == 0 && shown.Folders.Count == 1)
            {
                shown = shown.Folders.Values[0];
                name = $"{name}/{shown.Name}";
            }

            var expanded = !collapsed.Contains(shown.Path);
            var index = rows.Count;
            rows.Add(null!); // placeholder: the file count is known once the children are emitted
            var below = expanded
                ? Emit(shown, depth + 1, collapsed, rows)
                : CountFiles(shown);
            rows[index] = new FolderRow(shown.Path, name, below, expanded) { Depth = depth + 1, InTree = true };
            count += below;
        }

        foreach (var file in folder.Files)
        {
            (file.Depth, file.InTree) = (depth + 1, true);
            rows.Add(file);
        }

        return count;
    }

    private static int CountFiles(Folder folder) => folder.Files.Count + folder.Folders.Values.Sum(CountFiles);

    /// <summary>
    /// Updates <paramref name="target"/> to show <paramref name="desired"/> (same order) with as few collection
    /// changes as possible, so that the list does not flicker nor lose its scroll position: rows that vanished are
    /// removed, new ones inserted, changed ones replaced (a file row kept follows its level by itself).
    /// </summary>
    public static void Sync(ObservableCollection<FileListRow> target, IReadOnlyList<FileListRow> desired)
    {
        var order = new Dictionary<string, int>(desired.Count, StringComparer.Ordinal);
        for (var i = 0; i < desired.Count; i++)
            order[desired[i].Key] = i;
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!order.ContainsKey(target[i].Key)) target.RemoveAt(i);
        }

        // Rows kept in another order (path / tree switch): shown again from scratch.
        for (var i = 1; i < target.Count; i++)
        {
            if (order[target[i - 1].Key] < order[target[i].Key]) continue;
            target.Clear();
            break;
        }

        // What remains is a subsequence of `desired`, in the same order.
        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (i < target.Count && target[i].Key == row.Key)
            {
                if (!SameRow(target[i], row)) target[i] = row;
            }
            else
            {
                target.Insert(i, row);
            }
        }
    }

    private static bool SameRow(FileListRow shown, FileListRow row) => (shown, row) switch
    {
        (FolderRow a, FolderRow b) => a.Name == b.Name && a.Depth == b.Depth && a.FileCount == b.FileCount && a.IsExpanded == b.IsExpanded,
        _ => ReferenceEquals(shown, row),
    };

    private sealed class Folder(string path, string name)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public SortedList<string, Folder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<FileListRow> Files { get; } = [];
    }
}
