using System.Globalization;

namespace Vigo.Core.Services;

/// <summary>A size in bytes as shown to the user ("12 B", "1.5 KB", "3 MB"), localized (units and decimal separator).</summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => Loc.Format("ImageDiff.Size.Bytes", bytes),
        < 1024 * 1024 => Loc.Format("ImageDiff.Size.Kilo", (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture)),
        _ => Loc.Format("ImageDiff.Size.Mega", (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.CurrentCulture)),
    };
}
