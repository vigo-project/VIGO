using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class SshPublicKeyTests
{
    // Throwaway keys; fingerprints as printed by "ssh-keygen -lf".
    private const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJgeFpJGRitqGw5g4dJMCCfMisERCDJzyecBm9EVOc8P test@vigo";

    private const string Rsa3072 =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC6b2HBHvONZpR4l2sfbFtMianP9TignCLSCiX8R6i63czjmtcy0D6T7AgwkVqfHa0hukOw3WAa2HQTUey37kFskY0DQxckg/tzbuSb326I3i5JBhwmM2zty7ccb7Jsvk75LCLJwHnx5ab6l7Qs57ltZzFnDr4xyXCuLKi2kfk7+OoLio8EnN5NXgb0DGM51rEGLyEQQhoN7NtIv0rywjy0ahcnzWfrXFE174U0Qt5R++1QwLw3+zzFTIAVzr94qqT/B7yyStlIntG29svx78RPj4JEhpXRtRknlvex3IzSTro7UxV6+GjQd1ZW0T8tGtQk3YcR/xY7INLfzg4aUv/IB3eQaUeq24F6ez/fX2ZyO+ao++CBwCzimu/mOesMswDJg8OqjWBWgRoQdy0TSTJLYnbJVjykbtocdopiU+ha72BoGj/DvWz3UHTV3QRCS01Q7aSY1thbJr+8TIXiw76RtQhBx51dyRWlEHkT4A9w+FP6856PPQJ6D8zR1vyQebk= rsa@vigo";

    private const string Ecdsa384 =
        "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBLrXXN458k4a14dEwntfyfvWn6XiQiSMCOL6QZN7j39ZTEXU/cejJCkff2n3//rR6gpg8buyLcUGN/kqNbzv45zjZLuta4DtK3h7ULPjFCh61KDreXZrpdRpq6ILoKWcrg==";

    [Fact]
    public void Ed25519_key_has_its_type_fingerprint_and_comment()
    {
        var key = SshPublicKey.Parse(Path.Combine(Path.GetTempPath(), "keys", "id_ed25519"), Ed25519 + "\r\n");

        Assert.NotNull(key);
        Assert.Equal("ED25519", key.Type);
        Assert.Equal("SHA256:6HbA6OOn3dQ0L2eoVnL1ax5ry4amK6zHhW3D86Dmn2c", key.Fingerprint);
        Assert.Equal("test@vigo", key.Comment);
        Assert.Equal("id_ed25519", key.Name);
        Assert.Equal(Ed25519, key.PublicKey);
    }

    [Fact]
    public void Rsa_key_type_carries_its_size()
    {
        var key = SshPublicKey.Parse(@"C:\keys\id_rsa", Rsa3072);

        Assert.Equal("RSA 3072", key?.Type);
        Assert.Equal("SHA256:h/d/oUVI01ssxzN19T52tBTKejJguS5yAPL7a8u3A38", key?.Fingerprint);
    }

    [Fact]
    public void Ecdsa_key_without_comment()
    {
        var key = SshPublicKey.Parse(@"C:\keys\id_ecdsa", Ecdsa384);

        Assert.Equal("ECDSA 384", key?.Type);
        Assert.Equal("SHA256:iILkwjljnm8D8tjYiw3bmI9uu4glJCbXqP+u7UH6Rio", key?.Fingerprint);
        Assert.Null(key?.Comment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!")]
    // Algorithm of the line different from the one in the blob.
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIJgeFpJGRitqGw5g4dJMCCfMisERCDJzyecBm9EVOc8P")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void Invalid_lines_are_rejected(string line) => Assert.Null(SshPublicKey.Parse(@"C:\keys\x", line));
}
