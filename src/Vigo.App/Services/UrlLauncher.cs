using Microsoft.Extensions.Logging;
using Vigo.Core.Services;
using Windows.System;

namespace Vigo.App.Services;

/// <summary>Opens https pages in the default browser (<see cref="Launcher"/>); any other scheme is refused.</summary>
public sealed partial class UrlLauncher(ILogger<UrlLauncher> logger) : IUrlLauncher
{
    public async Task<bool> OpenAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            LogLaunchFailed(logger, e, uri.Host);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open a page of {Host} in the browser")]
    private static partial void LogLaunchFailed(ILogger logger, Exception exception, string host);
}
