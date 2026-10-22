using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Vigo.App.Controls;

/// <summary>
/// Image diff (J5.7, ADR-043): decodes both versions with the Windows imaging codecs (<see cref="BitmapImage"/>) and
/// shows them side by side, overlaid with an adjustable opacity, or on either side of a movable divider.
/// </summary>
public sealed partial class ImageDiffView : UserControl
{
    private int _generation;
    private bool _updatingMode;
    private ImageCompareMode _mode;
    private Size _oldSize, _newSize;

    public ImageDiffView()
    {
        InitializeComponent();
        // Not in the XAML: the string "True" cannot be assigned to ToggleButton.IsChecked under Native AOT.
        _updatingMode = true;
        ((RadioButton)ModeButtons.Children[0]).IsChecked = true;
        _updatingMode = false;
    }

    /// <summary>Raised when the user picks another comparison mode.</summary>
    public event EventHandler<ImageCompareMode>? ModeChanged;

    /// <summary>Comparison mode shown; setting it does not raise <see cref="ModeChanged"/>.</summary>
    public ImageCompareMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _updatingMode = true;
            ((RadioButton)ModeButtons.Children[(int)value]).IsChecked = true;
            _updatingMode = false;
            ApplyMode();
        }
    }

    /// <summary>Shows both versions of an image; null clears the view.</summary>
    public async Task ShowAsync(DiffImages? images)
    {
        var generation = ++_generation;
        if (images is null)
        {
            Clear();
            return;
        }

        var (oldImage, oldFailed) = await DecodeAsync(images.Old);
        var (newImage, newFailed) = await DecodeAsync(images.New);
        if (generation != _generation) return;

        OldImageSide.Source = OldImage.Source = oldImage;
        NewImageSide.Source = NewImage.Source = newImage;
        _oldSize = SizeOf(oldImage);
        _newSize = SizeOf(newImage);
        OldImage.Width = _oldSize.Width;
        OldImage.Height = _oldSize.Height;
        NewImage.Width = _newSize.Width;
        NewImage.Height = _newSize.Height;
        Layers.Width = Math.Max(_oldSize.Width, _newSize.Width);
        Layers.Height = Math.Max(_oldSize.Height, _newSize.Height);

        var oldText = Describe(Loc.Get("ImageDiff.Before"), images.Old, oldImage, oldFailed, Loc.Get("ImageDiff.FileAdded"));
        var newText = Describe(Loc.Get("ImageDiff.After"), images.New, newImage, newFailed, Loc.Get("ImageDiff.FileRemoved"));
        OldCaption.Text = oldText;
        NewCaption.Text = newText;
        Legend.Text = $"{oldText}   ·   {newText}";
        OldMissing.Text = MissingText(images.Old, oldFailed, Loc.Get("ImageDiff.Missing.Old"));
        NewMissing.Text = MissingText(images.New, newFailed, Loc.Get("ImageDiff.Missing.New"));
        Message.Visibility = Visibility.Collapsed;
        ApplyMode();
    }

    private void Clear()
    {
        OldImageSide.Source = OldImage.Source = null;
        NewImageSide.Source = NewImage.Source = null;
        OldCaption.Text = NewCaption.Text = Legend.Text = OldMissing.Text = NewMissing.Text = string.Empty;
        _oldSize = _newSize = default;
    }

    private static async Task<(BitmapImage? Image, bool Failed)> DecodeAsync(byte[]? bytes)
    {
        if (bytes is null) return (null, false);
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? bitmap : null, bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsupported or damaged data: the codecs report it with a COMException.
            return (null, true);
        }
    }

    private static Size SizeOf(BitmapImage? image) => image is null ? default : new Size(image.PixelWidth, image.PixelHeight);

    private static string Describe(string label, byte[]? bytes, BitmapImage? image, bool failed, string absent) =>
        bytes is null ? Loc.Format("ImageDiff.Caption.Absent", label, absent)
        : image is null || failed ? Loc.Format("ImageDiff.Caption.Unreadable", label, ByteSize.Format(bytes.Length))
        : Loc.Format("ImageDiff.Caption.Info", label, image.PixelWidth, image.PixelHeight, ByteSize.Format(bytes.Length));

    private static string MissingText(byte[]? bytes, bool failed, string absent) =>
        bytes is null ? absent : failed ? Loc.Get("ImageDiff.Missing.Unreadable") : string.Empty;

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingMode || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        _mode = (ImageCompareMode)index;
        ApplyMode();
        ModeChanged?.Invoke(this, _mode);
    }

    private void OnAdjustChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyAdjustment();

    private void ApplyMode()
    {
        var sideBySide = _mode == ImageCompareMode.SideBySide;
        SideBySidePanel.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
        CompositePanel.Visibility = sideBySide ? Visibility.Collapsed : Visibility.Visible;
        Legend.Visibility = sideBySide ? Visibility.Collapsed : Visibility.Visible;
        AdjustSlider.Visibility = AdjustLabel.Visibility = sideBySide ? Visibility.Collapsed : Visibility.Visible;
        AdjustLabel.Text = Loc.Get(_mode == ImageCompareMode.Overlay ? "ImageDiff.Adjust.Opacity" : "ImageDiff.Adjust.Position");
        AutomationProperties.SetName(AdjustSlider, AdjustLabel.Text);
        ApplyAdjustment();
    }

    private void ApplyAdjustment()
    {
        var fraction = AdjustSlider.Value / 100.0;
        switch (_mode)
        {
            case ImageCompareMode.Overlay:
                NewImage.Opacity = fraction;
                NewImage.Clip = null;
                Divider.Visibility = Visibility.Collapsed;
                break;
            case ImageCompareMode.Slider:
                var width = Layers.Width * fraction;
                NewImage.Opacity = 1;
                NewImage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Min(width, _newSize.Width), Math.Max(_newSize.Height, 1)) };
                Divider.Margin = new Thickness(Math.Max(width - 1, 0), 0, 0, 0);
                Divider.Visibility = Layers.Width > 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }
}
