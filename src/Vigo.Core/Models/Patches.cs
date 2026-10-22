namespace Vigo.Core.Models;

/// <summary>Which changes of the working directory a patch holds (J7.12, ADR-079).</summary>
public enum PatchScope
{
    /// <summary>Changes not staged yet (<c>git diff</c>).</summary>
    Unstaged,

    /// <summary>Staged changes (<c>git diff --cached</c>).</summary>
    Staged,

    /// <summary>Staged and unstaged changes against HEAD (<c>git diff HEAD</c>).</summary>
    All,
}

/// <summary>What <c>IGitService.ApplyPatchAsync</c> did.</summary>
public enum PatchApplyOutcome
{
    /// <summary>A diff was applied to the working directory and the index; nothing committed.</summary>
    Applied,

    /// <summary>A mailbox (<c>git format-patch</c> output) was applied: its commits were created.</summary>
    Committed,
}
