using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Services;

namespace OsuMapManager.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private SettingsService? _settingsService;
    private BeatmapDataService? _beatmapDataService;
    private bool _isLoading;

    // --- osu! Path ---
    [ObservableProperty]
    public partial string OsuInstallPath { get; set; } = string.Empty;

    // --- Database path ---
    [ObservableProperty]
    public partial string DatabasePath { get; set; } = string.Empty;

    // --- Beatmap data status ---
    [ObservableProperty]
    public partial bool IsDatabaseReady { get; set; }

    // --- Download threads ---
    [ObservableProperty]
    public partial int DownloadThreads { get; set; } = 4;

    // --- Download source ---
    [ObservableProperty]
    public partial bool UseOfficialSource { get; set; } = true;

    [ObservableProperty]
    public partial bool UseCatboyMirror { get; set; }

    public SettingsViewModel()
    {
        Console.WriteLine("[SettingsViewModel] Created.");
    }

    public void SetSettingsService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
    }

    public BeatmapDataService? GetBeatmapDataService() => _beatmapDataService;

    private void LoadFromSettings()
    {
        if (_settingsService == null) return;

        _isLoading = true;

        var s = _settingsService.Settings;
        OsuInstallPath = s.OsuInstallPath;
        DatabasePath = s.DatabasePath;
        DownloadThreads = s.DownloadThreads;
        UseOfficialSource = s.DownloadSource == "official";
        UseCatboyMirror = s.DownloadSource == "catboy";

        TryOpenDatabase();

        _isLoading = false;

        Console.WriteLine($"[SettingsViewModel] Loaded: path={OsuInstallPath}, db={DatabasePath}, threads={DownloadThreads}");
    }

    private void TryOpenDatabase()
    {
        if (!string.IsNullOrEmpty(DatabasePath) && System.IO.File.Exists(DatabasePath))
        {
            _beatmapDataService = new BeatmapDataService(DatabasePath);
            IsDatabaseReady = _beatmapDataService.IsDataReady;
        }
        else
        {
            _beatmapDataService = null;
            IsDatabaseReady = false;
        }
    }

    private void AutoSave()
    {
        if (_isLoading || _settingsService == null) return;
        SaveSettings();
    }

    partial void OnOsuInstallPathChanged(string value) => AutoSave();
    partial void OnDatabasePathChanged(string value) { AutoSave(); TryOpenDatabase(); }
    partial void OnDownloadThreadsChanged(int value) { AutoSave(); _settingsService!.Settings.DownloadThreads = value; _settingsService.Save(); }
    partial void OnUseOfficialSourceChanged(bool value) { if (value) { _settingsService!.Settings.DownloadSource = "official"; _settingsService.Save(); } }
    partial void OnUseCatboyMirrorChanged(bool value) { if (value) { _settingsService!.Settings.DownloadSource = "catboy"; _settingsService.Save(); } }

    public void SaveSettings()
    {
        if (_settingsService == null) return;

        _settingsService.Settings.OsuInstallPath = OsuInstallPath;
        _settingsService.Settings.DatabasePath = DatabasePath;
        _settingsService.Settings.DownloadThreads = Math.Clamp(DownloadThreads, 1, 16);
        _settingsService.Settings.DownloadSource = UseCatboyMirror ? "catboy" : "official";
        _settingsService.Save();

        Console.WriteLine("[SettingsViewModel] Settings saved.");
    }

    [RelayCommand]
    public async Task BrowseOsuPathAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select osu! lazer installation folder",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            OsuInstallPath = folders[0].Path.LocalPath;
            Console.WriteLine($"[SettingsViewModel] Selected osu! path: {OsuInstallPath}");
        }
    }

    [RelayCommand]
    public async Task BrowseDatabasePathAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select beatmap database file (.db)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("SQLite Database")
                    {
                        Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" }
                    }
                }
            });

        if (files.Count > 0)
        {
            DatabasePath = files[0].Path.LocalPath;
            Console.WriteLine($"[SettingsViewModel] Selected database: {DatabasePath}");
            TryOpenDatabase();
        }
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
        }
        return null;
    }
}
