using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Persistence of the window placement and panel / column widths.</summary>
public interface ILayoutStore
{
    /// <summary>
    /// Reads the saved layout synchronously: called once, before the main window is created, so that the window
    /// opens directly at its saved place (the file is a few hundred bytes). Defaults when missing or unreadable.
    /// </summary>
    LayoutSettings Load();

    Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores the layout in a JSON file (source-generated serializer: AOT-compatible), written to a temporary file
/// then moved over the previous one.
/// </summary>
public sealed partial class JsonLayoutStore(string filePath, ILogger<JsonLayoutStore> logger) : ILayoutStore
{
    private const int CurrentVersion = 1;

    /// <summary>Default location: <c>%LOCALAPPDATA%\VIGO\layout.json</c>.</summary>
    public static string DefaultFilePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "layout.json");

    public LayoutSettings Load()
    {
        if (!File.Exists(filePath))
        {
            return new LayoutSettings();
        }

        var file = JsonFiles.Read(filePath, LayoutJsonContext.Default.LayoutFile,
            f => f.Layout is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath));
        return file?.Layout?.Normalize() ?? new LayoutSettings();
    }

    public async Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            await JsonFiles.WriteAtomicAsync(filePath, new LayoutFile(CurrentVersion, settings), LayoutJsonContext.Default.LayoutFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the layout is an inconvenience only.
            LogSaveFailed(logger, e, filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Layout file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the layout from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the layout to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record LayoutFile(int Version, LayoutSettings? Layout);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LayoutFile))]
internal sealed partial class LayoutJsonContext : JsonSerializerContext;
