using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Vigo.Core.Models;

/// <summary>
/// Git object id (SHA-1, 20 bytes, or SHA-256, 32 bytes) stored inline without any heap allocation.
/// Used as a dictionary key by the graph, hence the cheap hash (the id is already uniformly distributed).
/// </summary>
public readonly struct GitSha : IEquatable<GitSha>, ISpanFormattable
{
    public const int Sha1Length = 20;
    public const int Sha256Length = 32;

    private readonly ulong _a, _b, _c, _d;
    private readonly byte _length;

    private GitSha(ReadOnlySpan<byte> bytes)
    {
        Span<byte> buffer = stackalloc byte[Sha256Length];
        buffer.Clear();
        bytes.CopyTo(buffer);
        _a = BinaryPrimitives.ReadUInt64BigEndian(buffer);
        _b = BinaryPrimitives.ReadUInt64BigEndian(buffer[8..]);
        _c = BinaryPrimitives.ReadUInt64BigEndian(buffer[16..]);
        _d = BinaryPrimitives.ReadUInt64BigEndian(buffer[24..]);
        _length = (byte)bytes.Length;
    }

    /// <summary>Raw length in bytes (20 or 32); 0 for <c>default</c>.</summary>
    public int ByteLength => _length;

    public bool IsEmpty => _length == 0;

    /// <summary>Parses a full hexadecimal id (40 or 64 characters, either case).</summary>
    public static GitSha Parse(ReadOnlySpan<char> hex) =>
        TryParse(hex, out var sha) ? sha : throw new FormatException($"Invalid Git object id: '{hex}'.");

    public static bool TryParse(ReadOnlySpan<char> hex, out GitSha sha)
    {
        Span<byte> bytes = stackalloc byte[Sha256Length];
        if (!TryDecode(hex, bytes, out var length)) { sha = default; return false; }
        sha = new GitSha(bytes[..length]);
        return true;
    }

    /// <summary>Parses a full hexadecimal id from UTF-8/ASCII bytes (git.exe output).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Hex, out GitSha sha)
    {
        Span<byte> bytes = stackalloc byte[Sha256Length];
        if (!TryDecode(utf8Hex, bytes, out var length)) { sha = default; return false; }
        sha = new GitSha(bytes[..length]);
        return true;
    }

    /// <summary>Abbreviated hexadecimal form (7 characters by default).</summary>
    public string ToShortString(int length = 7) => ToString()[..Math.Min(length, _length * 2)];

    public override string ToString()
    {
        if (_length == 0) return string.Empty;
        Span<byte> bytes = stackalloc byte[Sha256Length];
        WriteBytes(bytes);
        return Convert.ToHexStringLower(bytes[.._length]);
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<byte> bytes = stackalloc byte[Sha256Length];
        WriteBytes(bytes);
        return Convert.TryToHexStringLower(bytes[.._length], destination, out charsWritten);
    }

    public bool Equals(GitSha other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d && _length == other._length;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is GitSha other && Equals(other);

    public override int GetHashCode() => (int)(_a >> 32);

    public static bool operator ==(GitSha left, GitSha right) => left.Equals(right);
    public static bool operator !=(GitSha left, GitSha right) => !left.Equals(right);

    private void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _d);
    }

    private static bool TryDecode<TChar>(ReadOnlySpan<TChar> hex, Span<byte> destination, out int length)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        length = hex.Length / 2;
        if (hex.Length != Sha1Length * 2 && hex.Length != Sha256Length * 2) return false;
        for (var i = 0; i < length; i++)
        {
            var hi = HexValue(int.CreateTruncating(hex[2 * i]));
            var lo = HexValue(int.CreateTruncating(hex[2 * i + 1]));
            if ((hi | lo) < 0) return false;
            destination[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
