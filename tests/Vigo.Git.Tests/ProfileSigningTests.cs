using System.Diagnostics;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Signing;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Signing through a Git profile (J7.7, ADR-068): the format and the key reach git.exe as -c settings; GnuPG listing.</summary>
public sealed class ProfileSigningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Profile_signs_commits_with_its_ssh_key_whatever_the_configured_format()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a"));
        var keyPath = Path.Combine(repo.Path, "..", $"profile-sign-{Guid.NewGuid():N}");
        try
        {
            if (!GenerateKey(keyPath)) Assert.Skip("ssh-keygen is not available.");
            // The repository says nothing about signing (the global config may blank gpg.ssh.program out: an empty program breaks it).
            repo.Git("config", "gpg.ssh.program", "ssh-keygen");
            repo.WriteFile("a.txt", "b").Stage("a.txt");
            var profile = new GitProfile("p", "Perso", "Ada", "ada@example.com", keyPath + ".pub", SignCommits: true, SigningFormat: SigningFormats.Ssh);
            var git = new GitService(new GitCli { ProfileFor = _ => profile });

            var result = await git.CommitAsync(repo.Path, new CommitRequest("Signed by the profile\n"), Ct);

            Assert.IsType<Success<GitSha>>(result.Value, exactMatch: true);
            var commit = repo.Git("cat-file", "-p", "HEAD");
            Assert.Contains("gpgsig -----BEGIN SSH SIGNATURE-----", commit, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(keyPath)!, Path.GetFileName(keyPath) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task Profile_without_signing_leaves_the_commit_unsigned()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a"));
        repo.WriteFile("a.txt", "b").Stage("a.txt");
        var profile = new GitProfile("p", "Perso", "Ada", "ada@example.com", SigningFormat: SigningFormats.Ssh);
        var git = new GitService(new GitCli { ProfileFor = _ => profile });

        await git.CommitAsync(repo.Path, new CommitRequest("Not signed\n"), Ct);

        Assert.DoesNotContain("gpgsig", repo.Git("cat-file", "-p", "HEAD"), StringComparison.Ordinal);
    }

    [Fact]
    public void Gpg_listing_gives_key_id_user_id_and_expiry()
    {
        const string output = """
            sec:u:255:22:AAAABBBBCCCCDDDD:1700000000:4102444800::u:::scESC:::+::ed25519:::0:
            fpr:::::::::0123456789ABCDEF0123456789ABCDEFAAAABBBBCCCCDDDD:
            grp:::::::::FEDCBA9876543210FEDCBA9876543210FEDCBA98:
            uid:u::::1700000000::HASH1::Ada Lovelace <ada@example.org>::::::::::0:
            uid:u::::1700000001::HASH2::Second <second@example.org>::::::::::0:
            ssb:u:255:18:1111222233334444:1700000000:4102444800:::::e::::::cv25519::
            sec:u:255:22:5555666677778888:1700000000:::u:::scESC:::+::ed25519:::0:
            uid:u::::1700000000::HASH3::Colon\x3a Name <c@example.org>::::::::::0:
            """;

        var keys = GpgKeyParser.Parse(output, Now);

        Assert.Equal(2, keys.Count);
        Assert.Equal("AAAABBBBCCCCDDDD", keys[0].KeyId);
        Assert.Equal("Ada Lovelace <ada@example.org>", keys[0].UserId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), keys[0].Expires);
        Assert.Equal("5555666677778888", keys[1].KeyId);
        Assert.Equal("Colon: Name <c@example.org>", keys[1].UserId);
        Assert.Null(keys[1].Expires);
    }

    [Fact]
    public void Gpg_listing_skips_expired_revoked_and_disabled_keys()
    {
        const string output = """
            sec:e:255:22:1111111111111111:1500000000:1600000000::u:::sc:::+::ed25519:::0:
            uid:e::::1500000000::H::Expired <e@example.org>::::::::::0:
            sec:u:255:22:2222222222222222:1500000000:1600000000::u:::sc:::+::ed25519:::0:
            uid:u::::1500000000::H::Past expiry, not yet flagged <p@example.org>::::::::::0:
            sec:r:255:22:3333333333333333:1500000000:::u:::sc:::+::ed25519:::0:
            sec:d:255:22:4444444444444444:1500000000:::u:::sc:::+::ed25519:::0:
            sec:u:255:22:5555555555555555:1500000000:::u:::sc:::+::ed25519:::0:
            """;

        var keys = GpgKeyParser.Parse(output, Now);

        var key = Assert.Single(keys);
        Assert.Equal("5555555555555555", key.KeyId);
        Assert.Equal(string.Empty, key.UserId);
    }

    [Fact]
    public void Gpg_listing_of_nothing_is_empty() => Assert.Empty(GpgKeyParser.Parse(string.Empty, Now));

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
}
