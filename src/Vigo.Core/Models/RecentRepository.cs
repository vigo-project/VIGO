namespace Vigo.Core.Models;

/// <summary>Entry of the recent repositories list of the welcome screen.</summary>
/// <param name="Path">Path given to <c>OpenAsync</c> again: working directory, or Git directory for a bare repository.</param>
/// <param name="Name">Display name (root folder).</param>
/// <param name="Branch">Branch checked out when the repository was last opened; null for an empty repository.</param>
/// <param name="LastOpened">Last time the repository was opened in VIGO.</param>
/// <param name="IsPinned">Pinned entries stay at the top and are never trimmed.</param>
public sealed record RecentRepository(string Path, string Name, string? Branch, DateTimeOffset LastOpened, bool IsPinned);
