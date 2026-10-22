using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Services;

/// <summary>OpenPGP keys the user can sign with (preferences, "Profils Git" section).</summary>
public interface IGpgKeyService
{
    /// <summary>
    /// Secret keys of the Windows GnuPG keyring (the one git signs with, ADR-036), expired and revoked ones left out.
    /// Empty without GnuPG or without keys.
    /// </summary>
    Task<IReadOnlyList<GpgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an ED25519 signing key without passphrase in the keyring for <c>name &lt;email&gt;</c> (expires after two years);
    /// Failure when GnuPG is missing or refuses.
    /// </summary>
    Task<GitResult<GpgKey>> GenerateAsync(string name, string email, CancellationToken cancellationToken = default);

    /// <summary>ASCII-armored public key of <paramref name="keyId"/>, to paste on a hosting service.</summary>
    Task<GitResult<string>> ExportPublicKeyAsync(string keyId, CancellationToken cancellationToken = default);
}
