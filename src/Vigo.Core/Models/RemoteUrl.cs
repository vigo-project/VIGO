using System.Globalization;

namespace Vigo.Core.Models;

/// <summary>A configured remote (<c>git remote -v</c>).</summary>
/// <param name="PushUrl">Separate push URL (<c>remote.&lt;name&gt;.pushurl</c>); null when it is the fetch URL.</param>
public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl = null);

public enum RemoteProtocol
{
    Https,
    Http,
    Ssh,

    /// <summary>Local path or <c>file://</c>.</summary>
    Local,
}

/// <summary>
/// Parsed remote URL: <c>https://[user@]host[:port]/path</c>, <c>ssh://[user@]host[:port]/path</c> or the scp-like
/// <c>[user@]host:path</c> form, or a local path.
/// </summary>
/// <param name="Path">Repository path without leading '/' (e.g. "atlas/atlas-api.git").</param>
public sealed record RemoteUrl(RemoteProtocol Protocol, string Host, int? Port, string Path, string? User, string Original)
{
    /// <summary>"atlas/atlas-api" (no ".git", no trailing '/').</summary>
    public string DisplayPath
    {
        get
        {
            var path = Path.TrimEnd('/');
            return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        }
    }

    /// <summary>Host with its port when there is one ("github.com", "git.example.com:8443").</summary>
    public string HostAndPort => Port is { } port ? string.Create(CultureInfo.InvariantCulture, $"{Host}:{port}") : Host;

    public bool IsHttp => Protocol is RemoteProtocol.Https or RemoteProtocol.Http;

    public static RemoteUrl? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return Local(text);
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var protocol = text[..scheme].ToLowerInvariant() switch
            {
                "https" => RemoteProtocol.Https,
                "http" => RemoteProtocol.Http,
                "ssh" or "git+ssh" or "ssh+git" => RemoteProtocol.Ssh,
                _ => (RemoteProtocol?)null,
            };
            if (protocol is null) return null;

            var rest = text[(scheme + 3)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var authority = slash < 0 ? rest : rest[..slash];
            var path = slash < 0 ? string.Empty : rest[(slash + 1)..];
            string? user = null;
            if (authority.LastIndexOf('@') is var at and >= 0)
            {
                user = authority[..at];
                authority = authority[(at + 1)..];
                // https://user:secret@host: never keep a password.
                if (user.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0) user = user[..colon];
            }

            int? port = null;
            if (authority.LastIndexOf(':') is var p and > 0
                && int.TryParse(authority.AsSpan(p + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                port = parsed;
                authority = authority[..p];
            }
            return authority.Length == 0 ? null : new RemoteUrl(protocol.Value, authority, port, path, NullIfEmpty(user), text);
        }

        // scp-like syntax: [user@]host:path, the colon before any slash; "C:\repo" and "C:/repo" are local paths.
        var colonIndex = text.IndexOf(':', StringComparison.Ordinal);
        var slashIndex = text.IndexOfAny(['/', '\\']);
        if (colonIndex > 1 && (slashIndex < 0 || colonIndex < slashIndex))
        {
            var authority = text[..colonIndex];
            string? user = null;
            if (authority.IndexOf('@', StringComparison.Ordinal) is var at and >= 0)
            {
                user = authority[..at];
                authority = authority[(at + 1)..];
            }
            return authority.Length == 0 ? null : new RemoteUrl(RemoteProtocol.Ssh, authority, null, text[(colonIndex + 1)..].TrimStart('/'), NullIfEmpty(user), text);
        }

        // "git@github.com" (SSH error messages): host only, no repository path.
        if (colonIndex < 0 && slashIndex < 0 && text.IndexOf('@', StringComparison.Ordinal) is var userEnd and > 0 && userEnd < text.Length - 1)
            return new RemoteUrl(RemoteProtocol.Ssh, text[(userEnd + 1)..], null, string.Empty, text[..userEnd], text);

        return Local(text);
    }

    /// <summary>SSH equivalent of an HTTP(S) URL ("git@github.com:atlas/atlas-api.git"); null otherwise.</summary>
    public string? ToSsh() => IsHttp && DisplayPath.Length > 0 ? $"git@{Host}:{DisplayPath}.git" : null;

    /// <summary>HTTPS equivalent of an SSH URL ("https://github.com/atlas/atlas-api.git"); null otherwise.</summary>
    public string? ToHttps() => Protocol == RemoteProtocol.Ssh && DisplayPath.Length > 0 ? $"https://{Host}/{DisplayPath}.git" : null;

    /// <summary>Same host and repository path, whatever the protocol (case-insensitive).</summary>
    public bool IsSameRepository(RemoteUrl other) =>
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && (DisplayPath.Length == 0 || other.DisplayPath.Length == 0
            || string.Equals(DisplayPath, other.DisplayPath, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Original;

    private static RemoteUrl Local(string text) => new(RemoteProtocol.Local, string.Empty, null, text, null, text);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
