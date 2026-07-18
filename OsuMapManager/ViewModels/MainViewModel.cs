using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Services;

namespace OsuMapManager.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Services
    private SettingsService? _settingsService;
    private OsuDataService? _osuDataService;
    private BeatmapDataService? _beatmapDataService;

    // Tab view models
    public SyncViewModel SyncVm { get; } = new();
    public ImportExportViewModel ImportExportVm { get; } = new();
    public SettingsViewModel SettingsVm { get; } = new();

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty]
    public partial string StatusForeground { get; set; } = "#80FFFFFF";

    public MainViewModel()
    {
        Console.WriteLine("[MainViewModel] Created.");
    }

    /// <summary>
    /// Show an error message in the status bar (red text).
    /// </summary>
    public void ShowError(string message)
    {
        StatusMessage = message;
        StatusForeground = "#FF6B6B";
        Console.WriteLine($"[MainViewModel] Error: {message}");
    }

    /// <summary>
    /// Reset status bar to normal.
    /// </summary>
    public void ClearStatus()
    {
        StatusMessage = "Ready";
        StatusForeground = "#80FFFFFF";
    }

    /// <summary>
    /// Initialize all services. Called after the window is loaded.
    /// </summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        try
        {
            StatusMessage = "Initializing...";
            Console.WriteLine("[MainViewModel] Initializing services...");

            // Load settings
            _settingsService = new SettingsService();

            // Pass settings to SettingsVm
            SettingsVm.SetSettingsService(_settingsService);

            // Get beatmap data service from settings
            _beatmapDataService = SettingsVm.GetBeatmapDataService();
            if (_beatmapDataService == null)
            {
                Console.WriteLine("[MainViewModel] No beatmap database selected yet.");
            }

            // If osu path is configured, initialize osu data service
            if (!string.IsNullOrEmpty(_settingsService.Settings.OsuInstallPath))
            {
                _osuDataService = new OsuDataService(_settingsService.Settings.OsuInstallPath);
                _osuDataService.TryOpen();
                await RefreshLocalStatsAsync();
            }

            // Wire up services to sub-viewmodels
            SyncVm.SetServices(_osuDataService, _beatmapDataService, _settingsService, this);
            ImportExportVm.SetServices(_osuDataService, _settingsService, _beatmapDataService);

            IsInitialized = true;
            StatusMessage = "Ready";
            StatusForeground = "#80FFFFFF";
            Console.WriteLine("[MainViewModel] Initialization complete.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Init failed: {ex.Message}";
            StatusForeground = "#FF6B6B";
            Console.WriteLine($"[MainViewModel] Init error: {ex}");
        }
    }

    /// <summary>
    /// Refreshes local beatmap stats display.
    /// </summary>
    public async Task RefreshLocalStatsAsync()
    {
        if (_osuDataService == null) return;

        await Task.Run(async () =>
        {
            try
            {
                var (count, totalBytes) = await _osuDataService.GetLocalStatsAsync();
                SyncVm.LocalBeatmapCount = count;
                SyncVm.LocalTotalSize = FormatSize(totalBytes);
                Console.WriteLine($"[MainViewModel] Stats refreshed: {count} sets, {SyncVm.LocalTotalSize}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainViewModel] Failed to refresh stats: {ex.Message}");
            }
        });
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F2} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024.0:F2} KB";
        return $"{bytes} B";
    }
}
