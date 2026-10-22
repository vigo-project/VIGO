using System.Text;

namespace Vigo.Git.Cli;

/// <summary>
/// Decodes the bytes of a file or of git output: UTF-8 first, then the ANSI code page of the system (GBK on a Chinese
/// Windows, Windows-1252 on a Western one) for content that is not valid UTF-8, instead of turning it into U+FFFD.
/// Read side only: what VIGO writes back (patches, saved files) stays UTF-8.
/// </summary>
public static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding? Ansi = CreateAnsi();

    private static Encoding? CreateAnsi()
    {
        try
        {
            // Code pages other than the Unicode ones are only available through this provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var page = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return page is 0 or 65001 ? null : Encoding.GetEncoding(page, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The encoding a <c>gui.encoding</c> value names (J7.29): "system" is the ANSI code page; null for an empty or unknown name, which
    /// leaves the automatic detection.
    /// </summary>
    public static Encoding? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Reading Ansi first runs the type's initializers, which register the code pages the named encoding may need.
        var ansi = Ansi;
        if (name.Trim().Equals("system", StringComparison.OrdinalIgnoreCase)) return ansi;
        try
        {
            return Encoding.GetEncoding(name.Trim(), EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, out _);

    /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, out bool, Encoding?)"/>
    public static string Decode(ReadOnlySpan<byte> bytes, out bool usedFallback) => Decode(bytes, out usedFallback, null);

    /// <param name="usedFallback">True when the bytes were not UTF-8 and went through the ANSI code page: re-encoding the text as UTF-8 would not give the original bytes.</param>
    /// <param name="preferred">Encoding chosen for the repository (J7.29): used, after the byte order mark, instead of the detection.</param>
    public static string Decode(ReadOnlySpan<byte> bytes, out bool usedFallback, Encoding? preferred)
    {
        usedFallback = false;
        // Byte order marks first, as File.ReadAllText does (the UTF-8 one is dropped).
        if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes[2..]);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        if (preferred is not null)
        {
            usedFallback = preferred.CodePage != Encoding.UTF8.CodePage;
            return preferred.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            usedFallback = true;
            return (Ansi ?? Encoding.UTF8).GetString(bytes);
        }
    }
}
