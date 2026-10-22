using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Search box of the toolbar: what is typed ("fix crash author:alice file:README") narrows the graph to the matching
/// commits once the typing pauses; an empty box shows the whole history again.
/// </summary>
public sealed partial class CommitSearchViewModel : ObservableObject, IDisposable
{
    /// <summary>Pause after the last keystroke before <c>git log</c> is asked.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly GraphViewModel _graph;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _pending;

    public CommitSearchViewModel(GraphViewModel graph, TimeProvider time)
    {
        _graph = graph;
        _time = time;
        _graph.PropertyChanged += OnGraphPropertyChanged;
    }

    /// <summary>What is typed in the box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>The graph shows a search result.</summary>
    public bool IsActive => _graph.IsFiltered;

    /// <summary>"12 commits", "Aucun résultat", "Recherche…"; empty when there is nothing to say.</summary>
    public string Summary
    {
        get
        {
            if (_graph.FilterError is { } error) return error;
            if (_graph.IsSearching) return Loc.Get("Search.Running");
            if (!_graph.IsFiltered) return string.Empty;
            return _graph.FilterResultCount switch
            {
                0 => Loc.Get("Search.NoResult"),
                var n when _graph.IsFilterTruncated => Loc.Format("Search.CountTruncated", n),
                1 => Loc.Get("Search.OneCommit"),
                var n => Loc.Format("Search.Commits", n),
            };
        }
    }

    public bool HasSummary => Summary.Length > 0;

    /// <summary>Operators understood by the box, for its tooltip.</summary>
    public static string Help => Loc.Get("Search.Help");

    partial void OnTextChanged(string value)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var source = _pending = new CancellationTokenSource();
        _ = ApplyAsync(value, source.Token);
    }

    private async Task ApplyAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            // Clearing is immediate; typing waits for a pause.
            if (!string.IsNullOrWhiteSpace(text)) await Task.Delay(Debounce, _time, cancellationToken);
            await _graph.SetFilterAsync(CommitFilter.Parse(text));
        }
        catch (OperationCanceledException)
        {
            // Typed on: the next keystroke owns the search.
        }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() => Text = string.Empty;

    private bool CanClear() => Text.Length > 0;

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphViewModel.IsSearching) or nameof(GraphViewModel.IsFilterTruncated) or nameof(GraphViewModel.FilterError)
            or nameof(GraphViewModel.FilterResultCount) or nameof(GraphViewModel.IsFiltered))
        {
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasSummary));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public void Dispose()
    {
        _graph.PropertyChanged -= OnGraphPropertyChanged;
        _pending?.Cancel();
        _pending?.Dispose();
    }
}
