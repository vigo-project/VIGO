using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>
/// Work item (J6.14): header, rich text fields and timeline on the left, fields, links and attachments on the right (below on a narrow
/// view). Échap goes back to the previous work item of the trail, or to the graph.
/// </summary>
public sealed partial class WorkItemView : UserControl
{
    private const double MaxContentWidth = 1240;
    private const double SideWidth = 340;
    private const double StackBelow = 860;

    private WorkItemViewModel? _viewModel;

    public WorkItemView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            Back();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        ActualThemeChanged += (_, _) => UpdateBadges();
    }

    /// <summary>Work item shown by the control; set once by the repository view.</summary>
    public WorkItemViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
            UpdateBadges();
        }
    }

    public void Release() => ViewModel = null;

    /// <summary>The main column has no text field to show: only said once the item is read.</summary>
    public static Visibility NoTexts(bool hasTexts, bool hasDetail) => hasDetail && !hasTexts ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Icon of a link kind.</summary>
    public static string GlyphOf(WorkItemLinkKind kind) => kind switch
    {
        WorkItemLinkKind.Parent or WorkItemLinkKind.Child or WorkItemLinkKind.Related or WorkItemLinkKind.Predecessor
            or WorkItemLinkKind.Successor or WorkItemLinkKind.Duplicate => FluentIcons.ClipboardTask,
        WorkItemLinkKind.Tests => FluentIcons.Checkmark,
        WorkItemLinkKind.PullRequest => FluentIcons.Merge,
        WorkItemLinkKind.Commit or WorkItemLinkKind.Branch => FluentIcons.Branch,
        WorkItemLinkKind.Build => FluentIcons.Trophy,
        _ => FluentIcons.Open,
    };

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkItemViewModel.Detail) or nameof(WorkItemViewModel.Issue)) UpdateBadges();
    }

    // Colors from DesignTokens (or parsed): a brush read from the resources cannot be cast under AOT (see CLAUDE.md).
    private void UpdateBadges()
    {
        var light = ActualTheme == ElementTheme.Light;
        var text = _viewModel?.StateText ?? string.Empty;
        StateLabel.Text = text;
        StateBadge.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyStateColors(StateBadge, StateLabel, _viewModel?.StateKind ?? WorkItemStateKind.Proposed, light);

        var accent = light ? DesignTokens.Light.Accent : DesignTokens.Dark.Accent;
        TypeBar.Background = new SolidColorBrush(TryColor(_viewModel?.TypeColor) ?? accent);
    }

    // Same colors for the state of the work item in the header and for the states of its links.
    private static void ApplyStateColors(Border badge, TextBlock label, WorkItemStateKind kind, bool light)
    {
        var (back, fore) = kind switch
        {
            WorkItemStateKind.InProgress => (light ? DesignTokens.Light.AccentSoft2 : DesignTokens.Dark.AccentSoft2, light ? DesignTokens.Light.AccentText : DesignTokens.Dark.AccentText),
            WorkItemStateKind.Resolved => (light ? DesignTokens.Light.WarningSoft : DesignTokens.Dark.WarningSoft, light ? DesignTokens.Light.Warning : DesignTokens.Dark.Warning),
            WorkItemStateKind.Completed => (light ? DesignTokens.Light.SuccessSoft : DesignTokens.Dark.SuccessSoft, light ? DesignTokens.Light.Success : DesignTokens.Dark.Success),
            WorkItemStateKind.Removed => (light ? DesignTokens.Light.DangerSoft : DesignTokens.Dark.DangerSoft, light ? DesignTokens.Light.Danger : DesignTokens.Dark.Danger),
            _ => (light ? DesignTokens.Light.Layer2 : DesignTokens.Dark.Layer2, light ? DesignTokens.Light.Text2 : DesignTokens.Dark.Text2),
        };
        badge.Background = new SolidColorBrush(back);
        label.Foreground = new SolidColorBrush(fore);
    }

    // The badge of a link is part of a data template: colored once it is created, and again when the theme changes.
    private void OnLinkStateLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { Child: TextBlock label } badge || badge.DataContext is not WorkItemLinkRow row) return;
        ApplyStateColors(badge, label, row.StateKind, badge.ActualTheme == ElementTheme.Light);
        badge.ActualThemeChanged += (_, _) => ApplyStateColors(badge, label, row.StateKind, badge.ActualTheme == ElementTheme.Light);
    }

    private static Color? TryColor(string? hex)
    {
        if (hex is not { Length: 6 } || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    // The content gets exactly the width of the view and is centered by hand (see IssueView); the side column goes below a narrow one.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        Header.Width = width;
        Pages.Width = width;
        ContentScroll.Width = width;
        var column = Math.Min(MaxContentWidth, width);
        ContentColumn.Width = column;
        ContentColumn.Margin = new Thickness(Math.Max(0, (width - column) / 2), 0, 0, 0);

        var narrow = column < StackBelow;
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(SideWidth);
        Grid.SetColumn(SidePanel, narrow ? 0 : 1);
        Grid.SetRow(SidePanel, narrow ? 1 : 0);
    }

    private void Back() => _ = _viewModel?.BackAsync();

    private void OnBackClick(object sender, RoutedEventArgs e) => Back();

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkItemLinkRow row) _viewModel?.OpenLinkCommand.Execute(row);
    }

    private void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkItemAttachmentRow row) _viewModel?.OpenAttachmentCommand.Execute(row);
    }
}
