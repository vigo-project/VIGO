using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Profiles and the active one, as saved in <c>profiles.json</c>.</summary>
/// <param name="ActiveId">Profile of the repositories no folder rule matches; null when it was never chosen.</param>
public sealed record GitProfileSet(ImmutableArray<GitProfile> Profiles, string? ActiveId)
{
    public static readonly GitProfileSet Empty = new([], null);
}

/// <summary>Persistence of the profiles.</summary>
public interface IGitProfileStore
{
    /// <summary>Reads the profiles synchronously (once, at launch). Empty when unreadable.</summary>
    GitProfileSet Load();

    Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default);
}

/// <summary>
/// Git profiles (singleton, J6.7, ADR-051): which identity a repository uses (first matching folder rule, else the
/// active profile), and which profile owns the hosting accounts and the session. Without any profile VIGO changes
/// nothing: git.exe keeps the user's own configuration, and everything belongs to <see cref="SessionProfiles.Default"/>.
/// </summary>
public sealed class GitProfiles(IGitProfileStore store) : IDisposable
{
    private readonly SemaphoreSlim _saving = new(1, 1);
    private GitProfileSet _current = Normalize(store.Load());

    // The semaphore is not disposed: a save still running (fire and forget) would release a disposed object, and a
    // SemaphoreSlim that never exposed its wait handle holds nothing to free.
    public void Dispose()
    {
    }

    public IReadOnlyList<GitProfile> Profiles => _current.Profiles;

    /// <summary>Raised on the thread that changed the profiles.</summary>
    public event EventHandler? Changed;

    /// <summary>Profile of the repositories no rule matches; null without profiles.</summary>
    public GitProfile? Active => _current.Profiles.FirstOrDefault(p => p.Id == _current.ActiveId);

    /// <summary>Key of the active profile in the session and account files; "default" without profiles.</summary>
    public string ActiveId => Active?.Id ?? SessionProfiles.Default;

    /// <summary>The profile a repository uses: the first whose folder rule matches, else the active one.</summary>
    public GitProfile? ResolveFor(string? repositoryPath)
    {
        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            foreach (var profile in _current.Profiles)
            {
                if (profile.Matches(repositoryPath)) return profile;
            }
        }
        return Active;
    }

    /// <summary>Key of <see cref="ResolveFor"/> ("default" without profiles).</summary>
    public string IdFor(string? repositoryPath) => ResolveFor(repositoryPath)?.Id ?? SessionProfiles.Default;

    /// <summary>
    /// Adds a profile. The first one takes the id "default": the hosting accounts and the session saved before profiles
    /// existed are then its own.
    /// </summary>
    public GitProfile Add(string label, string name, string email)
    {
        var id = _current.Profiles.IsEmpty ? SessionProfiles.Default : Guid.NewGuid().ToString("N");
        var profile = new GitProfile(id, label, name, email);
        Apply(new(_current.Profiles.Add(profile), _current.ActiveId ?? id));
        return profile;
    }

    /// <summary>Replaces the profile that has the same id.</summary>
    public void Update(GitProfile profile)
    {
        var index = -1;
        for (var i = 0; i < _current.Profiles.Length; i++)
        {
            if (_current.Profiles[i].Id == profile.Id) index = i;
        }
        if (index < 0 || _current.Profiles[index] == profile) return;
        Apply(_current with { Profiles = _current.Profiles.SetItem(index, profile) });
    }

    public void SetActive(string id)
    {
        if (_current.ActiveId == id || _current.Profiles.All(p => p.Id != id)) return;
        Apply(_current with { ActiveId = id });
    }

    /// <summary>
    /// Removes a profile; the first remaining one becomes active if it was. Its hosting accounts and session stay in
    /// their files, unreachable (and reused if a profile with the same id comes back, which only happens for "default").
    /// </summary>
    public void Remove(string id)
    {
        var remaining = _current.Profiles.RemoveAll(p => p.Id == id);
        if (remaining.Length == _current.Profiles.Length) return;
        var active = remaining.Any(p => p.Id == _current.ActiveId) ? _current.ActiveId : remaining.FirstOrDefault()?.Id;
        Apply(new(remaining, active));
    }

    private void Apply(GitProfileSet next)
    {
        _current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        _lastSave = SaveAsync();
    }

    private Task _lastSave = Task.CompletedTask;

    /// <summary>Completes once the last change made so far is written (awaited before the process exits, J9.26).</summary>
    public Task WhenSavedAsync() => _lastSave;

    // Saves are serialized; the last one wins.
    private async Task SaveAsync()
    {
        await _saving.WaitAsync();
        try
        {
            // On the thread pool: the first file calls of the store would otherwise run on the caller's (UI) thread.
            var snapshot = _current;
            await Task.Run(() => store.SaveAsync(snapshot, CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            _saving.Release();
        }
    }

    // Hand-edited file: blank ids dropped, duplicates ignored, an unknown active profile falls back to the first one.
    private static GitProfileSet Normalize(GitProfileSet set)
    {
        var profiles = (set.Profiles.IsDefault ? [] : set.Profiles)
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .DistinctBy(p => p.Id)
            .Select(p => p with
            {
                Label = string.IsNullOrWhiteSpace(p.Label) ? (p.Name ?? string.Empty) : p.Label,
                Name = p.Name ?? string.Empty,
                Email = p.Email ?? string.Empty,
                SigningFormat = SigningFormats.Normalize(p.SigningFormat),
                FolderRules = [.. p.FolderRules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())],
            })
            .ToImmutableArray();
        var active = profiles.Any(p => p.Id == set.ActiveId) ? set.ActiveId : profiles.FirstOrDefault()?.Id;
        return new(profiles, active);
    }
}

/// <summary>Stores the profiles in <c>%LOCALAPPDATA%\VIGO\profiles.json</c> (source-generated serializer: AOT).</summary>
public sealed partial class JsonGitProfileStore(string filePath, ILogger<JsonGitProfileStore> logger) : IGitProfileStore
{
    private const int CurrentVersion = 1;

    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "profiles.json");

    public GitProfileSet Load()
    {
        if (!File.Exists(filePath)) return GitProfileSet.Empty;
        var file = JsonFiles.Read(filePath, GitProfilesJsonContext.Default.GitProfilesFile,
            f => f.Profiles is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath));
        return file is null ? GitProfileSet.Empty : new([.. file.Profiles!.Where(p => p is not null)], file.ActiveId);
    }

    public async Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default)
    {
        try
        {
            await JsonFiles.WriteAtomicAsync(filePath, new GitProfilesFile(CurrentVersion, profiles.ActiveId, [.. profiles.Profiles]),
                GitProfilesJsonContext.Default.GitProfilesFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, e, filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profiles file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the profiles from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the profiles to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record GitProfilesFile(int Version, string? ActiveId, List<GitProfile>? Profiles);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GitProfilesFile))]
internal sealed partial class GitProfilesJsonContext : JsonSerializerContext;
