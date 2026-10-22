namespace Vigo.Core.Models;

/// <summary>A repository the signed-in account can clone, as the hosting service lists it (J6.13).</summary>
/// <param name="FullName">"owner/name"; "organization/project/name" on Azure DevOps.</param>
/// <param name="Description">One-line description when the service has one.</param>
/// <param name="IsPrivate">Only members can see it.</param>
/// <param name="IsFork">Forked from another repository.</param>
/// <param name="HttpsUrl">Clone address over HTTPS.</param>
/// <param name="SshUrl">Clone address over SSH, when the service gives one.</param>
/// <param name="UpdatedAt">Last push when the service tells it.</param>
/// <param name="Owner">User, organization or project that holds the repository.</param>
public sealed record HostedRepository(
    string FullName, string? Description, bool IsPrivate, bool IsFork, string HttpsUrl, string? SshUrl, DateTimeOffset? UpdatedAt, string? Owner);

/// <summary>The repositories of one account; <see cref="IsTruncated"/> when the service has more than the pages read.</summary>
public sealed record HostedRepositoryList(IReadOnlyList<HostedRepository> Items, bool IsTruncated);
