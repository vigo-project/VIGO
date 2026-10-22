namespace Vigo.Core.Models;

/// <summary>Phase of a network operation as reported by git's <c>--progress</c> output.</summary>
public enum GitProgressPhase
{
    /// <summary>Any other line ("remote: …", "Fetching origin", "POST git-upload-pack…").</summary>
    Other,
    Enumerating,
    Counting,
    Compressing,
    Receiving,
    Unpacking,
    Resolving,
    Writing,

    /// <summary>Files written to the working directory after a clone ("Updating files").</summary>
    CheckingOut,

    /// <summary>A pull finished fetching and now merges or rebases: stopping git here could leave <c>index.lock</c> and a half-updated tree.</summary>
    Merging,
}

/// <summary>One progress update of git.exe (<c>Receiving objects:  42% (123/456), 1.20 MiB | 4.20 MiB/s</c>).</summary>
/// <param name="Text">Raw line, without the "remote: " prefix.</param>
/// <param name="Percent">0–100 when the line has a percentage.</param>
/// <param name="Throughput">"4.20 MiB/s" when given.</param>
/// <param name="IsRemote">The line comes from the server ("remote: Counting objects…").</param>
public sealed record GitProgress(
    GitProgressPhase Phase,
    string Text,
    int? Percent = null,
    long? Current = null,
    long? Total = null,
    string? Throughput = null,
    bool IsRemote = false)
{
    /// <summary>
    /// Overall completion (0–1) of a fetch / pull (receiving 0–80 %, resolving 80–100 %) or push (writing); null when
    /// the phase gives no usable measure (counting, compressing on the server…).
    /// </summary>
    public double? Fraction => (Phase, Percent) switch
    {
        (GitProgressPhase.Receiving or GitProgressPhase.Unpacking, { } p) when !IsRemote => 0.8 * p / 100,
        (GitProgressPhase.Resolving, { } p) when !IsRemote => 0.8 + 0.2 * p / 100,
        (GitProgressPhase.Writing, { } p) when !IsRemote => p / 100.0,
        _ => null,
    };
}

/// <summary>How <see cref="Git.IGitService.PullAsync"/> integrates the upstream commits.</summary>
public enum PullMode
{
    /// <summary>The user's config decides (<c>pull.rebase</c>, <c>pull.ff</c>).</summary>
    Default,

    /// <summary>Merge commit when the branches diverged (<c>--no-rebase</c>).</summary>
    Merge,

    /// <summary>Local commits replayed on top of the upstream ones (<c>--rebase</c>).</summary>
    Rebase,

    /// <summary>Only when no merge is needed (<c>--ff-only</c>).</summary>
    FastForwardOnly,
}

/// <summary>What <see cref="Git.IGitService.PushAsync"/> sends.</summary>
/// <param name="ForceWithLease">
/// Overwrite the remote branch (after an amend or a rebase), unless it moved since the last fetch.
/// </param>
public sealed record PushRequest(bool ForceWithLease = false);

/// <summary>What <see cref="Git.IGitService.CloneAsync"/> clones, and where.</summary>
/// <param name="Url">Remote URL (HTTPS, SSH, scp-like or local path).</param>
/// <param name="Directory">Destination: created when missing, refused when it is not empty.</param>
/// <param name="RecurseSubmodules">Also clone the submodules (<c>--recurse-submodules</c>).</param>
/// <param name="Shallow">Only the last commit of each branch (<c>--depth 1</c>).</param>
public sealed record CloneRequest(string Url, string Directory, bool RecurseSubmodules = true, bool Shallow = false);
