using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// An <c>Authorization</c> header git sends over HTTPS (J6.16, ADR-085): set as <c>http.&lt;url&gt;.extraHeader</c> for each of
/// <paramref name="Urls"/>, so that it never reaches another host. <paramref name="Value"/> is the whole header line.
/// </summary>
public sealed record GitHttpHeader(HostingProvider Provider, IReadOnlyList<string> Urls, string Value);

/// <summary>A service refused the token VIGO sent for the git commands of a repository (<see cref="IGitHttpCredentials.ReportRejected"/>).</summary>
public sealed class GitTokenRejectedEventArgs(string? repositoryPath, IReadOnlyList<HostingProvider> providers) : EventArgs
{
    public string? RepositoryPath { get; } = repositoryPath;

    public IReadOnlyList<HostingProvider> Providers { get; } = providers;
}

/// <summary>Credentials VIGO hands to its git commands from the signed-in hosting accounts, ahead of the credential helpers.</summary>
public interface IGitHttpCredentials
{
    /// <summary>Headers of the accounts that apply to the repository's profile (the active one without a folder); empty when none.</summary>
    Task<IReadOnlyList<GitHttpHeader>> GetHeadersAsync(string? repositoryPath, CancellationToken cancellationToken);

    /// <summary>The service refused the token sent with the headers: git ran again without them.</summary>
    void ReportRejected(string? repositoryPath, IReadOnlyList<GitHttpHeader> headers);
}
