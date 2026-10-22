using System.Collections.ObjectModel;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class FileListLayoutTests
{
    private static readonly HashSet<string> NoneCollapsed = [];

    private static ChangedFileItem File(string path) =>
        new(new ChangedFile(path, null, FileChangeKind.Modified, 1, 0, false), 1);

    private static List<ChangedFileItem> Files(params string[] paths) => [.. paths.Select(File)];

    /// <summary>"name@depth" for each row, folders ending with '/'.</summary>
    private static string[] Describe(IEnumerable<FileListRow> rows) =>
        [.. rows.Select(r => r is FolderRow f ? $"{f.Name}/@{f.Depth}" : $"{r.Path[(r.Path.LastIndexOf('/') + 1)..]}@{r.Depth}")];

    [Fact]
    public void Path_mode_lists_the_files_sorted_by_path_with_their_folder()
    {
        var rows = FileListLayout.Arrange(Files("src/b.cs", "README.md", "src/a.cs"), asTree: false, NoneCollapsed);

        Assert.Equal(["README.md", "src/a.cs", "src/b.cs"], rows.Select(r => r.Path));
        Assert.All(rows, r => Assert.Equal((0, false), (r.Depth, r.InTree)));
        Assert.Equal("src", rows[1].DirectoryLabel);
    }

    [Fact]
    public void Tree_mode_shows_folders_first_then_files_and_merges_single_folder_chains()
    {
        var rows = FileListLayout.Arrange(
            Files("README.md", "src/App/Views/Main.xaml", "src/App/Views/Main.xaml.cs", "src/Core/Model.cs", "docs/a/b/c.md"),
            asTree: true, NoneCollapsed);

        Assert.Equal(
            ["docs/a/b/@0", "c.md@1", "src/@0", "App/Views/@1", "Main.xaml@2", "Main.xaml.cs@2", "Core/@1", "Model.cs@2", "README.md@0"],
            Describe(rows));
        var src = Assert.IsType<FolderRow>(rows[2]);
        Assert.Equal(("src", 3, true), (src.Path, src.FileCount, src.IsExpanded));
        Assert.Equal("src/App/Views", rows[3].Path);
        Assert.Equal(string.Empty, rows[4].DirectoryLabel); // the folder row above shows it
    }

    [Fact]
    public void Collapsed_folder_hides_its_files_and_keeps_its_count()
    {
        var rows = FileListLayout.Arrange(Files("src/App/a.cs", "src/b.cs", "c.txt"), asTree: true, new HashSet<string> { "src" });

        Assert.Equal(["src/@0", "c.txt@0"], Describe(rows));
        var src = Assert.IsType<FolderRow>(rows[0]);
        Assert.Equal((2, false), (src.FileCount, src.IsExpanded));
    }

    [Fact]
    public void Sync_keeps_the_rows_still_shown()
    {
        var files = Files("src/a.cs", "src/b.cs");
        var shown = new ObservableCollection<FileListRow>();
        FileListLayout.Sync(shown, FileListLayout.Arrange(files, asTree: true, NoneCollapsed));
        var folder = shown[0];

        FileListLayout.Sync(shown, FileListLayout.Arrange([.. files, File("src/c.cs")], asTree: true, NoneCollapsed));

        Assert.Equal(["src/@0", "a.cs@1", "b.cs@1", "c.cs@1"], Describe(shown));
        Assert.NotSame(folder, shown[0]); // its file count changed
        Assert.Same(files[0], shown[1]);

        FileListLayout.Sync(shown, FileListLayout.Arrange(files, asTree: false, NoneCollapsed));

        Assert.Equal(["a.cs@0", "b.cs@0"], Describe(shown));
        Assert.Same(files[0], shown[0]); // moved back to level 0 in place (observable depth)
    }
}
