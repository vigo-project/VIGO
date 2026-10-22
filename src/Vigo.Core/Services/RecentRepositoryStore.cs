using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Persistence of the recent repositories list.</summary>
public interface IRecentRepositoryStore
{
    /// <summary>Reads the saved list; empty when nothing was saved yet or the file is unreadable.</summary>
    Task<IReadOnlyList<RecentRepository>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<RecentRepository> repositories, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores the list in a JSON file (source-generated serializer: no reflection, AOT-compatible).
/// Writes go to a temporary file then replace the previous one, so a crash never leaves a truncated file.
/// </summary>
public sealed partial class JsonRecentRepositoryStore(string filePath, ILogger<JsonRecentRepositoryStore> logger) : IRecentRepositoryStore
{
    private const int CurrentVersion = 1;

    /// <summary>Default location: <c>%LOCALAPPDATA%\VIGO\recent-repositories.json</c>.</summary>
    public static string DefaultFilePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "recent-repositories.json");

    public async Task<IReadOnlyList<RecentRepository>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var file = await JsonFiles.ReadAsync(filePath, RecentRepositoriesJsonContext.Default.RecentRepositoriesFile,
            f => f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath), cancellationToken);
        return file is null ? [] : [.. file.Repositories.Where(r => !string.IsNullOrEmpty(r.Path) && !string.IsNullOrEmpty(r.Name))];
    }

    public async Task SaveAsync(IReadOnlyList<RecentRepository> repositories, CancellationToken cancellationToken = default)
    {
        try
        {
            await JsonFiles.WriteAtomicAsync(filePath, new RecentRepositoriesFile(CurrentVersion, [.. repositories]),
                RecentRepositoriesJsonContext.Default.RecentRepositoriesFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the list is an inconvenience, not a reason to fail the operation that triggered the save.
            LogSaveFailed(logger, e, filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recent repositories file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read recent repositories from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save recent repositories to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record RecentRepositoriesFile(int Version, RecentRepository[] Repositories);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RecentRepositoriesFile))]
internal sealed partial class RecentRepositoriesJsonContext : JsonSerializerContext;
