using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vigo.App.Controls;
using Vigo.App.Services;
using Vigo.App.Views;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Vigo.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Dictionary<TabViewModel, UIElement> _views = [];
    private readonly ILogger<MainWindow> _logger;
    private readonly LayoutState _layout;
    private readonly JsonSessionStore _session;
    private readonly Preferences _preferences;
    private readonly WindowContext _context;
    private readonly WindowManager _windows;
    private readonly bool _isMain;
    private bool _firstFrameSeen;
    private bool _isMaximized;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// Presenter created and set by the window itself: under AOT, <c>AppWindow.Presenter</c> comes back as the base
    /// <c>AppWindowPresenter</c>, so an <c>is OverlappedPresenter</c> test fails silently.
    /// </summary>
    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();

    /// <param name="isMain">The window opened at launch: it restores the session and runs the startup probe.</param>
    public MainWindow(
        ShellViewModel viewModel, UserNotifier notifier, WindowContext context, LayoutState layout, JsonSessionStore session,
        Preferences preferences, WindowManager windows, ILogger<MainWindow> logger, WindowPlacement placement, bool isMain)
    {
        ViewModel = viewModel;
        Notifier = notifier;
        _layout = layout;
        _session = session;
        _preferences = preferences;
        _context = context;
        _windows = windows;
        _isMain = isMain;
        _logger = logger;
        // Theme and density before the first layout: nothing is drawn twice.
        Appearance.Current.Apply(preferences.Current);
        InitializeComponent();
        ApplyTheme();
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
        preferences.Changed += OnPreferencesChanged;
        Root.ActualThemeChanged += (_, _) => ViewModel.IsDarkThemeShown = Root.ActualTheme == ElementTheme.Dark;
        Root.Loaded += (_, _) => ViewModel.IsDarkThemeShown = Root.ActualTheme == ElementTheme.Dark;
        AddShortcuts();

        context.WindowId = AppWindow.Id;
        context.Dispatcher = DispatcherQueue;
        Root.Loaded += (_, _) => context.XamlRoot = Root.XamlRoot;

        // Taskbar and Alt+Tab icon (the exe icon is embedded by ApplicationIcon; this one covers every window).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Vigo.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        // AppWindow works in physical pixels: scale by the monitor DPI.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        // Minimum mockup width: 1180 px.
        _presenter.PreferredMinimumWidth = (int)(1180 * scale);
        _presenter.PreferredMinimumHeight = (int)(640 * scale);
        AppWindow.SetPresenter(_presenter);
        RestorePlacement(scale, placement);
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.StatusBar.PropertyChanged += OnStatusBarPropertyChanged;
        ViewModel.Tabs.CollectionChanged += OnTabsCollectionChanged;
        Tabs.LayoutUpdated += OnTabsLayoutUpdated;
        ViewModel.ActivationRequested += OnActivationRequested;
        ViewModel.CloseRequested += OnCloseRequested;
        ShowTab(ViewModel.SelectedTab);

        if (isMain)
            CompositionTarget.Rendering += OnFirstRendering;
        else
            StartAutoFetch();
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Stops the automatic fetch loop (called when the window closes).</summary>
    public void Dispose() => _closing.Dispose();

    /// <summary>Canceled when the window starts closing (a restoration in progress must stop adding tabs).</summary>
    public CancellationToken ClosingToken => _closing.Token;

    public UserNotifier Notifier { get; }

    /// <summary>Bounds of the window when neither maximized nor minimized (physical pixels).</summary>
    public WindowBounds? NormalBounds { get; private set; }

    /// <summary>Repository tabs and placement of the window, for the next launch.</summary>
    public SessionWindow CaptureSession() => ViewModel.CaptureSession() with { Bounds = NormalBounds, IsMaximized = _isMaximized };

    /// <summary>Automatic fetch of the visible tab, until the window closes.</summary>
    public void StartAutoFetch() => _ = ViewModel.RunAutoFetchAsync(_closing.Token);

    /// <summary>
    /// Saved bounds when they are still on a connected display (otherwise the default size, centered), then
    /// maximized if the window was, the saved bounds becoming its restore bounds. A window closed minimized comes
    /// back in the state it had before being minimized.
    /// </summary>
    private void RestorePlacement(double scale, WindowPlacement placement)
    {
        if (placement.Bounds is { } saved
            && DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height));
        }
        else
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)(1440 * scale), work.Width);
            var height = Math.Min((int)(900 * scale), work.Height);
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        }

        RecordNormalBounds();
        _isMaximized = placement.IsMaximized;
        if (placement.IsMaximized)
            _presenter.Maximize();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) return;
        switch (_presenter.State)
        {
            case OverlappedPresenterState.Maximized:
                _isMaximized = true;
                break;
            case OverlappedPresenterState.Restored:
                _isMaximized = false;
                RecordNormalBounds();
                break;
            // Minimized: the state from before is kept, and restored at the next launch.
        }
    }

    private void RecordNormalBounds() =>
        NormalBounds = new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    /// <summary>
    /// The window manager saves the session (last window, or "Quitter VIGO") or releases the tabs of this window (the
    /// application keeps running). The tabs are still there: they are captured on the UI thread before any teardown.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing.Cancel();
        _preferences.Changed -= OnPreferencesChanged;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        ViewModel.ActivationRequested -= OnActivationRequested;
        ViewModel.CloseRequested -= OnCloseRequested;
        ViewModel.StatusBar.PropertyChanged -= OnStatusBarPropertyChanged;
        Dispose();
        _windows.OnClosed(this);
    }

    /// <summary>One of the tabs of this window was asked for from another window: brings it to the front.</summary>
    private void OnActivationRequested(object? sender, EventArgs e)
    {
        if (_presenter.State == OverlappedPresenterState.Minimized) _presenter.Restore();
        Activate();
    }

    /// <summary>
    /// The last tab was moved to another window. Closing is deferred: this can happen in the middle of a tab drag.
    /// </summary>
    private void OnCloseRequested(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Close);

    // Tab drag and drop between windows (ADR-033): the tab bar stays client area (clicks, context menu and window
    // dragging behave normally). Reordering within a window is the TabView's own; a tab dropped on the tab strip of
    // another window joins it, a tab dropped elsewhere gets a new window under the pointer. The dragged tab is known
    // in-process (WindowManager.DraggedTab): the data package only carries a marker.

    private const string DraggedTabFormat = "VigoTab";

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Item is not TabViewModel tab) return;
        _windows.DraggedTab = tab;
        args.Data.Properties[DraggedTabFormat] = tab.Title;
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args) => _windows.DraggedTab = null;

    /// <summary>Accepts a tab of another VIGO window (one of this window is reordered by the TabView itself).</summary>
    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(DraggedTabFormat) || _windows.DraggedTab is not { } tab || ViewModel.Tabs.Contains(tab)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (_windows.DraggedTab is not { } tab || ViewModel.Tabs.Contains(tab) || _windows.FindShell(tab) is not { } source) return;
        _windows.DraggedTab = null;
        source.MoveTab(tab, ViewModel, DropIndex(e));
        Activate();
    }

    /// <summary>Index before the first tab whose middle is right of the pointer (the end otherwise).</summary>
    private int DropIndex(DragEventArgs e)
    {
        for (var i = 0; i < ViewModel.Tabs.Count; i++)
        {
            // Base type only: a container created by native XAML may not come back as TabViewItem under AOT.
            if (Tabs.ContainerFromIndex(i) is FrameworkElement container && e.GetPosition(container).X < container.ActualWidth / 2)
                return i;
        }
        return ViewModel.Tabs.Count;
    }

    /// <summary>Dropped outside every tab strip: the tab gets its own window under the pointer (if others are left).</summary>
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Item is not TabViewModel tab || ViewModel.Tabs.Count < 2 || !ViewModel.Tabs.Contains(tab)) return;
        GetCursorPos(out var cursor);
        var target = _windows.OpenWindow(withWelcomeTab: false, at: (cursor.X, cursor.Y));
        ViewModel.MoveTab(tab, target.ViewModel);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    // ---- Project colors (J7.6) ------------------------------------------------------------------------------------------
    // The tab edges are rectangles on an overlay canvas over the tab strip, placed from the tab containers' bounds: the
    // TabViewItem template overrides its border when the tab is selected, and reaching into the template needs type tests
    // that fail silently under AOT. The selected tab's color also frames the content area (UpdateProjectFrame).

    private readonly List<Border> _edgeRects = [];
    private readonly HashSet<TabViewModel> _tracked = [];

    private void OnTabItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) Track(tab);
        RefreshProjectColors();
    }

    private void OnTabItemDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is TabViewModel tab) Track(tab);
        RefreshProjectColors();
    }

    private void Track(TabViewModel tab)
    {
        if (_tracked.Add(tab)) tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ProjectColorIndex) or nameof(TabViewModel.ProjectColor)) RefreshProjectColors();
    }

    private void RefreshProjectColors()
    {
        UpdateTabEdges();
        UpdateProjectFrame();
    }

    /// <summary>
    /// Edges of the tabs that belong to a project: a top line following the rounded top corners of the tab; the
    /// selected tab gets a full outline (PlaceSelectedEdge), as the frame of the content goes up around it. Elements are
    /// reused; a value set again unchanged costs no layout.
    /// </summary>
    private void UpdateTabEdges()
    {
        var used = 0;
        var selected = false;
        for (var i = 0; i < ViewModel.Tabs.Count; i++)
        {
            var tab = ViewModel.Tabs[i];
            if (!tab.HasProject || Tabs.ContainerFromIndex(i) is not FrameworkElement { ActualWidth: > 0 } container) continue;
            var origin = container.TransformToVisual(TabEdges).TransformPoint(new Windows.Foundation.Point(0, 0));
            var brush = ProjectBrushes.For(tab.ProjectColorIndex, tab.ProjectColor);
            if (ReferenceEquals(tab, ViewModel.SelectedTab))
            {
                // The feet end on the middle of the top line of the frame, which starts where the tab strip ends.
                PlaceSelectedEdge(brush, origin.X, origin.Y, container.ActualWidth);
                selected = true;
            }
            else
            {
                PlaceEdge(used++, brush, origin.X, origin.Y, container.ActualWidth);
            }
        }

        for (var i = used; i < _edgeRects.Count; i++) _edgeRects[i].Visibility = Visibility.Collapsed;
        if (!selected && _selectedEdge is not null) _selectedEdge.Visibility = Visibility.Collapsed;
    }

    // Thickness of the colored lines (tab edges and frame), radius of the tab's top corners, and of the concave flares
    // joining the selected tab to the frame of the content.
    internal const double EdgeThickness = 2;
    private const double TabRadius = 8;
    internal const double TabFlare = 6;

    private static readonly CornerRadius TabCorners = new(TabRadius, TabRadius, 0, 0);

    private Microsoft.UI.Xaml.Shapes.Path? _selectedEdge;
    private (double X, double Y, double Width, double Line, double FrameX, double FrameWidth) _selectedEdgeBounds;

    private void PlaceEdge(int slot, Brush? brush, double x, double y, double width)
    {
        if (slot == _edgeRects.Count)
        {
            var created = new Border { IsHitTestVisible = false, CornerRadius = TabCorners };
            _edgeRects.Add(created);
            TabEdges.Children.Add(created);
        }

        var edge = _edgeRects[slot];
        edge.BorderBrush = brush;
        edge.BorderThickness = new Thickness(0, EdgeThickness, 0, 0);
        edge.Width = width;
        edge.Height = TabRadius + EdgeThickness;
        Canvas.SetLeft(edge, x);
        Canvas.SetTop(edge, y);
        edge.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The selected tab and the top line of the frame, as one stroke so nothing can be misaligned: from the left end of
    /// the frame, a concave flare up to the tab, over its rounded top, a flare back down, to the right end of the frame.
    /// Rebuilt only when the geometry changes.
    /// </summary>
    private void PlaceSelectedEdge(Brush? brush, double x, double y, double width)
    {
        if (_selectedEdge is null)
        {
            _selectedEdge = new Microsoft.UI.Xaml.Shapes.Path { IsHitTestVisible = false, StrokeThickness = EdgeThickness };
            TabEdges.Children.Add(_selectedEdge);
        }

        _selectedEdge.Stroke = brush;
        _selectedEdge.Visibility = Visibility.Visible;

        // Top line of the frame, in the coordinates of the overlay: the stroke runs along its middle.
        var half = EdgeThickness / 2;
        var frame = ProjectFrame.TransformToVisual(TabEdges).TransformPoint(new Windows.Foundation.Point(0, 0));
        var line = frame.Y + half;
        var bounds = (x, y, width, line, frame.X, ProjectFrame.ActualWidth);
        if (_selectedEdgeBounds == bounds) return;
        _selectedEdgeBounds = bounds;

        var left = x + half;
        var right = x + width - half;
        var top = y + half;
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(frame.X, line), IsClosed = false };
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x - TabFlare, line) });
        figure.Segments.Add(Arc(left, line - TabFlare, TabFlare, SweepDirection.Counterclockwise));
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(left, top + TabRadius) });
        figure.Segments.Add(Arc(left + TabRadius, top, TabRadius, SweepDirection.Clockwise));
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(right - TabRadius, top) });
        figure.Segments.Add(Arc(right, top + TabRadius, TabRadius, SweepDirection.Clockwise));
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(right, line - TabFlare) });
        figure.Segments.Add(Arc(x + width + TabFlare, line, TabFlare, SweepDirection.Counterclockwise));
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(frame.X + ProjectFrame.ActualWidth, line) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _selectedEdge.Data = geometry;
    }

    private static ArcSegment Arc(double x, double y, double radius, SweepDirection direction) => new()
    {
        Point = new Windows.Foundation.Point(x, y),
        Size = new Windows.Foundation.Size(radius, radius),
        SweepDirection = direction,
    };

    /// <summary>A tab got its place (or moved): edges and the gap of the frame follow.</summary>
    private void OnTabsLayoutUpdated(object? sender, object e) => RefreshProjectColors();

    private void OnProjectFrameSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProjectFrame();

    /// <summary>
    /// Frame of the content area in the color of the selected tab's project (none without project): its sides and
    /// bottom here, its top line being part of the selected tab's outline (PlaceSelectedEdge).
    /// </summary>
    private void UpdateProjectFrame()
    {
        var index = ViewModel.SelectedTab?.ProjectColorIndex ?? -1;
        ProjectFrameSides.BorderBrush = ProjectBrushes.For(index, ViewModel.SelectedTab?.ProjectColor);
        ProjectFrameSides.BorderThickness = index >= 0 ? new Thickness(EdgeThickness, 0, EdgeThickness, EdgeThickness) : new Thickness(0);
    }

    /// <summary>Tooltip of a tab: its title, then its project.</summary>
    public static string TabTooltip(string title, string? project) => project is null ? title : Loc.Format("Shell.Tab.Tooltip", title, project);

    /// <summary>
    /// Tab context menu, built each time it opens: "Déplacer vers une nouvelle fenêtre" (only when the window has another
    /// tab), the project entries of a repository tab, "Fermer l'onglet". The tab is the data context of the menu's target.
    /// </summary>
    private void OnTabContextMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        var tab = (menu.Target as FrameworkElement)?.DataContext as TabViewModel;
        menu.Items.Clear();

        var move = new MenuFlyoutItem { Text = Loc.Get("Shell.TabMenu.MoveToWindow"), IsEnabled = ViewModel.Tabs.Count > 1 };
        move.Click += (_, _) => ViewModel.MoveTabToNewWindowCommand.Execute(tab);
        menu.Items.Add(move);

        if (tab is RepositoryTabViewModel repoTab)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var icon = new MenuFlyoutItem { Text = Loc.Get("Shell.TabMenu.ChangeIcon") };
            icon.Click += async (_, _) => await ChooseTabIconAsync(repoTab);
            menu.Items.Add(icon);
        }

        if (tab is RepositoryTabViewModel && ViewModel.HasProjects)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var current = ViewModel.ProjectOf(tab);
            var assign = new MenuFlyoutSubItem { Text = Loc.Get("Shell.TabMenu.AssignProject") };
            foreach (var project in ViewModel.Projects)
            {
                var id = project.Id;
                var item = new ToggleMenuFlyoutItem { Text = project.Name, IsChecked = project.Id == current?.Id };
                item.Click += (_, _) => ViewModel.AssignToProject(tab, id);
                assign.Items.Add(item);
            }
            if (ViewModel.Projects.Count > 0) assign.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = Loc.Get("Shell.TabMenu.NewProject") };
            create.Click += async (_, _) => await ViewModel.NewProjectAsync(tab, _closing.Token);
            assign.Items.Add(create);
            menu.Items.Add(assign);

            if (current is not null)
            {
                var remove = new MenuFlyoutItem { Text = Loc.Format("Shell.TabMenu.RemoveProject", current.Name) };
                remove.Click += (_, _) => ViewModel.RemoveFromProject(tab);
                menu.Items.Add(remove);
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var close = new MenuFlyoutItem { Text = Loc.Get("Shell.TabMenu.CloseTab"), KeyboardAcceleratorTextOverride = "Ctrl+W" };
        close.Click += (_, _) => ViewModel.CloseTabCommand.Execute(tab);
        menu.Items.Add(close);
    }

    /// <summary>The "+" button belongs to the TabView template: its tooltip gets the shortcut.</summary>
    private void OnTabsLoaded(object sender, RoutedEventArgs e)
    {
        if (FindDescendant<Button>(Tabs, "AddButton") is not { } add) return;
        ToolTipService.SetToolTip(add, ShortcutMap.Current.WithHint(Loc.Get("Shell.NewTab"), ShortcutIds.NewTab));
        AutomationProperties.SetName(add, Loc.Get("Shell.NewTab"));
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name) return element;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnFirstRendering(object? sender, object e)
    {
        if (_firstFrameSeen) return;
        _firstFrameSeen = true;
        CompositionTarget.Rendering -= OnFirstRendering;

        var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
        LogFirstFrame(_logger, elapsed.TotalMilliseconds);

        var options = Program.Options;
        if (options.StartupProbeFile is { } probe)
        {
            File.WriteAllText(probe, elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
            Close();
            return;
        }
        OpenAtStartup(options.OpenPath);
    }

    /// <summary>
    /// After the first frame: reopens the repositories of the previous session (the other windows of the session at
    /// their place), then the --open one, in this window (which gets the focus). Fire and forget, but a failure (e.g.
    /// while building a view) must reach the log.
    /// </summary>
    private async void OpenAtStartup(string? path)
    {
        try
        {
            var profile = _windows.SessionProfileId;
            var session = await Task.Run(() => _session.LoadAsync(profile));
            _windows.Workspace.Projects.Load(session.Projects);
            await _windows.RestoreAsync(async () =>
            {
                if (session.Windows is [var main, ..])
                    await ViewModel.RestoreSessionAsync(main, _closing.Token);
                foreach (var saved in session.Windows.Skip(1))
                {
                    var window = _windows.RestoreWindow(saved);
                    await window.ViewModel.RestoreSessionAsync(saved, window.ClosingToken);
                }
            });
            if (path is not null)
            {
                await ViewModel.OpenRepositoryAsync(path, _closing.Token);
                if (session.Windows.Count > 1) Activate();
            }
            // Automatic fetch of the visible tab, once the session is back (not for each restored tab).
            StartAutoFetch();
            await ShowWhatsNewAsync();
        }
        catch (Exception ex)
        {
            // In an async void, an exception escaping here would take the whole application down.
            LogStartupOpenFailed(_logger, ex, path ?? "(session)");
        }
    }

    /// <summary>First launch of a new version: the release notes of every version since the last one seen, once (recorded just before showing, so a crash never loops).</summary>
    private async Task ShowWhatsNewAsync()
    {
        var version = AppVersion.Current;
        if (_preferences.Current.LastSeenVersion == version) return;
        if (Changelog.NotesSince(Changelog.Text, version, _preferences.Current.LastSeenVersion) is not { Length: > 0 } notes)
        {
            _ = _preferences.UpdateAsync(p => p with { LastSeenVersion = version });
            return;
        }
        // Another dialog is open (e.g. authentication of a restored tab): the notes wait for the next launch.
        if (_context.IsDialogOpen || _context.XamlRoot is not { } root) return;
        _ = _preferences.UpdateAsync(p => p with { LastSeenVersion = version });
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = Appearance.Current.Theme,
            Title = Loc.Format("WhatsNew.Title", version),
            Content = new ScrollViewer { MaxHeight = 420, Content = new MarkdownView { Blocks = Vigo.Core.Markdown.MarkdownParser.Parse(notes) } },
            CloseButtonText = Loc.Get("WhatsNew.Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        _context.IsDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _context.IsDialogOpen = false;
        }
    }

    /// <summary>Dialog to pick the icon of a repository tab among all the Fluent icons; the secondary button restores the folder.</summary>
    private async Task ChooseTabIconAsync(RepositoryTabViewModel tab)
    {
        if (_context.IsDialogOpen || _context.XamlRoot is not { } root) return;
        _context.IsDialogOpen = true;
        try
        {
            if (await TabIconPicker.ShowDialogAsync(root, Loc.Get("TabIcon.Title")) is { } pick) ViewModel.SetTabIcon(tab, pick.Name);
        }
        finally
        {
            _context.IsDialogOpen = false;
        }
    }

    private Storyboard? _checkSpin;

    // The icon of the "check for updates" button turns for as long as a check (and its download) runs.
    private void OnStatusBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatusBarViewModel.IsCheckingForUpdates)) return;
        if (ViewModel.StatusBar.IsCheckingForUpdates)
        {
            var spin = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(spin, CheckUpdatesRotation);
            Storyboard.SetTargetProperty(spin, "Angle");
            _checkSpin = new Storyboard { Children = { spin } };
            _checkSpin.Begin();
        }
        else
        {
            _checkSpin?.Stop();
            _checkSpin = null;
            CheckUpdatesRotation.Angle = 0;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab))
            ShowTab(ViewModel.SelectedTab);
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A move (tabs grouped by project) keeps the views.
        if (e.OldItems is null || e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (TabViewModel old in e.OldItems)
        {
            if (_tracked.Remove(old)) old.PropertyChanged -= OnTabPropertyChanged;
            if (_views.Remove(old, out var view) && view is RepositoryView repositoryView)
                repositoryView.Release();
        }
    }

    private void ShowTab(TabViewModel? tab)
    {
        if (!ReferenceEquals(Tabs.SelectedItem, tab))
            Tabs.SelectedItem = tab;

        if (tab is not null) Track(tab);
        RefreshProjectColors();

        // The content follows the selection once the burst of changes of this tick (session restore selects a tab,
        // opens the next one, then selects the first again) is over: see ShowSelectedTab.
        if (_showTabQueued) return;
        _showTabQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowSelectedTab);
    }

    private bool _showTabQueued;

    /// <summary>
    /// Puts the view of the selected tab in the content host. A view removed and added back within one tick receives
    /// <c>Unloaded</c> after it is back in the tree and no <c>Loaded</c> after that: Win2D stops drawing its control
    /// (the graph stayed empty), and the views that unsubscribe on <c>Unloaded</c> stay unsubscribed. So a view that
    /// is already the content stays where it is.
    /// </summary>
    private void ShowSelectedTab()
    {
        _showTabQueued = false;
        var tab = ViewModel.SelectedTab;
        if (tab is null)
        {
            ContentHost.Children.Clear();
            return;
        }

        if (!_views.TryGetValue(tab, out var view))
        {
            view = tab switch
            {
                WelcomeTabViewModel w => new WelcomeView(w),
                RepositoryTabViewModel r => new RepositoryView(r, _layout, ViewModel, OpenPalette),
                PreferencesTabViewModel p => new PreferencesView(p),
                _ => new TextBlock { Text = tab.Title },
            };
            _views[tab] = view;
        }

        if (ContentHost.Children is [var current] && ReferenceEquals(current, view)) return;
        ContentHost.Children.Clear();
        ContentHost.Children.Add(view);
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewModel tab && !ReferenceEquals(ViewModel.SelectedTab, tab))
            ViewModel.SelectedTab = tab;
    }

    private void OnAddTabButtonClick(TabView sender, object args) => ViewModel.NewTabCommand.Execute(null);

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        ViewModel.CloseTabCommand.Execute(args.Item as TabViewModel);

    /// <summary>Shortcuts of the window (KeyboardShortcuts: also reached from the code editor, ADR-038).</summary>
    private void AddShortcuts()
    {
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.NewTab, () => ViewModel.NewTabCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.CloseTab, () => ViewModel.CloseTabCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.OpenRepository, () => ViewModel.OpenCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.CloneRepository, () => ViewModel.CloneCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.InitRepository, () => ViewModel.InitCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.Palette, () => OpenPalette(string.Empty));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.NewWindow, () => ViewModel.NewWindowCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.Quit, () => ViewModel.QuitCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.Preferences, () => ViewModel.OpenPreferencesCommand.Execute(null));
        KeyboardShortcuts.AddCommand(Root, ShortcutIds.OnlineHelp, () => ViewModel.OpenOnlineHelpCommand.Execute(null));
    }

    /// <summary>
    /// Icon of a tab, a new source each time: a TabViewItem reused for another tab (one closed or replaced) keeps the
    /// icon element its source created first, so changing the glyph of that source would not show.
    /// </summary>
    public static FontIconSource TabIcon(string name) => new()
    {
        FontFamily = new FontFamily("ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"),
        FontSize = 14,
        Glyph = FluentIcons.FromName(name),
    };

    /// <summary>Command palette over the window (Ctrl+P, toolbar button); not while a dialog is shown.</summary>
    private void OpenPalette(string query)
    {
        if (_context.IsDialogOpen) return;
        Palette.Open(ViewModel.CreatePalette(query));
    }

    // Status bar entries.
    private void OnStatusBranchClick(object sender, RoutedEventArgs e) => OpenPalette("@");

    private void OnStatusSyncClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.StatusBar.Repository?.Remote.FetchCommand is { } fetch && fetch.CanExecute(null)) _ = fetch.ExecuteAsync(null);
    }

    /// <summary>
    /// Profile menu: one entry per profile (the active one checked), then the preferences. Built when opened, so it
    /// follows the profiles; without profiles, the button opens the preferences as before.
    /// </summary>
    private void OnStatusProfileClick(object sender, RoutedEventArgs e)
    {
        var choices = ViewModel.ProfileChoices;
        if (choices.Count == 0 || sender is not FrameworkElement anchor)
        {
            ViewModel.OpenPreferencesCommand.Execute(null);
            return;
        }

        var menu = new MenuFlyout();
        foreach (var (id, label, isActive, icon) in choices)
        {
            menu.Items.Add(ProfileMenuItems.Create(label, isActive, icon, () =>
            {
                if (!isActive) _ = ViewModel.SwitchProfileAsync(id);
            }));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = Loc.Get("Shell.ProfileMenu.Manage") };
        manage.Click += (_, _) => ViewModel.OpenProfiles();
        menu.Items.Add(manage);
        menu.ShowAt(anchor);
    }

    private void OnStatusPaletteClick(object sender, RoutedEventArgs e) => OpenPalette(string.Empty);

    private void OnPreferencesChanged(object? sender, EventArgs e) => Appearance.Current.Apply(_preferences.Current);

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Appearance.Theme)) ApplyTheme();
    }

    /// <summary>
    /// Theme of the window content and of the caption buttons (drawn by Windows). The dialogs take it when they open,
    /// the Win2D controls redraw on <c>ActualThemeChanged</c>.
    /// </summary>
    private void ApplyTheme()
    {
        var theme = Appearance.Current.Theme;
        Root.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void OnNotificationClosed(InfoBar sender, object args)
    {
        if (sender.DataContext is Notification n) Notifier.Items.Remove(n);
    }

    /// <summary>
    /// Starts the countdown of a toast that closes by itself: its bottom bar shrinks to the left (render transform,
    /// animated by the compositor), and the toast closes when it is empty.
    /// </summary>
    private void OnNotificationCountdownLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Notification { AutoCloses: true, Countdown: null } notification } bar) return;

        // Transform created here rather than read back from XAML (typed casts of XAML-created objects are fragile under AOT).
        var scale = new ScaleTransform();
        bar.RenderTransform = scale;
        var shrink = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(Notification.Lifetime) };
        Storyboard.SetTarget(shrink, scale);
        Storyboard.SetTargetProperty(shrink, "ScaleX");
        var countdown = new Storyboard();
        countdown.Children.Add(shrink);
        countdown.Completed += (_, _) => Notifier.Items.Remove(notification);
        notification.Countdown = countdown;
        countdown.Begin();
    }

    private void OnNotificationCountdownUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Notification { Countdown: { } countdown } }) countdown.Stop();
    }

    private void OnNotificationPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Notification { Countdown: { } countdown } }) countdown.Pause();
    }

    private void OnNotificationPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Notification { Countdown: { } countdown } }) countdown.Resume();
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not open {Path} at startup")]
    private static partial void LogStartupOpenFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the layout and session at exit timed out")]
    private static partial void LogLayoutSaveTimedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "First frame rendered in {Milliseconds:F0} ms")]
    private static partial void LogFirstFrame(ILogger logger, double milliseconds);
}
