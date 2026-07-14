using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Services;

namespace OsuMapManager.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private SettingsService? _settingsService;
    private BeatmapDataService? _beatmapDataService;

    // --- osu! Path ---
    [ObservableProperty]
    public partial string OsuInstallPath { get; set; } = string.Empty;

    // --- Download threads ---
    [ObservableProperty]
    public partial int DownloadThreads { get; set; } = 4;

    // --- Beatmap data status ---
    [ObservableProperty]
    public partial bool IsBeatmapDataReady { get; set; }

    [ObservableProperty]
    public partial bool IsFetchingData { get; set; }

    [ObservableProperty]
    public partial string FetchProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double FetchProgress { get; set; }

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

    /// <summary>
    /// Load current settings into view model properties.
    /// </summary>
    private void LoadFromSettings()
    {
        if (_settingsService == null) return;

        var s = _settingsService.Settings;
        OsuInstallPath = s.OsuInstallPath;
        DownloadThreads = s.DownloadThreads;
        UseOfficialSource = s.DownloadSource == "official";
        UseCatboyMirror = s.DownloadSource == "catboy";
        IsBeatmapDataReady = s.BeatmapDataReady;

        Console.WriteLine($"[SettingsViewModel] Loaded settings: path={OsuInstallPath}, threads={DownloadThreads}, source={(UseOfficialSource ? "official" : "catboy")}, dataReady={IsBeatmapDataReady}");
    }

    /// <summary>
    /// Save current settings.
    /// </summary>
    [RelayCommand]
    public void SaveSettings()
    {
        if (_settingsService == null) return;

        _settingsService.Settings.OsuInstallPath = OsuInstallPath;
        _settingsService.Settings.DownloadThreads = Math.Clamp(DownloadThreads, 1, 16);
        _settingsService.Settings.DownloadSource = UseCatboyMirror ? "catboy" : "official";
        _settingsService.Settings.BeatmapDataReady = IsBeatmapDataReady;
        _settingsService.Save();

        Console.WriteLine("[SettingsViewModel] Settings saved.");

    }

    /// <summary>
    /// Browse for osu! lazer installation folder.
    /// </summary>
    [RelayCommand]
    public async Task BrowseOsuPathAsync()
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);

        if (topLevel != null)
        {
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
    }

    /// <summary>
    /// Download and process beatmap data from data.ppy.sh.
    /// </summary>
    [RelayCommand]
    public async Task FetchBeatmapDataAsync()
    {
        if (_settingsService == null) return;

        IsFetchingData = true;
        FetchProgress = 0;
        FetchProgressText = "Starting...";

        try
        {
            var dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OsuMapManager", "BeatmapData");

            _beatmapDataService = new BeatmapDataService(dataDir);

            var progress = new Progress<(string Stage, double Progress)>(p =>
            {
                FetchProgressText = p.Stage;
                FetchProgress = p.Progress * 100;
            });

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var success = await _beatmapDataService.FetchBeatmapDataAsync(progress, cts.Token);

            if (success)
            {
                IsBeatmapDataReady = true;
                FetchProgressText = "Beatmap data ready!";
                FetchProgress = 100;
            }
            else
            {
                FetchProgressText = "Failed to fetch beatmap data.";
            }
        }
        catch (Exception ex)
        {
            FetchProgressText = $"Error: {ex.Message}";
            Console.WriteLine($"[SettingsViewModel] Error fetching data: {ex}");
        }
        finally
        {
            IsFetchingData = false;
        }
    }
}
