namespace Vigo.TestUtilities;

/// <summary>Paths written the Windows way in the tests, turned into paths of the platform the tests run on.</summary>
public static class TestPaths
{
    /// <summary>A rooted Windows path (<c>D:\work\api</c>) unchanged on Windows; elsewhere without its drive and with '/' (<c>/work/api</c>).</summary>
    public static string Native(string windowsPath) =>
        OperatingSystem.IsWindows() ? windowsPath : windowsPath[2..].Replace('\\', '/');
}
