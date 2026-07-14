using System;
using System.IO;
using System.Text.Json;
using OsuMapManager.Models;

namespace OsuMapManager.Services;

/// <summary>
/// Manages persistent app settings as JSON.
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                Console.WriteLine($"[SettingsService] Loaded settings. OsuPath={Settings.OsuInstallPath}, Threads={Settings.DownloadThreads}, Source={Settings.DownloadSource}");
            }
            else
            {
                Console.WriteLine("[SettingsService] No settings file found, using defaults.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            Console.WriteLine("[SettingsService] Settings saved.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
        }
    }
}
