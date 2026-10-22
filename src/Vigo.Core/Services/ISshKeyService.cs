using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Services;

/// <summary>State of the Windows OpenSSH authentication agent (service "ssh-agent").</summary>
public enum SshAgentState { Unknown, Running, Stopped, Disabled, NotInstalled }

/// <summary>SSH keys of the user (preferences, "Clés SSH" section).</summary>
public interface ISshKeyService
{
    /// <summary>Folder of the user's keys (<c>%USERPROFILE%\.ssh</c>).</summary>
    string KeyDirectory { get; }

    /// <summary>
    /// Key pairs of <see cref="KeyDirectory"/> (every <c>*.pub</c> next to its private key) followed by the
    /// <paramref name="imported"/> ones still readable, without duplicates.
    /// </summary>
    Task<IReadOnlyList<SshKey>> ListAsync(IEnumerable<string> imported, CancellationToken cancellationToken = default);

    /// <summary>Key pair of a private key file chosen by the user; Failure when its <c>.pub</c> is missing or unreadable.</summary>
    Task<GitResult<SshKey>> ReadAsync(string privateKeyPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an ED25519 key without passphrase in <see cref="KeyDirectory"/> (<c>id_ed25519</c>, or
    /// <c>id_ed25519_vigo</c>, <c>id_ed25519_vigo2</c>… when taken), commented with the global <c>user.email</c>
    /// (else <c>user@machine</c>).
    /// </summary>
    Task<GitResult<SshKey>> GenerateAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the private key file and its <c>.pub</c> sibling from disk (irreversible); Failure when a file cannot be deleted.</summary>
    Task<GitResult> DeleteAsync(string privateKeyPath, CancellationToken cancellationToken = default);

    Task<SshAgentState> GetAgentStateAsync(CancellationToken cancellationToken = default);
}
