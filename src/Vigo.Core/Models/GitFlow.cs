using Vigo.Core.Services;

namespace Vigo.Core.Models;

/// <summary>The three kinds of short-lived branches of Git Flow (J7.8, ADR-069).</summary>
public enum GitFlowKind
{
    /// <summary>Started from the development branch, merged back into it.</summary>
    Feature,

    /// <summary>Started from the development branch, merged into the production branch (tagged) and the development branch.</summary>
    Release,

    /// <summary>Started from the production branch, merged into the production branch (tagged) and the development branch.</summary>
    Hotfix,
}

/// <summary>
/// Git Flow settings of a repository, stored under <c>gitflow.*</c> in <c>.git/config</c> as the git-flow extension does
/// (so a repository initialized by either tool is understood by both).
/// </summary>
/// <param name="Main">Production branch (<c>gitflow.branch.master</c>).</param>
/// <param name="Develop">Development branch (<c>gitflow.branch.develop</c>).</param>
/// <param name="FeaturePrefix">Prefix of feature branches, with its trailing "/" ("feature/").</param>
/// <param name="VersionTagPrefix">Prefix of the tags made when finishing a release or a hotfix (empty, or "v").</param>
public sealed record GitFlowConfig(
    string Main, string Develop, string FeaturePrefix, string ReleasePrefix, string HotfixPrefix, string VersionTagPrefix)
{
    /// <summary>The settings of <c>git flow init -d</c>, with <paramref name="main"/> as production branch.</summary>
    public static GitFlowConfig Defaults(string main = "main") => new(main, "develop", "feature/", "release/", "hotfix/", string.Empty);

    public string PrefixOf(GitFlowKind kind) => kind switch
    {
        GitFlowKind.Feature => FeaturePrefix,
        GitFlowKind.Release => ReleasePrefix,
        _ => HotfixPrefix,
    };

    /// <summary>Full branch name of <paramref name="name"/> ("feature/login").</summary>
    public string BranchOf(GitFlowKind kind, string name) => PrefixOf(kind) + name;

    /// <summary>Branch the kind starts from.</summary>
    public string BaseOf(GitFlowKind kind) => kind == GitFlowKind.Hotfix ? Main : Develop;

    /// <summary>The kind and short name of a Git Flow branch ("feature/login" → Feature, "login"); null for any other branch.</summary>
    public (GitFlowKind Kind, string Name)? Parse(string branch)
    {
        foreach (var kind in Enum.GetValues<GitFlowKind>())
        {
            var prefix = PrefixOf(kind);
            if (branch.Length > prefix.Length && branch.StartsWith(prefix, StringComparison.Ordinal)) return (kind, branch[prefix.Length..]);
        }
        return null;
    }

    /// <summary>Why these settings cannot be used; null when they can.</summary>
    public string? Validate()
    {
        if (RefNames.Check(Main) is { } main) return Loc.Format("GitFlow.Validate.Main", main);
        if (RefNames.Check(Develop) is { } develop) return Loc.Format("GitFlow.Validate.Develop", develop);
        if (Main == Develop) return Loc.Get("GitFlow.Validate.Same");
        string[] prefixes = [FeaturePrefix, ReleasePrefix, HotfixPrefix];
        foreach (var prefix in prefixes)
        {
            if (!prefix.EndsWith('/') || RefNames.Check(prefix + "x") is not null) return Loc.Format("GitFlow.Validate.Prefix", prefix);
        }
        if (prefixes.Distinct(StringComparer.Ordinal).Count() != prefixes.Length) return Loc.Get("GitFlow.Validate.PrefixesDistinct");
        if (VersionTagPrefix.Length > 0 && RefNames.Check(VersionTagPrefix + "1") is not null) return Loc.Format("GitFlow.Validate.TagPrefix", VersionTagPrefix);
        return null;
    }

    /// <summary>Name of the tag that marks the release or hotfix <paramref name="name"/>.</summary>
    public string TagOf(string name) => VersionTagPrefix + name;
}

/// <summary>A new Git Flow branch: <paramref name="Name"/> is the short name ("login", "1.2.0").</summary>
public sealed record GitFlowStartRequest(GitFlowKind Kind, string Name);

/// <summary>Finishing a Git Flow branch: merged (always with a merge commit), tagged for a release or a hotfix, then deleted.</summary>
/// <param name="KeepBranch">The branch is not deleted once merged.</param>
/// <param name="TagMessage">Message of the annotated tag of a release or a hotfix; null: "Release 1.2.0".</param>
public sealed record GitFlowFinishRequest(GitFlowKind Kind, string Name, bool KeepBranch, string? TagMessage = null);

/// <summary>What finishing a Git Flow branch did.</summary>
public sealed record GitFlowFinished(IReadOnlyList<string> MergedInto, string? Tag, bool BranchDeleted);
