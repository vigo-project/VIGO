using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Persistence of the open repositories, per profile, in a JSON file (source-generated serializer: AOT-compatible), written to a
/// temporary file then moved over the previous one.
/// </summary>
public sealed partial class JsonSessionStore(string filePath, ILogger<JsonSessionStore> logger) : IDisposable
{
    public void Dispose() => _saving.Dispose();

    private const int CurrentVersion = 1;

    /// <summary>Default location: <c>%LOCALAPPDATA%\VIGO\session.json</c>.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "session.json");

    /// <summary>Session of <paramref name="profile"/>; empty when missing or unreadable.</summary>
    public async Task<SessionSettings> LoadAsync(string profile, CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken))?.TryGetValue(profile, out var session) == true ? Normalize(session) : SessionSettings.Empty;

    // Background saves (J9.9) and the one on exit must not share the temporary file.
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <summary>Replaces the session of <paramref name="profile"/>; the other profiles are kept.</summary>
    public async Task SaveAsync(string profile, SessionSettings session, CancellationToken cancellationToken = default)
    {
        await _saving.WaitAsync(CancellationToken.None);
        try
        {
            // Unreadable file: writing now would erase the other profiles' sessions (J9.4).
            if (await ReadAsync(cancellationToken) is not { } profiles) return;
            profiles[profile] = session;
            await JsonFiles.WriteAtomicAsync(filePath, new SessionFile(CurrentVersion, profiles), SessionJsonContext.Default.SessionFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the session only means the tabs are not reopened.
            LogSaveFailed(logger, e, filePath);
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>The sessions of every profile; empty when there is no file, null when it exists but cannot be read.</summary>
    private async Task<Dictionary<string, SessionSettings>?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return new(StringComparer.Ordinal);
        var file = await JsonFiles.ReadAsync(filePath, SessionJsonContext.Default.SessionFile,
            f => f.Profiles is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath), cancellationToken);
        return file is null ? null : new(file.Profiles!, StringComparer.Ordinal);
    }

    /// <summary>Drops empty paths and windows without tabs; an out-of-range selection becomes "welcome tab".</summary>
    private static SessionSettings Normalize(SessionSettings session) => new(
        NormalizeWindows(session),
        // Projects are cleaned by TabProjects.Load.
        session.Projects ?? []);

    private static SessionWindow[] NormalizeWindows(SessionSettings session) =>
    [
        .. (session.Windows ?? [])
            .Where(w => w is not null)
            .Select(w => (Paths: (w.Repositories ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList(), Window: w))
            .Where(w => w.Paths.Count > 0)
            .Select(w => new SessionWindow(
                w.Paths,
                w.Window.SelectedIndex >= 0 && w.Window.SelectedIndex < w.Paths.Count ? w.Window.SelectedIndex : -1,
                w.Window.Bounds is { Width: > 0, Height: > 0 } bounds ? bounds : null,
                w.Window.IsMaximized,
                (w.Window.Unrestored ?? []).Where(u => w.Paths.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList())),
    ];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the session from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the session to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record SessionFile(int Version, Dictionary<string, SessionSettings>? Profiles);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
