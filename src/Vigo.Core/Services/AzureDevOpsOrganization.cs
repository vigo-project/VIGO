namespace Vigo.Core.Services;

/// <summary>Reads an Azure DevOps organization from what the user typed: its name or any URL of it.</summary>
public static class AzureDevOpsOrganization
{
    private const string Host = "dev.azure.com";
    private const string LegacySuffix = ".visualstudio.com";

    /// <summary>
    /// <c>contoso</c>, <c>https://dev.azure.com/contoso/Project/_git/repo</c>, <c>https://contoso@dev.azure.com/contoso</c> or
    /// <c>https://contoso.visualstudio.com</c> give <c>contoso</c>; null when nothing sensible can be read.
    /// </summary>
    public static string? Parse(string? input)
    {
        input = input?.Trim();
        if (string.IsNullOrEmpty(input)) return null;

        if (!input.Contains('/', StringComparison.Ordinal) && !input.Contains('.', StringComparison.Ordinal))
            return IsName(input) ? input : null;

        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        if (host == Host)
        {
            var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var name = first is null ? null : Uri.UnescapeDataString(first);
            return name is not null && IsName(name) ? name : null;
        }
        if (host.EndsWith(LegacySuffix, StringComparison.Ordinal))
        {
            var name = host[..^LegacySuffix.Length];
            return IsName(name) ? name : null;
        }
        return null;
    }

    /// <summary>Page where the user creates a personal access token (the account's organization list when none is known).</summary>
    public static string TokenPageUrl(string? organization) =>
        string.IsNullOrEmpty(organization)
            ? $"https://{Host}/"
            : $"https://{Host}/{Uri.EscapeDataString(organization)}/_usersSettings/tokens";

    private static bool IsName(string name) => name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}
