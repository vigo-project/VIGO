using Vigo.Core.Models;
using Vigo.Git.Commits;

namespace Vigo.Git.Tests;

public sealed class CommitSignatureParsingTests
{
    [Theory]
    [InlineData("G", SignatureStatus.Good)]
    [InlineData("B", SignatureStatus.Bad)]
    [InlineData("U", SignatureStatus.UnknownValidity)]
    [InlineData("X", SignatureStatus.Expired)]
    [InlineData("Y", SignatureStatus.ExpiredKey)]
    [InlineData("R", SignatureStatus.Revoked)]
    [InlineData("E", SignatureStatus.MissingKey)]
    [InlineData("N", SignatureStatus.None)]
    [InlineData("", SignatureStatus.None)]
    [InlineData("?", SignatureStatus.Error)]
    public void Status_letter_maps_to_a_status(string code, SignatureStatus expected) =>
        Assert.Equal(expected, CommitCliReader.ParseSignature(code, "Ada", "ABCD").Status);

    [Fact]
    public void Signer_and_key_are_kept_for_a_signed_commit()
    {
        var signature = CommitCliReader.ParseSignature("G", " Ada <ada@example.com> ", "ABCD1234");

        Assert.True(signature.IsSigned);
        Assert.Equal("Ada <ada@example.com>", signature.Signer);
        Assert.Equal("ABCD1234", signature.Key);
    }

    [Fact]
    public void Unsigned_commit_has_no_signer()
    {
        var signature = CommitCliReader.ParseSignature("N", "ignored", "ignored");

        Assert.False(signature.IsSigned);
        Assert.Equal(string.Empty, signature.Signer);
    }

    [Fact]
    public void Show_output_carries_the_signature_before_the_message()
    {
        var sha = new string('a', 40);
        var output = string.Join('\0', sha, "", "Ada", "a@x", "1700000000", "2023-11-14 22:13:20 +0000",
            "Ada", "a@x", "1700000000", "2023-11-14 22:13:20 +0000", "G", "Ada", "KEY1", "subject\n\nbody\n");

        var (_, _, _, _, message, signature) = CommitCliReader.ParseShow(output);

        Assert.Equal("subject\n\nbody", message);
        Assert.Equal(SignatureStatus.Good, signature.Status);
        Assert.Equal("KEY1", signature.Key);
    }
}
