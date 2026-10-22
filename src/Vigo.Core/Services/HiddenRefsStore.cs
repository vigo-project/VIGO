using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Vigo.Core.Services;

/// <summary>
/// Persistence of the refs hidden from the graph, per repository, in a JSON file (source-generated serializer: AOT-compatible),
/// written to a temporary file then moved over the previous one.
/// </summary>
public sealed partial class JsonHiddenRefsStore(string filePath, ILogger<JsonHiddenRefsStore> logger) : IDisposable
{
    private const int CurrentVersion = 1;

    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <summary>Default location: <c>%LOCALAPPDATA%\VIGO\hidden-refs.json</c>.</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "hidden-refs.json");

    /// <summary>
    /// Keys hidden in <paramref name="repository"/> (its Git directory), read synchronously when a tab is created so that
    /// the first graph already leaves them out (the file is a few hundred bytes). Empty when missing or unreadable.
    /// </summary>
    public IReadOnlySet<string> Load(string repository) =>
        Read().TryGetValue(repository, out var keys)
            ? new HashSet<string>(keys.Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private Task _lastSave = Task.CompletedTask;

    /// <summary>Completes once the last save requested so far is written (awaited before the process exits, J9.26).</summary>
    public Task WhenSavedAsync() => _lastSave;

    /// <summary>Replaces the keys hidden in <paramref name="repository"/>; the other repositories are kept.</summary>
    public Task SaveAsync(string repository, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        // Saves queue on the semaphore in the order they are requested: the last one written is the last one asked for.
        // The place in the queue is taken now (on the caller's thread); the file work runs on the thread pool.
        var turn = _writing.WaitAsync(cancellationToken);
        return _lastSave = Task.Run(() => WriteAsync(turn, repository, keys, cancellationToken), CancellationToken.None);
    }

    private async Task WriteAsync(Task turn, string repository, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        await turn;
        try
        {
            var repositories = Read();
            if (keys.Count == 0) repositories.Remove(repository);
            else repositories[repository] = [.. keys.Order(StringComparer.Ordinal)];

            await JsonFiles.WriteAtomicAsync(filePath, new HiddenRefsFile(CurrentVersion, repositories), HiddenRefsJsonContext.Default.HiddenRefsFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the choice only means the refs are shown again.
            LogSaveFailed(logger, e, filePath);
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Dispose() => _writing.Dispose();

    private Dictionary<string, string[]> Read()
    {
        var empty = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath)) return empty;
        var file = JsonFiles.Read(filePath, HiddenRefsJsonContext.Default.HiddenRefsFile,
            f => f.Repositories is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath), FileShare.ReadWrite);
        // Paths compare like Windows does.
        return file is null ? empty : new Dictionary<string, string[]>(file.Repositories!, StringComparer.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hidden refs file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the hidden refs from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the hidden refs to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record HiddenRefsFile(int Version, Dictionary<string, string[]>? Repositories);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(HiddenRefsFile))]
internal sealed partial class HiddenRefsJsonContext : JsonSerializerContext;
