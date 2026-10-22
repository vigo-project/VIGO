namespace Vigo.Core.Services;

/// <summary>Opens a web page in the user's browser (pull requests, issues…). Only https pages are opened.</summary>
public interface IUrlLauncher
{
    /// <summary>False when the URL is not an absolute https address or the browser could not be started.</summary>
    Task<bool> OpenAsync(string url);
}
