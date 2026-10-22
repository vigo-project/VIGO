using System.Globalization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.Git.Ssh;

namespace Vigo.Git.Signing;

/// <summary>
/// Lists the OpenPGP secret keys with <c>gpg --list-secret-keys --with-colons</c>, run with the same program git signs
/// with (<see cref="SigningProgram"/>, ADR-036) so that the keys shown are the ones git can use.
/// </summary>
public sealed class GpgKeyService(GitCli cli) : IGpgKeyService
{
    public async Task<IReadOnlyList<GpgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default)
    {
        var program = await ResolveProgramAsync(cancellationToken);
        if (program is null) return [];

        var run = await SshTools.RunAsync(program, ["--batch", "--no-tty", "--list-secret-keys", "--keyid-format=long", "--with-colons"], cancellationToken);
        return run is { ExitCode: 0 } ? GpgKeyParser.Parse(run.StandardOutput, DateTimeOffset.UtcNow) : [];
    }

    public async Task<GitResult<GpgKey>> GenerateAsync(string name, string email, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        email = email.Trim();
        if (name.Length == 0 || email.Length == 0) return new Failure(Loc.Get("GpgKeys.Service.IdentityMissing"));
        if (await ResolveProgramAsync(cancellationToken) is not { } program) return new Failure(Loc.Get("GpgKeys.Service.GpgMissing"));

        var before = (await ListSecretKeysAsync(cancellationToken)).Select(k => k.KeyId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Loopback pinentry with an empty passphrase: no dialog, like the SSH keys VIGO generates.
        var run = await SshTools.RunAsync(program,
            ["--batch", "--no-tty", "--pinentry-mode", "loopback", "--passphrase", "", "--quick-generate-key", $"{name} <{email}>", "ed25519", "sign", "2y"],
            cancellationToken);
        if (run is null) return Canceled.Instance;
        if (run.ExitCode != 0)
        {
            var error = (run.StandardError.Trim().Length > 0 ? run.StandardError : run.StandardOutput).Trim();
            return new Failure(Loc.Get("GpgKeys.Service.GenerateFailed"), run.ExitCode, Details: error.Length > 0 ? error : null);
        }

        var created = (await ListSecretKeysAsync(cancellationToken)).FirstOrDefault(k => !before.Contains(k.KeyId));
        return created is not null ? new Success<GpgKey>(created) : new Failure(Loc.Get("GpgKeys.Service.NotFoundAfter"));
    }

    public async Task<GitResult<string>> ExportPublicKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (await ResolveProgramAsync(cancellationToken) is not { } program) return new Failure(Loc.Get("GpgKeys.Service.GpgMissing"));

        var run = await SshTools.RunAsync(program, ["--batch", "--no-tty", "--armor", "--export", keyId], cancellationToken);
        if (run is null) return Canceled.Instance;
        return run.ExitCode == 0 && run.StandardOutput.Trim().Length > 0
            ? new Success<string>(run.StandardOutput.Trim())
            : new Failure(Loc.Get("GpgKeys.Service.ExportFailed"), run.ExitCode, Details: run.StandardError.Trim().Length > 0 ? run.StandardError.Trim() : null);
    }

    private async Task<string?> ResolveProgramAsync(CancellationToken cancellationToken)
    {
        var configured = await cli.RunAsync(null, ["config", "--global", "--get", "gpg.program"], 1, cancellationToken);
        var value = configured.Value is Success<CliOutput> { Value.StandardOutput: var text } ? text.Trim() : string.Empty;

        // An explicit path is used as is; a bare name (or none) is resolved on the Windows PATH, MSYS builds excluded.
        return value.IndexOfAny(['/', '\\', ':']) >= 0 ? (File.Exists(value) ? value : null) : SigningProgram.Resolve(value.Length == 0 ? null : value);
    }
}

/// <summary>Reads the machine-readable listing of GnuPG (<c>doc/DETAILS</c>: one record per line, fields split on colons).</summary>
internal static class GpgKeyParser
{
    /// <summary>
    /// The usable keys: a <c>sec</c> record, its key id, its first user id; expired, revoked and disabled keys are skipped.
    /// </summary>
    public static IReadOnlyList<GpgKey> Parse(string output, DateTimeOffset now)
    {
        List<GpgKey> keys = [];
        string? keyId = null;
        var userId = string.Empty;
        DateTimeOffset? expires = null;
        var usable = false;

        void Flush()
        {
            if (keyId is not null && usable) keys.Add(new GpgKey(keyId, userId, expires));
            keyId = null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(':');
            switch (fields[0])
            {
                case "sec" when fields.Length > 6 && fields[4].Length > 0:
                    Flush();
                    keyId = fields[4];
                    userId = string.Empty;
                    expires = long.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                        : null;
                    usable = fields[1] is not ("e" or "r" or "d") && (expires is null || expires > now);
                    break;
                case "uid" when keyId is not null && userId.Length == 0 && fields.Length > 9:
                    // GnuPG escapes the colon (and the characters below 0x20) as \xNN.
                    userId = fields[9].Replace("\\x3a", ":", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }
        Flush();
        return keys;
    }
}
