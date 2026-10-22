using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Vigo.App.Controls;

/// <summary>
/// Thin grip that resizes a panel or a column: a transparent strip with the west-east cursor, laid over the border it
/// moves (or, <see cref="Orientation"/> horizontal, a strip with the north-south cursor that resizes a height). Reports
/// the distance dragged since the press (the owner keeps the width at the press,
/// so the clamped width follows the pointer back exactly); a double click asks for the default width.
/// </summary>
public sealed partial class Splitter : Grid
{
    private uint? _pointerId;
    private double _start;

    public Splitter()
    {
        // A null background is not hit-testable.
        Background = new SolidColorBrush(Colors.Transparent);
        Orientation = Orientation.Vertical;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, e) =>
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Vertical (default): a 6 px strip that resizes a width. Horizontal: a strip that resizes a height (its height is
    /// left to the owner, which may show a visible grip in it).
    /// </summary>
    public Orientation Orientation
    {
        get;
        set
        {
            field = value;
            Width = value == Orientation.Vertical ? 6 : double.NaN;
            ProtectedCursor = InputSystemCursor.Create(
                value == Orientation.Vertical ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        }
    }

    /// <summary>A drag starts: the owner records the current width.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Distance from the press, in device-independent pixels (positive to the right, or downwards).</summary>
    public event EventHandler<double>? DragDelta;

    /// <summary>The drag ended: the owner saves the layout.</summary>
    public event EventHandler? DragCompleted;

    public event EventHandler? ResetRequested;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer)) return;
        _pointerId = e.Pointer.PointerId;
        // Window coordinates: the splitter itself moves while the column it belongs to is resized.
        _start = Position(e);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        DragDelta?.Invoke(this, Position(e) - _start);
        e.Handled = true;
    }

    private double Position(PointerRoutedEventArgs e) =>
        e.GetCurrentPoint(null).Position is var p && Orientation == Orientation.Vertical ? p.X : p.Y;

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_pointerId is null) return;
        _pointerId = null;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
