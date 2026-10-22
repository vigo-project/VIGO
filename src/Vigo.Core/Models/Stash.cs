namespace Vigo.Core.Models;

/// <summary>
/// Puts the changes of the working directory and the index aside (<c>git stash push</c>).
/// </summary>
/// <param name="Message">Description of the stash; null or blank: git's "WIP on &lt;branch&gt;: &lt;sha&gt; &lt;subject&gt;".</param>
/// <param name="IncludeUntracked">Untracked files are stashed (and removed) too; ignored files never are.</param>
/// <param name="Paths">Only the changes under these repository-relative paths (files or folders, literal); null or empty: everything.</param>
public sealed record StashRequest(string? Message, bool IncludeUntracked, IReadOnlyList<string>? Paths = null, string? Description = null);
