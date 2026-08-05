using System;
using System.IO;
using System.Reflection;

namespace OsuMapManager.Helpers;

/// <summary>
/// Reads the application version from an embedded VERSION file at runtime.
/// </summary>
public static class AppVersion
{
    private static string? _current;

    /// <summary>
    /// The current version string, e.g. "v0.1.0". Read from the embedded
    /// VERSION resource, which is kept in sync with the VERSION file at
    /// the repository root.
    /// </summary>
    public static string Current
    {
        get
        {
            if (_current != null)
                return _current;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("OsuMapManager.VERSION");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var version = reader.ReadToEnd().Trim();
                    _current = version.StartsWith('v') ? version : "v" + version;
                    return _current;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppVersion] Failed to read embedded VERSION: {ex.Message}");
            }

            _current = "v0.0.0";
            return _current;
        }
    }
}
