namespace Vigo.Core.Models;

/// <summary>What <c>IGitService.ResetAsync</c> does with the index and the working directory (<c>git reset --soft|--mixed|--hard</c>).</summary>
public enum ResetMode
{
    /// <summary>Only the branch moves: the changes of the commits left behind stay staged.</summary>
    Soft,

    /// <summary>The branch and the index move: the changes stay in the working directory, unstaged.</summary>
    Mixed,

    /// <summary>The branch, the index and the tracked files move: uncommitted changes are lost (untracked files are kept).</summary>
    Hard,

    /// <summary>
    /// The branch, the index and the files that differ between the two commits move; uncommitted changes are kept, and
    /// git refuses when one of them touches such a file (<c>git reset --keep</c>: Undo / Redo only, never in the menus).
    /// </summary>
    Keep,
}
