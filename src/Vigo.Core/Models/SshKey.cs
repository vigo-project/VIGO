using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Vigo.Core.Models;

/// <summary>
/// An SSH key pair: private key file and the public key read from its <c>.pub</c> sibling (OpenSSH format).
/// </summary>
/// <param name="PrivateKeyPath">Full path of the private key (the file handed to <c>ssh -i</c>).</param>
/// <param name="Type">Short type shown in the list: "ED25519", "RSA 4096", "ECDSA 256"…</param>
/// <param name="Fingerprint">SHA256 fingerprint, as printed by <c>ssh-keygen -l</c> ("SHA256:…").</param>
/// <param name="PublicKey">Public key line (algorithm, base64 blob, comment), to paste on a hosting service.</param>
/// <param name="Comment">Comment of the public key (usually an e-mail address); null when absent.</param>
public sealed record SshKey(string PrivateKeyPath, string Type, string Fingerprint, string PublicKey, string? Comment)
{
    /// <summary>File name of the private key ("id_ed25519").</summary>
    public string Name => Path.GetFileName(PrivateKeyPath);
}

/// <summary>Reads OpenSSH public key lines (<c>ssh-ed25519 AAAA… comment</c>).</summary>
public static class SshPublicKey
{
    /// <summary>Key of <paramref name="privateKeyPath"/> described by the public key line, or null when unreadable.</summary>
    public static SshKey? Parse(string privateKeyPath, string publicKeyLine)
    {
        var parts = publicKeyLine.Trim().Split((char[])[' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (ReadString(blob, 0, out var next) is not { } algorithm || algorithm != parts[0]) return null;

        var type = algorithm switch
        {
            "ssh-ed25519" => "ED25519",
            "sk-ssh-ed25519@openssh.com" => "ED25519-SK",
            "ssh-rsa" => RsaBits(blob, next) is { } bits ? $"RSA {bits}" : "RSA",
            "ssh-dss" => "DSA",
            "sk-ecdsa-sha2-nistp256@openssh.com" => "ECDSA-SK",
            _ when algorithm.StartsWith("ecdsa-sha2-nistp", StringComparison.Ordinal) => $"ECDSA {algorithm["ecdsa-sha2-nistp".Length..]}",
            _ => algorithm,
        };
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        var comment = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null;
        return new SshKey(privateKeyPath, type, fingerprint, string.Join(' ', parts.Select(p => p.Trim())), comment);
    }

    // RSA blob: string "ssh-rsa", mpint e, mpint n; the size of the key is the bit length of n.
    private static int? RsaBits(byte[] blob, int offset)
    {
        if (ReadBytes(blob, offset, out offset) is null || ReadBytes(blob, offset, out _) is not { } modulus) return null;
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        return n.IsZero ? null : (int)n.GetBitLength();
    }

    private static string? ReadString(byte[] blob, int offset, out int next) =>
        ReadBytes(blob, offset, out next) is { } bytes ? System.Text.Encoding.ASCII.GetString(bytes) : null;

    private static byte[]? ReadBytes(byte[] blob, int offset, out int next)
    {
        next = offset;
        if (blob.Length - offset < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
        if (length > (uint)(blob.Length - offset - 4)) return null;
        next = offset + 4 + (int)length;
        return blob.AsSpan(offset + 4, (int)length).ToArray();
    }
}
