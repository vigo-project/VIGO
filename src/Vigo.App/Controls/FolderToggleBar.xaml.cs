using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>Collapse all / Expand all button of a changed-files list shown as a tree .</summary>
public sealed partial class FolderToggleBar : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(object), typeof(FolderToggleBar), new PropertyMetadata(null, OnRowsChanged));

    private ObservableCollection<FileListRow>? _watched;
    private bool _collapse;

    public FolderToggleBar()
    {
        InitializeComponent();
        // Subscribed while shown only: the rows outlive the view.
        Loaded += (_, _) => Watch(Rows as ObservableCollection<FileListRow>);
        Unloaded += (_, _) => Watch(null);
    }

    /// <summary>The rows of the list (an <see cref="ObservableCollection{T}"/> of <see cref="FileListRow"/>).</summary>
    public object? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>Raised on click: true to collapse every folder, false to expand them.</summary>
    public event EventHandler<bool>? ToggleRequested;

    private static void OnRowsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var bar = (FolderToggleBar)sender;
        if (bar.IsLoaded) bar.Watch(e.NewValue as ObservableCollection<FileListRow>);
    }

    private void Watch(ObservableCollection<FileListRow>? rows)
    {
        _watched?.CollectionChanged -= OnRowsCollectionChanged;
        _watched = rows;
        _watched?.CollectionChanged += OnRowsCollectionChanged;
        Update();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        var folders = _watched?.OfType<FolderRow>().ToList() ?? [];
        Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _collapse = folders.Any(f => f.IsExpanded);
        Label.Text = Loc.Get(_collapse ? "FolderToggle.CollapseAll" : "FolderToggle.ExpandAll");
    }

    private void OnClick(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke(this, _collapse);
}
