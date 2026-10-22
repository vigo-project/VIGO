using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Where the clone dialog takes the repository from: an address typed in, or the repositories of a signed-in account (J6.13).</summary>
public sealed class CloneSource
{
    /// <summary>The address typed in by the user (the dialog's original behavior).</summary>
    public static CloneSource FromUrl { get; } = new(null);

    public CloneSource(HostAccount? account)
    {
        Account = account;
        Label = account is null ? Loc.Get("Clone.Source.Url") : $"{ProviderName(account.Provider)} · {account.Login}";
    }

    /// <summary>Null for the typed address.</summary>
    public HostAccount? Account { get; }

    /// <summary>"URL", "GitHub · login".</summary>
    public string Label { get; }

    public override string ToString() => Label;

    private static string ProviderName(HostingProvider provider) => provider switch
    {
        HostingProvider.GitHub => "GitHub",
        HostingProvider.GitLab => "GitLab",
        HostingProvider.AzureDevOps => "Azure DevOps",
        HostingProvider.Bitbucket => "Bitbucket",
        _ => provider.ToString(),
    };
}

/// <summary>A row of the clone dialog's repository list.</summary>
/// <param name="Repository">What the service listed.</param>
/// <param name="UpdatedText">Relative date of the last push ("il y a 3 jours"); empty when unknown.</param>
public sealed record CloneRepositoryItem(HostedRepository Repository, string UpdatedText)
{
    public string Name => Repository.FullName;

    /// <summary>The row is the header of a group of repositories (<see cref="Name"/> is the group), not a repository.</summary>
    public bool IsHeader { get; init; }

    public bool IsRepository => !IsHeader;

    /// <summary>"ADP-Degradation · 4" on a header row.</summary>
    public string HeaderText { get; init; } = string.Empty;

    public static CloneRepositoryItem Header(string group, int count) =>
        new(new HostedRepository(group, null, false, false, string.Empty, null, null, null), string.Empty) { IsHeader = true, HeaderText = $"{group} · {count}" };

    /// <summary>The repository's own name ("api" of "contoso/Atlas/api"): the title of the row.</summary>
    public string ShortName => Repository.FullName[(Repository.FullName.LastIndexOf('/') + 1)..];

    /// <summary>Where it lives ("contoso/Atlas"): the second line of the row; empty without a parent.</summary>
    public string ParentName => Repository.FullName.LastIndexOf('/') is var at and > 0 ? Repository.FullName[..at] : string.Empty;

    public string Description => Repository.Description ?? string.Empty;

    public bool HasDescription => !string.IsNullOrEmpty(Repository.Description);

    public bool IsPrivate => Repository.IsPrivate;

    public bool IsFork => Repository.IsFork;

    public override string ToString() => Repository.FullName;
}
