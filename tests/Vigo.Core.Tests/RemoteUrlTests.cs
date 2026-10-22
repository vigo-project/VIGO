using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class RemoteUrlTests
{
    [Theory]
    [InlineData("https://github.com/atlas/atlas-api.git", RemoteProtocol.Https, "github.com", null, "atlas/atlas-api", null)]
    [InlineData("https://camille:s3cret@git.example.com:8443/team/app", RemoteProtocol.Https, "git.example.com", 8443, "team/app", "camille")]
    [InlineData("http://127.0.0.1:5000/team/project.git/", RemoteProtocol.Http, "127.0.0.1", 5000, "team/project", null)]
    [InlineData("git@github.com:atlas/atlas-api.git", RemoteProtocol.Ssh, "github.com", null, "atlas/atlas-api", "git")]
    [InlineData("ssh://git@ssh.dev.azure.com:22/v3/org/project/repo", RemoteProtocol.Ssh, "ssh.dev.azure.com", 22, "v3/org/project/repo", "git")]
    [InlineData("git@github.com", RemoteProtocol.Ssh, "github.com", null, "", "git")]
    public void Parses_remote_urls(string text, RemoteProtocol protocol, string host, int? port, string path, string? user)
    {
        var url = RemoteUrl.TryParse(text);

        Assert.NotNull(url);
        Assert.Equal((protocol, host, port, path, user), (url.Protocol, url.Host, url.Port, url.DisplayPath, url.User));
    }

    [Theory]
    [InlineData(@"C:\repos\origin.git")]
    [InlineData("C:/repos/origin.git")]
    [InlineData("../origin.git")]
    [InlineData("file:///C:/repos/origin.git")]
    public void Local_paths_are_not_mistaken_for_ssh(string text) => Assert.Equal(RemoteProtocol.Local, RemoteUrl.TryParse(text)!.Protocol);

    [Fact]
    public void Https_and_ssh_convert_both_ways()
    {
        var https = RemoteUrl.TryParse("https://github.com/atlas/atlas-api.git")!;
        var ssh = RemoteUrl.TryParse("git@github.com:atlas/atlas-api.git")!;

        Assert.Equal("git@github.com:atlas/atlas-api.git", https.ToSsh());
        Assert.Equal("https://github.com/atlas/atlas-api.git", ssh.ToHttps());
        Assert.Null(https.ToHttps());
        Assert.True(https.IsSameRepository(ssh));
        // Host only (as git prints it for a refused SSH key): same host, any repository.
        Assert.True(RemoteUrl.TryParse("git@GitHub.com")!.IsSameRepository(ssh));
        Assert.False(https.IsSameRepository(RemoteUrl.TryParse("https://github.com/atlas/other.git")!));
    }

    [Fact]
    public void Password_in_the_url_is_never_kept()
    {
        var url = RemoteUrl.TryParse("https://camille:s3cret@github.com/atlas/api.git")!;

        Assert.Equal("camille", url.User);
        Assert.DoesNotContain("s3cret", url.ToSsh(), StringComparison.Ordinal);
    }
}
