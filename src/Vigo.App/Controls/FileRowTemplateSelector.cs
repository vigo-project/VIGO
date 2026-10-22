using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>Template of a changed files list row: a folder of the tree, or a file.</summary>
public sealed partial class FileRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileTemplate { get; set; }

    public DataTemplate? FolderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is FolderRow ? FolderTemplate : FileTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed partial class FolderRowContent : UserControl
{
    public FolderRowContent() => InitializeComponent();

    /// <summary>Folder shown; set by x:Bind (also when an ItemsRepeater recycles the element).</summary>
    public FolderRow? Folder
    {
        get;
        set
        {
            field = value;
            Bindings.Update();
        }
    }

    public static string ChevronGlyph(bool expanded) => expanded ? FluentIcons.ChevronDown : FluentIcons.ChevronRight;

    public static string FolderGlyph(bool expanded) => expanded ? FluentIcons.FolderOpen : FluentIcons.Folder;
}
