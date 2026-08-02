namespace OsuMapManager.Tester;

public static class TesterConfig
{
    /// <summary>
    /// Set this to your osu! lazer install path to skip the path prompt in all tests.
    /// Leave empty to be prompted each time.
    /// </summary>
    public static string OsuInstallPath { get; set; } = "";

    /// <summary>
    /// Returns the configured osu! path, or prompts the user if not set.
    /// </summary>
    public static string GetOsuPath()
    {
        if (!string.IsNullOrEmpty(OsuInstallPath))
        {
            Console.WriteLine($"osu! path: {OsuInstallPath}");
            return OsuInstallPath;
        }
        return (Console.ReadLine()?.Trim() ?? "").Trim('"');
    }
}
