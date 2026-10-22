namespace Vigo.Core.Services;

/// <summary>
/// A settings file that could not be read (truncated, locked, written by a newer version) is about to be replaced by
/// defaults at the next save: keep a copy first, so nothing is lost for good (J9.4).
/// </summary>
internal static class JsonFileBackup
{
    public static void Keep(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked as well: nothing more to do, the next load tries again.
        }
    }
}
