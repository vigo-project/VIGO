using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Ssh;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class SshTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _keys = Directory.CreateTempSubdirectory("vigo-ssh-").FullName;

    public void Dispose() => Directory.Delete(_keys, recursive: true);

    [Fact]
    public void Ssh_command_passes_only_the_chosen_key()
    {
        Assert.Null(SshCommand.Build(new SshSettings(null, false), @"C:\Windows\System32\OpenSSH\ssh.exe"));
        Assert.Equal("ssh -i 'C:/Users/me/.ssh/id_ed25519' -o IdentitiesOnly=yes",
            SshCommand.Build(new SshSettings(@"C:\Users\me\.ssh\id_ed25519", false), @"C:\Windows\System32\OpenSSH\ssh.exe"));
    }

    [Fact]
    public void Windows_agent_runs_the_Windows_client_when_installed()
    {
        Assert.Equal("'C:/Windows/System32/OpenSSH/ssh.exe'",
            SshCommand.Build(new SshSettings(null, true), @"C:\Windows\System32\OpenSSH\ssh.exe"));
        Assert.Equal("'C:/Windows/System32/OpenSSH/ssh.exe' -i 'C:/k/it'\\''s key' -o IdentitiesOnly=yes",
            SshCommand.Build(new SshSettings(@"C:\k\it's key", true), @"C:\Windows\System32\OpenSSH\ssh.exe"));
        // Not installed: Git's client, nothing to change without a key.
        Assert.Null(SshCommand.Build(new SshSettings(null, true), null));
    }

    [Fact]
    public async Task Network_commands_get_the_ssh_command_unless_core_sshCommand_is_set()
    {
        using var repo = TestRepoBuilder.Create();
        var cli = new GitCli { SshSettings = () => new SshSettings(@"C:\k\id_ed25519", false) };

        var push = await cli.PrepareAsync(repo.Path, ["-c", "credential.helper=", "push", "origin"], Ct);
        var status = await cli.PrepareAsync(repo.Path, ["status"], Ct);
        Assert.Equal("ssh -i 'C:/k/id_ed25519' -o IdentitiesOnly=yes", push.SshCommand);
        Assert.Null(status.SshCommand);

        repo.Git("config", "core.sshCommand", "ssh -i other");
        var withUserCommand = await cli.PrepareAsync(repo.Path, ["fetch", "--all"], Ct);
        Assert.Null(withUserCommand.SshCommand);
    }

    [Fact]
    public async Task Keys_of_the_folder_and_imported_ones_are_listed_once()
    {
        File.WriteAllText(Path.Combine(_keys, "id_ed25519"), "private");
        File.WriteAllText(Path.Combine(_keys, "id_ed25519.pub"), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJgeFpJGRitqGw5g4dJMCCfMisERCDJzyecBm9EVOc8P a@b\n");
        // Public key alone (no private key) and unrelated files: ignored.
        File.WriteAllText(Path.Combine(_keys, "orphan.pub"), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJgeFpJGRitqGw5g4dJMCCfMisERCDJzyecBm9EVOc8P");
        File.WriteAllText(Path.Combine(_keys, "known_hosts"), "github.com ssh-ed25519 AAAA");
        var other = Directory.CreateDirectory(Path.Combine(_keys, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "work"), "private");
        File.WriteAllText(Path.Combine(other, "work.pub"), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJgeFpJGRitqGw5g4dJMCCfMisERCDJzyecBm9EVOc8P work");
        var sut = new SshKeyService(new GitCli(), _keys);

        var keys = await sut.ListAsync([Path.Combine(other, "work"), Path.Combine(_keys, "id_ed25519"), Path.Combine(other, "missing")], Ct);

        Assert.Equal(["id_ed25519", "work"], keys.Select(k => k.Name));
        Assert.Equal("work", keys[1].Comment);
    }

    [Fact]
    public async Task Importing_needs_the_public_key_next_to_the_private_one()
    {
        File.WriteAllText(Path.Combine(_keys, "lonely"), "private");
        var sut = new SshKeyService(new GitCli(), _keys);

        var result = await sut.ReadAsync(Path.Combine(_keys, "lonely"), Ct);

        Assert.Equal("Clé publique introuvable : lonely.pub doit être à côté de la clé privée.", Assert.IsType<Failure>(result.Value).Message);
    }

    [Fact, Trait("Platform", "Windows")]
    public async Task Generated_key_does_not_overwrite_an_existing_one()
    {
        // ssh-keygen.exe is looked for in Windows OpenSSH or Git for Windows only.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ssh-keygen.exe is located on Windows only.");
        File.WriteAllText(Path.Combine(_keys, "id_ed25519"), "existing");
        var sut = new SshKeyService(new GitCli(), _keys);

        var result = await sut.GenerateAsync(Ct);

        var key = Assert.IsType<Success<SshKey>>(result.Value).Value;
        Assert.Equal("id_ed25519_vigo", key.Name);
        Assert.Equal("ED25519", key.Type);
        Assert.StartsWith("SHA256:", key.Fingerprint, StringComparison.Ordinal);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_keys, "id_ed25519")));
    }
}
