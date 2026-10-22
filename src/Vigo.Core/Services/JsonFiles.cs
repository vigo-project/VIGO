using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vigo.Core.Services;

/// <summary>
/// Reading and writing of the versioned JSON settings files (preferences, layout, session, profiles, accounts, hidden refs,
/// recent repositories): the same steps for each, on the source-generated <see cref="JsonTypeInfo{T}"/> (AOT-compatible).
/// Each store keeps its own wrapper type, its version check and its log messages.
/// </summary>
internal static class JsonFiles
{
    /// <summary>
    /// The content of an existing file, or null when it cannot be used (kept as .bad first, J9.4): unreadable, or
    /// <paramref name="isSupported"/> refuses it (newer version, missing payload). <paramref name="onUnsupported"/> receives the
    /// file read (null when empty) and <paramref name="onFailed"/> the read error, for the store's log messages.
    /// </summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> info, Func<T, bool> isSupported, Action<T?> onUnsupported, Action<Exception> onFailed,
        FileShare share = FileShare.Read) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            var file = JsonSerializer.Deserialize(stream, info);
            if (file is not null && isSupported(file)) return file;
            onUnsupported(file);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            onFailed(e);
        }
        JsonFileBackup.Keep(path);
        return null;
    }

    /// <inheritdoc cref="Read{T}"/>
    public static async Task<T?> ReadAsync<T>(string path, JsonTypeInfo<T> info, Func<T, bool> isSupported, Action<T?> onUnsupported, Action<Exception> onFailed,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var file = await JsonSerializer.DeserializeAsync(stream, info, cancellationToken);
            if (file is not null && isSupported(file)) return file;
            onUnsupported(file);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            onFailed(e);
        }
        JsonFileBackup.Keep(path);
        return null;
    }

    /// <summary>Writes to a temporary file then moves it over the previous one. IO errors propagate: the store logs them.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, info, cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
