namespace Vigo.Core.Results;

// Git operation results modeled as C# 15 union types (ADR-002).
// Each case is an immutable record; the compiler checks that `switch`es are exhaustive.

/// <summary>Successful operation, with its value.</summary>
public sealed record Success<T>(T Value);

/// <summary>Successful operation without a return value.</summary>
public sealed record Done
{
    public static readonly Done Instance = new();
    private Done() { }
}

/// <summary>The operation stopped on conflicts (merge, rebase, cherry-pick, stash pop…).</summary>
public sealed record Conflict(IReadOnlyList<string> Paths, string Operation);

/// <summary>The remote server rejected authentication.</summary>
/// <param name="Remote">Remote name when git's arguments or output give it.</param>
/// <param name="Url">URL refused, as git printed it ("https://github.com/atlas/api.git/", "git@github.com").</param>
/// <param name="Details">Full error output of git.exe.</param>
public sealed record AuthenticationFailed(string? Remote, string Message, string? Url = null, string? Details = null);

/// <summary>Canceled by the user (CancellationToken).</summary>
public sealed record Canceled
{
    public static readonly Canceled Instance = new();
    private Canceled() { }
}

/// <summary>Failure: readable message, optional git.exe exit code, optional originating exception.</summary>
/// <param name="Details">
/// Full error output of git.exe (hook output, several error lines…) when it says more than <paramref name="Message"/>.
/// </param>
public sealed record Failure(string Message, int? ExitCode = null, Exception? Exception = null, string? Details = null)
{
    /// <summary>Known cause the caller can act on (e.g. offer to stash); <see cref="FailureReason.Unknown"/> otherwise.</summary>
    public FailureReason Reason { get; init; }
}

/// <summary>Causes of a <see cref="Failure"/> that a caller handles in a specific way.</summary>
public enum FailureReason
{
    Unknown,

    /// <summary>git refused because uncommitted changes would be overwritten (checkout, merge, stash apply).</summary>
    LocalChangesWouldBeOverwritten,

    /// <summary>git refused to remove a worktree holding modifications, untracked files or a lock: only <c>--force</c> removes it.</summary>
    WorktreeNeedsForce,

    /// <summary>git refused to deinitialize a submodule holding local modifications: only <c>--force</c> does.</summary>
    SubmoduleNeedsForce,

    /// <summary>
    /// The push was refused as a non-fast-forward and the remote tip is already known locally (amended or rebased
    /// commits): <c>--force-with-lease</c> is safe to offer.
    /// </summary>
    PushNeedsForce,
}

/// <summary>Result of a Git operation producing a value.</summary>
public union GitResult<T>(Success<T>, Conflict, AuthenticationFailed, Canceled, Failure);

/// <summary>Result of a Git operation without a value.</summary>
public union GitResult(Done, Conflict, AuthenticationFailed, Canceled, Failure);
