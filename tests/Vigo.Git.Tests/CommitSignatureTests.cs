using System.Diagnostics;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Signature verification of commit details, with a real SSH-signed commit (no GPG keyring needed).</summary>
public sealed class CommitSignatureTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool GenerateKey(string keyPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", keyPath])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(30_000);
            return process is { ExitCode: 0 } && File.Exists(keyPath + ".pub");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<CommitSignature?> SignedCommitSignatureAsync(bool trustKey)
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a"));
        var keyPath = Path.Combine(repo.Path, "..", $"sign-{Guid.NewGuid():N}");
        if (!GenerateKey(keyPath)) return null;
        try
        {
            var signers = keyPath + ".signers";
            File.WriteAllText(signers, trustKey ? $"vigo@example.com {File.ReadAllText(keyPath + ".pub")}" : string.Empty);
            repo.Git("config", "gpg.format", "ssh");
            // The service reads the user's global config, which may blank this setting out (an empty program breaks verification).
            repo.Git("config", "gpg.ssh.program", "ssh-keygen");
            repo.Git("config", "user.signingkey", keyPath + ".pub");
            repo.Git("config", "gpg.ssh.allowedSignersFile", signers);
            File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "b");
            repo.Git("add", "-A");
            repo.Git("commit", "-S", "-q", "-m", "signed");
            var sha = repo.Git("rev-parse", "HEAD").Trim();

            var result = await _sut.GetCommitAsync(repo.Path, GitSha.Parse(sha), Ct);
            return Assert.IsType<Success<CommitDetails>>(result.Value).Value.Signature;
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(keyPath)!, Path.GetFileName(keyPath) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task A_commit_signed_with_a_trusted_ssh_key_is_good()
    {
        var signature = await SignedCommitSignatureAsync(trustKey: true);
        if (signature is null) Assert.Skip("ssh-keygen is not available.");

        Assert.Equal(SignatureStatus.Good, signature.Status);
        Assert.StartsWith("SHA256:", signature.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_signed_with_an_unlisted_ssh_key_is_not_good()
    {
        var signature = await SignedCommitSignatureAsync(trustKey: false);
        if (signature is null) Assert.Skip("ssh-keygen is not available.");

        Assert.True(signature.IsSigned);
        Assert.NotEqual(SignatureStatus.Good, signature.Status);
    }
}
