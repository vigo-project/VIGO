using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Markdown;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Text;

namespace Vigo.App.Controls;

/// <summary>
/// Draws a Markdown document (J6.9a) as native XAML: paragraphs and headings in selectable <see cref="RichTextBlock"/>s, code
/// blocks, quotes, lists, tables. Colors that the default text style does not give come from <see cref="DesignTokens"/> (a brush
/// read from the resources cannot be cast under AOT). Links open in the browser (http, https, mailto only); http(s) images are
/// downloaded and shown inline (a link when they cannot be read).
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    private const double MaxImageWidth = 640;
    private const double MaxImageHeight = 480;
    private const int MaxCachedImages = 64;

    // Pictures already downloaded, kept so a redraw (theme change, refresh) does not fetch them again. UI thread only.
    private static readonly Dictionary<string, byte[]> ImageCache = [];

    // Bumped when the cache is cleared: a download started with the previous account must not fill the cache afterwards.
    private static int _cacheGeneration;

    /// <summary>
    /// Forgets the pictures kept: one loaded with the token of an account must not stay visible once another profile is active
    /// or the account signed out. Called on the UI thread, when the accounts or the profiles change.
    /// </summary>
    public static void ClearImageCache()
    {
        ImageCache.Clear();
        _cacheGeneration++;
    }

    /// <summary>
    /// Downloads a picture with the signed-in account's token (null: not possible, the plain address is tried). Set once at start-up
    /// by the application; the view has no access to the accounts.
    /// </summary>
    public static Func<string, Task<byte[]?>>? ImageDownloader { get; set; }

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas");

    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks), typeof(object), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    private readonly StackPanel _root = new() { Spacing = 8 };

    public MarkdownView()
    {
        IsTabStop = false;
        Content = _root;
        ActualThemeChanged += (_, _) => Render();
    }

    /// <summary>The document to draw: an <c>IReadOnlyList&lt;MarkdownBlock&gt;</c> (typed as object: a WinRT dependency property).</summary>
    public object? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    private bool IsLight => ActualTheme == ElementTheme.Light;

    private Color Muted => IsLight ? DesignTokens.Light.Text3 : DesignTokens.Dark.Text3;

    private Color Subtle => IsLight ? DesignTokens.Light.Layer2 : DesignTokens.Dark.Layer2;

    private Color StrokeColor => IsLight ? DesignTokens.Light.StrokeStrong : DesignTokens.Dark.StrokeStrong;

    private Color LineColor => IsLight ? DesignTokens.Light.Stroke : DesignTokens.Dark.Stroke;

    private void Render()
    {
        _root.Children.Clear();
        if (Blocks is IReadOnlyList<MarkdownBlock> blocks)
        {
            foreach (var block in blocks) _root.Children.Add(Build(block));
        }
    }

    private UIElement Build(MarkdownBlock block) => block switch
    {
        MarkdownParagraph p => Text(p.Content, 13),
        MarkdownHeading h => Text(h.Content, h.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 13 }, FontWeights.SemiBold),
        MarkdownCodeBlock c => BuildCode(c),
        MarkdownQuote q => BuildQuote(q),
        MarkdownList l => BuildList(l),
        MarkdownTable t => BuildTable(t),
        MarkdownRule => new Border { Height = 1, Background = new SolidColorBrush(LineColor), Margin = new Thickness(0, 4, 0, 4) },
        _ => new Border(),
    };

    private RichTextBlock Text(IReadOnlyList<MarkdownInline> inlines, double size, FontWeight? weight = null)
    {
        var paragraph = new Paragraph();
        AddInlines(paragraph.Inlines, inlines, new InlineStyle(weight ?? FontWeights.Normal, false, false, false));
        var block = new RichTextBlock { FontSize = Appearance.Scaled(size), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        block.Blocks.Add(paragraph);
        return block;
    }

    // Emphasis nests: the flags travel down to the runs, which carry them.
    private readonly record struct InlineStyle(FontWeight Weight, bool Italic, bool Strike, bool Mono);

    private void AddInlines(InlineCollection target, IEnumerable<MarkdownInline> inlines, InlineStyle style)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownText t:
                    target.Add(MakeRun(t.Text, style));
                    break;
                case MarkdownCode c:
                    target.Add(MakeRun(c.Text, style with { Mono = true }, Muted));
                    break;
                case MarkdownEmphasis e:
                    AddInlines(target, e.Content, e.Kind switch
                    {
                        MarkdownEmphasisKind.Bold => style with { Weight = FontWeights.SemiBold },
                        MarkdownEmphasisKind.Strikethrough => style with { Strike = true },
                        _ => style with { Italic = true },
                    });
                    break;
                case MarkdownLink l:
                    AddLink(target, l.Url, l.Content, style);
                    break;
                case MarkdownImage i:
                    AddImage(target, i, style);
                    break;
                case MarkdownBreak b:
                    if (b.IsHard) target.Add(new LineBreak());
                    else target.Add(MakeRun(" ", style));
                    break;
                case MarkdownTask task:
                    target.Add(MakeRun(task.IsChecked ? "☑ " : "☐ ", style));
                    break;
            }
        }
    }

    // The picture is downloaded by the Image (http / https only); when it cannot be, or the address is not one, a link to it is shown.
    private void AddImage(InlineCollection target, MarkdownImage image, InlineStyle style)
    {
        MarkdownInline[] fallback = [new MarkdownText(image.Alt.Length > 0 ? $"[image : {image.Alt}]" : "[image]")];
        if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            AddLink(target, image.Url, fallback, style);
            return;
        }

        var picture = new Image
        {
            MaxWidth = MaxImageWidth, MaxHeight = MaxImageHeight, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (image.Alt.Length > 0)
        {
            ToolTipService.SetToolTip(picture, image.Alt);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picture, image.Alt);
        }

        var container = new InlineUIContainer { Child = picture };
        target.Add(container);
        _ = LoadImageAsync(picture, container, uri, fallback[0] is MarkdownText t ? t.Text : "[image]");
    }

    // The account's token first (pictures of a private repository answer 404 without it), then the plain address (https only).
    private static async Task LoadImageAsync(Image picture, InlineUIContainer container, Uri uri, string linkText)
    {
        BitmapImage? bitmap = null;
        try
        {
            var generation = _cacheGeneration;
            if (ImageCache.TryGetValue(uri.AbsoluteUri, out var cached)) bitmap = await DecodeAsync(cached);
            else if (ImageDownloader is { } download && await download(uri.AbsoluteUri) is { } bytes)
            {
                bitmap = await DecodeAsync(bytes);
                if (bitmap is not null && generation == _cacheGeneration && ImageCache.Count < MaxCachedImages) ImageCache[uri.AbsoluteUri] = bytes;
            }

            if (bitmap is null)
            {
                // Never in clear text: the author of the page would learn the reader's address and the time of reading.
                if (uri.Scheme != Uri.UriSchemeHttps)
                {
                    ShowLink(container, uri, linkText);
                    return;
                }
                var direct = new BitmapImage(uri);
                direct.ImageFailed += (_, _) => ShowLink(container, uri, linkText);
                picture.Source = direct;
                return;
            }

            picture.Source = bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowLink(container, uri, linkText);
        }
    }

    // Not readable (no access, gone, not a picture): back to a link the browser can open.
    private static void ShowLink(InlineUIContainer container, Uri uri, string text) =>
        container.Child = new HyperlinkButton { Content = text, NavigateUri = uri, Padding = new Thickness(0) };

    private static async Task<BitmapImage?> DecodeAsync(byte[] bytes)
    {
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
            return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? bitmap : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsupported or damaged data: the codecs report it with a COMException.
            return null;
        }
    }

    private void AddLink(InlineCollection target, string url, IReadOnlyList<MarkdownInline> content, InlineStyle style)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            var link = new Hyperlink { NavigateUri = uri };
            AddInlines(link.Inlines, content, style);
            target.Add(link);
        }
        else
        {
            // Relative links (issues, files of the repository) have no page to go to from here: plain text.
            AddInlines(target, content, style);
        }
    }

    private static Run MakeRun(string text, InlineStyle style, Color? color = null)
    {
        var run = new Run { Text = text, FontWeight = style.Weight };
        if (style.Italic) run.FontStyle = FontStyle.Italic;
        if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
        if (style.Mono) run.FontFamily = MonoFont;
        if (color is { } c) run.Foreground = new SolidColorBrush(c);
        return run;
    }

    private Border BuildCode(MarkdownCodeBlock code)
    {
        var text = new TextBlock
        {
            Text = code.Text, FontFamily = MonoFont, FontSize = Appearance.Scaled(12), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap,
        };
        return new Border
        {
            Background = new SolidColorBrush(Subtle),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                Content = text,
            },
        };
    }

    private Border BuildQuote(MarkdownQuote quote)
    {
        var inner = new StackPanel { Spacing = 6 };
        foreach (var block in quote.Blocks) inner.Children.Add(Build(block));
        return new Border
        {
            BorderBrush = new SolidColorBrush(StrokeColor),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Opacity = 0.9,
            Child = inner,
        };
    }

    private StackPanel BuildList(MarkdownList list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•", FontSize = Appearance.Scaled(13), HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(Muted),
            };
            var content = new StackPanel { Spacing = 4 };
            foreach (var block in item) content.Children.Add(Build(block));
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private ScrollViewer BuildTable(MarkdownTable table)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid { BorderBrush = new SolidColorBrush(LineColor), BorderThickness = new Thickness(1, 1, 0, 0) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rowIndex = 0;
        if (table.Header.Count > 0) AddRow(table.Header, bold: true);
        foreach (var row in table.Rows) AddRow(row, bold: false);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = grid,
        };

        void AddRow(IReadOnlyList<IReadOnlyList<MarkdownInline>> cells, bool bold)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(LineColor),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = bold ? new SolidColorBrush(Subtle) : null,
                    Child = Text(cells[c], 12, bold ? FontWeights.SemiBold : null),
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            rowIndex++;
        }
    }
}
