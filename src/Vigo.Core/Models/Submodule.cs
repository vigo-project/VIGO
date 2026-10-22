namespace Vigo.Core.Models;

/// <summary>Where a submodule stands compared with what the repository records (<c>git submodule status</c>).</summary>
public enum SubmoduleState
{
    /// <summary>Declared in <c>.gitmodules</c> but not cloned yet (status prefix "-").</summary>
    NotInitialized,

    /// <summary>Cloned, on the commit the repository records.</summary>
    UpToDate,

    /// <summary>Cloned, on another commit than the recorded one (status prefix "+"): moved inside, or the repository moved on.</summary>
    OutOfSync,

    /// <summary>Merge conflict on the recorded commit (status prefix "U").</summary>
    Conflict,
}

/// <summary>A submodule of the repository: a nested repository pinned to a commit.</summary>
/// <param name="Name">Name in <c>.gitmodules</c> (usually the path).</param>
/// <param name="Path">Path relative to the working directory, with forward slashes.</param>
/// <param name="Url">Where it is cloned from (<c>.gitmodules</c>); null when the entry is missing there.</param>
/// <param name="Branch">Branch followed by <c>submodule update --remote</c> (<c>.gitmodules</c>); null when not set.</param>
/// <param name="Head">Commit shown by git: the checked-out one, or the recorded one while not initialized.</param>
public sealed record Submodule(
    string Name,
    string Path,
    string? Url,
    string? Branch,
    SubmoduleState State,
    GitSha? Head)
{
    public bool IsInitialized => State != SubmoduleState.NotInitialized;

    /// <summary>Last folder of the path, shown in the left panel.</summary>
    public string Label
    {
        get
        {
            var cut = Path.LastIndexOf('/');
            return cut < 0 ? Path : Path[(cut + 1)..];
        }
    }
}
