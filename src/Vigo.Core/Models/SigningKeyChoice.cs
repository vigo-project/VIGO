namespace Vigo.Core.Models;

/// <summary>Signature formats of <c>gpg.format</c> a Git profile can impose (J7.7, ADR-068).</summary>
public static class SigningFormats
{
    public const string OpenPgp = "openpgp";
    public const string Ssh = "ssh";
    public const string X509 = "x509";

    /// <summary>The value when it is a known format (case-insensitive), else null: the Git configuration decides.</summary>
    public static string? Normalize(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        OpenPgp => OpenPgp,
        Ssh => Ssh,
        X509 => X509,
        _ => null,
    };
}

/// <summary>An OpenPGP secret key found by <see cref="Services.IGpgKeyService"/>.</summary>
/// <param name="KeyId">Long key id (16 hex digits), what <c>user.signingkey</c> takes.</param>
/// <param name="UserId">First user id ("Name &lt;mail&gt;"); empty when the key has none.</param>
/// <param name="Expires">Expiry date; null when the key does not expire.</param>
public sealed record GpgKey(string KeyId, string UserId, DateTimeOffset? Expires);

/// <summary>A key proposed by "Choisir une clé…" in a Git profile.</summary>
/// <param name="Format">Format the key belongs to (<see cref="SigningFormats"/>): picking it sets the profile's format too.</param>
/// <param name="Value">What goes into <c>user.signingkey</c>: the key id, or the path of the SSH public key.</param>
/// <param name="Label">Line shown in the menu.</param>
public sealed record SigningKeyChoice(string Format, string Value, string Label);
