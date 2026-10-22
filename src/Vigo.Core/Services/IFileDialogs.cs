namespace Vigo.Core.Services;

/// <summary>A group of file extensions offered by a file dialog ("Patch files" → ".patch", ".diff").</summary>
public sealed record FileDialogFilter(string Name, IReadOnlyList<string> Extensions);

/// <summary>Native file pickers (implemented in the App, one per window: ADR-033).</summary>
public interface IFileDialogs
{
    /// <summary>Asks where to save a file; null when the user cancels. The first filter gives the default extension.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default);

    /// <summary>Asks for an existing file; null when the user cancels.</summary>
    Task<string?> PickOpenFileAsync(IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default);
}
