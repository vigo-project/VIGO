using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Views;

/// <summary>
/// "Git" tab of the terminal panel (see <see cref="GitCommandLogViewModel"/>): the list follows its end, as long as the
/// user has not scrolled up, whenever its content grows (a new command, the output of one that ends).
/// </summary>
public sealed partial class GitCommandLogView : UserControl
{
    /// <summary>Distance from the end (DIPs) under which the list counts as scrolled to its end.</summary>
    private const double FollowThreshold = 24;

    private bool _following = true;
    private bool _resizing;

    public GitCommandLogView() => InitializeComponent();

    /// <summary>Set once by the terminal panel, right after its own view model.</summary>
    public GitCommandLogViewModel ViewModel
    {
        get;
        set
        {
            field = value;
            // The x:Bind bindings were evaluated with a null view model during InitializeComponent.
            Bindings.Update();
        }
    } = null!;

    /// <summary>The offset only changes when the user scrolls (or by <see cref="ScrollToEnd"/>): it tells whether to follow.</summary>
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // A view change raised while the content is being resized is the content growing, not the user: the distance
        // to the end is then temporarily large, and must not stop the following.
        if (_resizing) return;
        _following = Scroller.ScrollableHeight - Scroller.VerticalOffset <= FollowThreshold;
    }

    /// <summary>Content grew or shrank (output added, a new command): stay at the end when following.</summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_resizing) return;
        _resizing = true;
        // Once the layout is done: during the size change the scroll viewer still has its previous extent, and the
        // offset would stop short of the end.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_following)
            {
                Scroller.UpdateLayout();
                Scroller.ChangeView(null, Scroller.ExtentHeight, null, disableAnimation: true);
            }
            _resizing = false;
        });
    }
}
