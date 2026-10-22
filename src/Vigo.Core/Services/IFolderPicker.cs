namespace Vigo.Core.Services;

/// <summary>Native folder picker (implemented in the App).</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}
