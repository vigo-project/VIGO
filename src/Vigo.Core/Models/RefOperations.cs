using Vigo.Core.Services;

namespace Vigo.Core.Models;

/// <summary>A new local branch on <paramref name="StartPoint"/>, without upstream; switched to when <paramref name="Checkout"/>.</summary>
public sealed record CreateBranchRequest(string Name, GitSha StartPoint, bool Checkout);

/// <summary>A new tag on <paramref name="Target"/>: annotated when <paramref name="Message"/> is not empty, lightweight otherwise.</summary>
public sealed record CreateTagRequest(string Name, GitSha Target, string? Message);

/// <summary>
/// Updates (or deletes) one reference of a remote: <c>git push &lt;remote&gt; &lt;source&gt;:&lt;destination&gt;</c>.
/// </summary>
/// <param name="Remote">Remote name (<c>origin</c>).</param>
/// <param name="Source">Full local name (<c>refs/heads/x</c>, <c>refs/tags/v1</c>); null deletes <paramref name="Destination"/>.</param>
/// <param name="Destination">Full name on the remote (<c>refs/heads/x</c>, <c>refs/tags/v1</c>).</param>
/// <param name="SetUpstream">The local branch <paramref name="Source"/> then tracks the pushed branch.</param>
public sealed record RefPush(string Remote, string? Source, string Destination, bool SetUpstream = false)
{
    public bool IsDelete => Source is null;

    /// <summary>"git push origin refs/heads/x:refs/heads/x" as shown in the authentication dialog.</summary>
    public string CommandLine =>
        $"git push{(SetUpstream ? " --set-upstream" : string.Empty)} {Remote} {(Source is null ? "--delete " + Destination : Source + ":" + Destination)}";
}

/// <summary>
/// Validity of branch, tag and remote names, following <c>git check-ref-format</c>: checked while the user types,
/// before git is asked.
/// </summary>
public static class RefNames
{
    /// <summary>Why <paramref name="name"/> cannot name a branch or tag (short name, without <c>refs/…/</c>); null when it can.</summary>
    public static string? Check(string name)
    {
        if (name.Length == 0) return Loc.Get("RefNames.Required");
        if (name.StartsWith('-')) return Loc.Get("RefNames.StartsWithDash");
        if (name == "@") return Loc.Get("RefNames.AtOnly");
        if (name == "HEAD") return Loc.Get("RefNames.HeadReserved");
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c)) return Loc.Get("RefNames.Whitespace");
            if (c < ' ' || c == '\x7f') return Loc.Get("RefNames.ControlChar");
            if (c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') return Loc.Format("RefNames.BadChar", c);
        }
        if (name.Contains("..", StringComparison.Ordinal)) return Loc.Get("RefNames.DoubleDot");
        if (name.Contains("@{", StringComparison.Ordinal)) return Loc.Get("RefNames.AtBrace");
        if (name.StartsWith('/') || name.EndsWith('/')) return Loc.Get("RefNames.EdgeSlash");
        if (name.Contains("//", StringComparison.Ordinal)) return Loc.Get("RefNames.DoubleSlash");
        if (name.EndsWith('.')) return Loc.Get("RefNames.EndsWithDot");
        foreach (var part in name.Split('/'))
        {
            if (part.StartsWith('.')) return Loc.Get("RefNames.PartStartsWithDot");
            if (part.EndsWith(".lock", StringComparison.Ordinal)) return Loc.Get("RefNames.PartEndsWithLock");
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> would clash with an existing name of <paramref name="existing"/>: the same name, or
    /// a name that is a folder of the other ("feature" and "feature/x" cannot both exist). Null when it does not.
    /// </summary>
    public static string? CheckConflict(string name, IEnumerable<string> existing, RefNameKind kind)
    {
        var tag = kind == RefNameKind.Tag;
        foreach (var other in existing)
        {
            if (string.Equals(other, name, StringComparison.Ordinal)) return Loc.Format(tag ? "RefNames.Exists.Tag" : "RefNames.Exists.Branch", name);
            if (other.StartsWith(name + "/", StringComparison.Ordinal)) return Loc.Format(tag ? "RefNames.IsFolder.Tag" : "RefNames.IsFolder.Branch", other, name);
            if (name.StartsWith(other + "/", StringComparison.Ordinal)) return Loc.Format(tag ? "RefNames.NotFolder.Tag" : "RefNames.NotFolder.Branch", other);
        }
        return null;
    }
}

/// <summary>What a name designates, for the wording of the name clash messages of <see cref="RefNames"/>.</summary>
public enum RefNameKind
{
    Branch,
    Tag,
}
