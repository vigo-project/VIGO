namespace Vigo.Core.Models;

/// <summary>Switch to an existing local branch.</summary>
public sealed record CheckoutBranch(string Name);

/// <summary>
/// Create the local branch <paramref name="LocalName"/> from a remote branch, tracking it, and switch to it.
/// </summary>
/// <param name="RemoteBranch">Display name of the remote branch (<c>origin/feature/x</c>).</param>
/// <param name="LocalName">Name of the new local branch (<c>feature/x</c>).</param>
public sealed record CheckoutRemoteBranch(string RemoteBranch, string LocalName);

/// <summary>Check out a commit (or a tag) without a branch: detached HEAD.</summary>
public sealed record CheckoutCommit(GitSha Commit);

/// <summary>What <c>IGitService.CheckoutAsync</c> switches to.</summary>
public union CheckoutTarget(CheckoutBranch, CheckoutRemoteBranch, CheckoutCommit);
