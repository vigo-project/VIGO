using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Persistence of the user preferences.</summary>
public interface IPreferencesStore
{
    /// <summary>Reads the preferences synchronously (once, at launch; a few hundred bytes). Defaults when unreadable.</summary>
    UserPreferences Load();

    Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores the preferences in a JSON file (source-generated serializer: AOT-compatible), written to a temporary file
/// then moved over the previous one.
/// </summary>
public sealed partial class JsonPreferencesStore(string filePath, ILogger<JsonPreferencesStore> logger) : IPreferencesStore
{
    private const int CurrentVersion = 1;

    /// <summary>Default location: <c>%LOCALAPPDATA%\VIGO\preferences.json</c>.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "preferences.json");

    public UserPreferences Load()
    {
        if (!File.Exists(filePath)) return new UserPreferences();

        var file = JsonFiles.Read(filePath, PreferencesJsonContext.Default.PreferencesFile,
            f => f.Preferences is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath));
        return file?.Preferences?.Normalize() ?? new UserPreferences();
    }

    public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        try
        {
            await JsonFiles.WriteAtomicAsync(filePath, new PreferencesFile(CurrentVersion, preferences), PreferencesJsonContext.Default.PreferencesFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, e, filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preferences file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the preferences from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the preferences to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

/// <summary>Current preferences of the application (singleton), saved on every change.</summary>
public sealed class Preferences(IPreferencesStore store, UserPreferences initial) : IDisposable
{
    private readonly SemaphoreSlim _saving = new(1, 1);

    public UserPreferences Current { get; private set; } = initial.Normalize();

    // The semaphore is not disposed: a save still running (fire and forget) would release a disposed object, and a
    // SemaphoreSlim that never exposed its wait handle holds nothing to free.
    public void Dispose()
    {
    }

    /// <summary>Raised on the thread that changed the preferences.</summary>
    public event EventHandler? Changed;

    public async Task UpdateAsync(Func<UserPreferences, UserPreferences> change, CancellationToken cancellationToken = default)
    {
        var updated = change(Current).Normalize();
        if (updated == Current) return;
        Current = updated;
        Changed?.Invoke(this, EventArgs.Empty);

        await (_lastSave = SaveCurrentAsync());
    }

    private Task _lastSave = Task.CompletedTask;

    /// <summary>Completes once the last change made so far is written (awaited before the process exits, J9.26).</summary>
    public Task WhenSavedAsync() => _lastSave;

    // Saves in order; the last one written is the current value.
    // Not canceled: the value is already current in memory, a short write must not leave the file behind.
    private async Task SaveCurrentAsync()
    {
        await _saving.WaitAsync(CancellationToken.None);
        try
        {
            await Task.Run(() => store.SaveAsync(Current, CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            _saving.Release();
        }
    }
}

internal sealed record PreferencesFile(int Version, UserPreferences? Preferences);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PreferencesFile))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;
