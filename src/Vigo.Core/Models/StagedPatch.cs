namespace Vigo.Core.Models;

/// <summary>The staged changes sent to an AI engine, and what was left out so that the prompt stays reasonable.</summary>
/// <param name="Text">Diffstat then patch; empty when nothing is staged.</param>
/// <param name="Truncated">The patch was cut at the size limit.</param>
/// <param name="RecentSubjects">Subjects of the last commits, to match the repository's style.</param>
public sealed record StagedPatch(string Text, bool Truncated, IReadOnlyList<string> RecentSubjects);
