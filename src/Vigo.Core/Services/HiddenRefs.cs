using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Branches, tags and stashes the user hid from the graph of one repository. The choice is kept by the
/// <see cref="JsonHiddenRefsStore"/>, so it survives restarts. A hidden ref has no badge, and the commits only it reaches
/// are not drawn; it stays in the left panel, dimmed, to be shown again.
/// </summary>
public sealed class HiddenRefs
{
    private readonly JsonHiddenRefsStore? _store;
    private readonly string _repository;
    private readonly HashSet<string> _keys;

    /// <param name="store">Persistence; null keeps the choice for the life of the tab only.</param>
    /// <param name="repository">Key of the repository in the store (its Git directory).</param>
    public HiddenRefs(JsonHiddenRefsStore? store, string repository)
    {
        _store = store;
        _repository = repository;
        _keys = store is null ? new HashSet<string>(StringComparer.Ordinal) : [.. store.Load(repository)];
    }

    /// <summary>Raised on the calling thread after the set of hidden refs changed.</summary>
    public event EventHandler? Changed;

    public bool IsEmpty => _keys.Count == 0;

    /// <summary>Snapshot of the hidden keys.</summary>
    public IReadOnlyCollection<string> Keys => [.. _keys];

    /// <summary>
    /// Key of a ref: its full name for a branch or a tag, the commit of a stash (<c>stash@{n}</c> shifts as stashes
    /// come and go).
    /// </summary>
    public static string KeyOf(GitRef reference) =>
        reference.Kind == GitRefKind.Stash ? "stash:" + reference.Target : reference.CanonicalName;

    public bool IsHidden(string key) => _keys.Contains(key);

    public bool IsHidden(GitRef reference) => _keys.Count > 0 && _keys.Contains(KeyOf(reference));

    /// <summary>Hides <paramref name="key"/>, or shows it again; saved right away.</summary>
    public void Toggle(string key)
    {
        if (!_keys.Remove(key)) _keys.Add(key);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Full names the graph must leave out (branches and tags; stashes are filtered by the graph itself).</summary>
    public IReadOnlyList<string> ExcludedRefs() => [.. _keys.Where(k => k.StartsWith("refs/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private void Save()
    {
        if (_store is null) return;
        var keys = Keys;
        // The store writes in the order saves are requested; a failure is logged by the store.
        _saving = _store.SaveAsync(_repository, keys);
    }

    private Task _saving = Task.CompletedTask;

    /// <summary>Completes once the changes made so far are written (tests).</summary>
    internal Task WhenSavedAsync() => _saving;
}
