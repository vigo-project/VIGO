using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Windows.UI.Core;

namespace Vigo.App.Controls;

/// <summary>
/// What a box of the native review keeps across layouts (the box itself is created again with its card): the text, the caret,
/// the preview switch, the write it waits for.
/// </summary>
public sealed class ReviewBoxState
{
    public string Text { get; set; } = string.Empty;
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public bool Preview { get; set; } = true;

    /// <summary>Key of the write being done (0: none); the box is locked meanwhile.</summary>
    public int BusyKey { get; set; }
}

/// <summary>Markdown input of the native review (see the XAML): the page's <c>markdownBox</c> in XAML.</summary>
public sealed partial class ReviewMarkdownBox : UserControl
{
    // The text box gives its line breaks as \r: inserted text uses the same, so that the caret positions stay right.
    private const string NewLine = "\r";
    private const int MaxFocusTries = 5;

    private readonly ReviewBoxState _state;
    private readonly string? _suggestion;
    private readonly bool _canComplete;
    private readonly MarkdownReferences? _references;
    private readonly ObservableCollection<ReferenceSuggestion> _proposals = [];
    private readonly DispatcherQueueTimer _referenceTimer;
    private (int Start, int End, ReferenceKind Kind, string Query)? _target;
    private int _focusTries;

    /// <param name="state">Kept by the editor: the box reads and updates it.</param>
    /// <param name="secondary">Second action (add to the review), or null.</param>
    /// <param name="suggestion">The commented code of the new version: the suggestion button inserts it; null hides the button.</param>
    /// <param name="references">What the references of the preview link to (see <see cref="MarkdownParser.Parse"/>).</param>
    public ReviewMarkdownBox(
        ReviewBoxState state, string placeholder, string primary, string? secondary, string? suggestion, bool canComplete, MarkdownReferences? references)
    {
        InitializeComponent();
        _state = state;
        _suggestion = suggestion;
        _canComplete = canComplete;
        _references = references;
        var labels = EditorReviewLabels.Current;
        Tip(HeadingButton, labels.Heading);
        Tip(BoldButton, labels.Bold);
        Tip(ItalicButton, labels.Italic);
        Tip(StrikeButton, labels.Strike);
        Tip(CodeButton, labels.Code);
        Tip(LinkButton, labels.Link);
        Tip(QuoteButton, labels.Quote);
        Tip(BulletButton, labels.BulletList);
        Tip(NumberButton, labels.NumberList);
        Tip(SuggestButton, labels.Suggest);
        SuggestButton.Visibility = suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewToggle.Content = labels.Preview;
        PreviewToggle.IsChecked = state.Preview;
        EmptyPreview.Text = labels.NothingToPreview;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = labels.Cancel;
        Input.PlaceholderText = placeholder;
        Input.Text = state.Text;
        Input.Select(Math.Min(state.SelectionStart, Input.Text.Length), state.SelectionLength);
        ReferenceList.ItemsSource = _proposals;

        var busy = state.BusyKey != 0;
        foreach (var child in Toolbar.Children)
        {
            if (child is not Control control) continue;
            control.AllowFocusOnInteraction = false;
            control.IsEnabled = !busy;
        }
        Input.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        UpdateActions();
        UpdatePreview();

        _referenceTimer = DispatcherQueue.CreateTimer();
        _referenceTimer.Interval = TimeSpan.FromMilliseconds(180);
        _referenceTimer.IsRepeating = false;
        Unloaded += (_, _) => _referenceTimer.Stop();
        _referenceTimer.Tick += (_, _) =>
        {
            if (_target is { } target) ReferencesRequested?.Invoke(this, (target.Kind, target.Query));
        };
    }

    /// <summary>A comment button was clicked (or Ctrl+Entrée): true for the second action (add to the review).</summary>
    public event EventHandler<bool>? Submitted;

    public event EventHandler? Canceled;

    /// <summary>The user typed <c>#</c>, <c>!</c> or <c>@</c> and some letters (after a pause): answer with <see cref="ShowReferences"/>.</summary>
    public event EventHandler<(ReferenceKind Kind, string Query)>? ReferencesRequested;

    /// <summary>Key of the last proposals asked for: only their answer is shown.</summary>
    public int ReferenceKey { get; set; }

    /// <summary>The text to send: trimmed, with <c>\n</c> line breaks.</summary>
    public string Body => Input.Text.Trim().ReplaceLineEndings("\n");

    /// <summary>Gives the focus to the text once the box is on screen (a zone shows its content after the next layout).</summary>
    public void FocusWhenShown()
    {
        _focusTries = 0;
        if (IsLoaded) TryFocus();
        else Loaded += OnLoadedFocus;
    }

    private void OnLoadedFocus(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedFocus;
        TryFocus();
    }

    private void TryFocus() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!IsLoaded || !Input.IsEnabled) return;
        if (Input.Focus(FocusState.Programmatic))
        {
            Input.Select(Math.Min(_state.SelectionStart, Input.Text.Length), _state.SelectionLength);
            return;
        }
        if (++_focusTries < MaxFocusTries) TryFocus();
    });

    /// <summary>The proposals for the last request; an answer to an older one is ignored.</summary>
    public void ShowReferences(int key, IReadOnlyList<ReferenceSuggestion> items)
    {
        if (key != ReferenceKey || _target is not { } target) return;
        _proposals.Clear();
        foreach (var item in items) _proposals.Add(item);
        if (_proposals.Count == 0)
        {
            CloseReferences();
            return;
        }
        ReferenceList.SelectedIndex = 0;
        // Under the character being completed (the popup is in the cell of the text box).
        var rect = Input.GetRectFromCharacterIndex(Math.Clamp(target.Start, 0, Math.Max(0, Input.Text.Length - 1)), false);
        ReferencePopup.HorizontalOffset = Math.Max(0, rect.Left);
        ReferencePopup.VerticalOffset = Math.Min(Input.ActualHeight, rect.Bottom + 2);
        ReferencePopup.IsOpen = true;
    }

    private void CloseReferences()
    {
        _referenceTimer.Stop();
        _target = null;
        ReferenceKey = 0;
        ReferencePopup.IsOpen = false;
        _proposals.Clear();
    }

    private bool ReferencesOpen => ReferencePopup.IsOpen && _proposals.Count > 0;

    private void ApplyReference(ReferenceSuggestion item)
    {
        if (_target is not { } target) return;
        var text = Input.Text;
        var end = Math.Min(target.End, text.Length);
        Input.Text = string.Concat(text.AsSpan(0, target.Start), item.Insert, " ", text.AsSpan(end));
        CloseReferences();
        Input.Focus(FocusState.Programmatic);
        Input.Select(target.Start + item.Insert.Length + 1, 0);
    }

    [GeneratedRegex(@"(?:^|[\s(])([#!@])([\w.-]*)$")]
    private static partial Regex ReferenceToken();

    /// <summary>A completion after <c>#</c>, <c>!</c> or <c>@</c> at the caret starts a request after a pause.</summary>
    private void UpdateReferences()
    {
        // Not for the text given back to a box created again (it has no focus yet): only for typing.
        if (!_canComplete || Input.FocusState == FocusState.Unfocused || Input.SelectionLength > 0 || ReferenceToken().Match(Input.Text[..Math.Min(Input.SelectionStart, Input.Text.Length)]) is not { Success: true } m)
        {
            CloseReferences();
            return;
        }
        var kind = m.Groups[1].Value switch { "#" => ReferenceKind.Issue, "!" => ReferenceKind.PullRequest, _ => ReferenceKind.Person };
        _target = (m.Groups[1].Index, m.Groups[2].Index + m.Groups[2].Length, kind, m.Groups[2].Value);
        _referenceTimer.Stop();
        _referenceTimer.Start();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _state.Text = Input.Text;
        UpdateActions();
        UpdatePreview();
        UpdateReferences();
    }

    private void OnInputSelectionChanged(object sender, RoutedEventArgs e)
    {
        _state.SelectionStart = Input.SelectionStart;
        _state.SelectionLength = Input.SelectionLength;
    }

    private void OnInputLostFocus(object sender, RoutedEventArgs e) => CloseReferences();

    /// <summary>
    /// Keys the text box leaves (Alt+arrows, Échap…) stop here: the editor around the zone and the diff view never see them.
    /// Ctrl keys go on to the shortcuts of the app (the text box keeps its own Ctrl+Z / Ctrl+Y, Ctrl+C…).
    /// </summary>
    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsDown(VirtualKey.Control)) e.Handled = true;
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        if (ReferencesOpen && !ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Down or VirtualKey.Up:
                    var count = _proposals.Count;
                    ReferenceList.SelectedIndex = (Math.Max(0, ReferenceList.SelectedIndex) + (e.Key == VirtualKey.Down ? 1 : count - 1)) % count;
                    ReferenceList.ScrollIntoView(ReferenceList.SelectedItem);
                    e.Handled = true;
                    return;
                case VirtualKey.Enter or VirtualKey.Tab:
                    if (ReferenceList.SelectedItem is ReferenceSuggestion item) ApplyReference(item);
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    CloseReferences();
                    e.Handled = true;
                    return;
            }
        }
        switch (e.Key)
        {
            // Échap closes the box (not the screen of the pull request).
            case VirtualKey.Escape:
                e.Handled = true;
                if (CancelButton.IsEnabled) Canceled?.Invoke(this, EventArgs.Empty);
                return;
            case VirtualKey.Enter when ctrl:
                e.Handled = true;
                Submit(draft: false);
                return;
        }
        if (!ctrl || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu)) return;
        var labels = EditorReviewLabels.Current;
        switch (e.Key)
        {
            case VirtualKey.B:
                Surround("**", "**", labels.BoldSample);
                e.Handled = true;
                break;
            case VirtualKey.I:
                Surround("_", "_", labels.ItalicSample);
                e.Handled = true;
                break;
            case VirtualKey.U:
                Surround("<u>", "</u>", labels.UnderlineSample);
                e.Handled = true;
                break;
        }
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Submit(bool draft)
    {
        if (_state.BusyKey == 0 && Body.Length > 0) Submitted?.Invoke(this, draft);
    }

    private void UpdateActions()
    {
        var enabled = _state.BusyKey == 0 && Input.Text.Trim().Length > 0;
        PrimaryButton.IsEnabled = enabled;
        SecondaryButton.IsEnabled = enabled;
    }

    private void UpdatePreview()
    {
        var on = PreviewToggle.IsChecked == true;
        PreviewPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var body = Body;
        PreviewView.Blocks = body.Length > 0 ? MarkdownParser.Parse(body, _references) : null;
        PreviewView.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreview.Visibility = body.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- toolbar (as the page)

    private void Surround(string before, string after, string placeholder)
    {
        var (start, length) = (Input.SelectionStart, Input.SelectionLength);
        var text = Input.Text;
        var chosen = length > 0 ? text.Substring(start, length) : placeholder;
        Input.Text = string.Concat(text.AsSpan(0, start), before + chosen + after, text.AsSpan(start + length));
        Input.Focus(FocusState.Programmatic);
        Input.Select(start + before.Length, chosen.Length);
    }

    /// <summary>Inserts a block on its own lines at the caret (replacing the selection).</summary>
    private void InsertBlock(string block)
    {
        var (start, length) = (Input.SelectionStart, Input.SelectionLength);
        var text = Input.Text;
        var end = start + length;
        var before = start > 0 && text[start - 1] != '\r' ? NewLine : string.Empty;
        var after = end < text.Length && text[end] != '\r' ? NewLine : string.Empty;
        var inserted = before + block + after;
        Input.Text = string.Concat(text.AsSpan(0, start), inserted, text.AsSpan(end));
        Input.Focus(FocusState.Programmatic);
        Input.Select(start + inserted.Length, 0);
    }

    /// <summary>Puts <paramref name="prefix"/> (given the index of the line) before each line of the selection.</summary>
    private void PrefixLines(Func<int, string> prefix)
    {
        var text = Input.Text;
        var start = Input.SelectionStart > 0 ? text.LastIndexOf('\r', Input.SelectionStart - 1) + 1 : 0;
        var end = text.IndexOf('\r', Input.SelectionStart + Input.SelectionLength);
        if (end < 0) end = text.Length;
        var lines = text[start..end].Split('\r');
        var block = string.Join(NewLine, lines.Select((line, i) => prefix(i) + line));
        Input.Text = string.Concat(text.AsSpan(0, start), block, text.AsSpan(end));
        Input.Focus(FocusState.Programmatic);
        Input.Select(start, block.Length);
    }

    private void OnHeadingClick(object sender, RoutedEventArgs e) => PrefixLines(_ => "### ");

    private void OnBoldClick(object sender, RoutedEventArgs e) => Surround("**", "**", EditorReviewLabels.Current.BoldSample);

    private void OnItalicClick(object sender, RoutedEventArgs e) => Surround("_", "_", EditorReviewLabels.Current.ItalicSample);

    private void OnStrikeClick(object sender, RoutedEventArgs e) => Surround("~~", "~~", EditorReviewLabels.Current.StrikeSample);

    private void OnCodeClick(object sender, RoutedEventArgs e) => Surround("`", "`", EditorReviewLabels.Current.CodeSample);

    private void OnLinkClick(object sender, RoutedEventArgs e) => Surround("[", "](https://)", EditorReviewLabels.Current.LinkSample);

    private void OnQuoteClick(object sender, RoutedEventArgs e) => PrefixLines(_ => "> ");

    private void OnBulletClick(object sender, RoutedEventArgs e) => PrefixLines(_ => "- ");

    private void OnNumberClick(object sender, RoutedEventArgs e) =>
        PrefixLines(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ");

    private void OnSuggestClick(object sender, RoutedEventArgs e) =>
        InsertBlock("```suggestion" + NewLine + _suggestion?.ReplaceLineEndings(NewLine) + NewLine + "```");

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        _state.Preview = PreviewToggle.IsChecked == true;
        UpdatePreview();
    }

    private void OnReferenceClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReferenceSuggestion item) ApplyReference(item);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Submit(draft: false);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Submit(draft: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Canceled?.Invoke(this, EventArgs.Empty);

    private static void Tip(DependencyObject element, string text)
    {
        ToolTipService.SetToolTip(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
    }
}
