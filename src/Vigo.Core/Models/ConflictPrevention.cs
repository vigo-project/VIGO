namespace Vigo.Core.Models;

/// <summary>Remote branch the current branch is checked against for conflicts (J7.11, ADR-078): the default branch of the remote.</summary>
/// <param name="Name">Short name ("origin/main").</param>
/// <param name="Sha">Commit it points to now.</param>
public sealed record ConflictTarget(string Name, GitSha Sha);
