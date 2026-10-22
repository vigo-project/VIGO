namespace Vigo.Core.Services;

/// <summary>Secure storage of a named secret (Windows Credential Locker), e.g. the API key of the AI commit message.</summary>
public interface ISecretVault
{
    string? Retrieve(string name);

    void Store(string name, string secret);

    void Remove(string name);
}

/// <summary>Names of the secrets VIGO keeps.</summary>
public static class SecretNames
{
    public const string AnthropicApiKey = "anthropic-api-key";
}
