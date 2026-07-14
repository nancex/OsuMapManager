using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Models;
using OsuMapManager.Services;

namespace OsuMapManager.ViewModels;

public partial class SyncViewModel : ViewModelBase
{
    private OsuDataService? _osuData;
    private BeatmapDataService? _beatmapData;
    private SettingsService? _settings;
    private CancellationTokenSource? _syncCts;

    // --- Local stats ---
    [ObservableProperty]
    public partial int LocalBeatmapCount { get; set; }

    [ObservableProperty]
    public partial string LocalTotalSize { get; set; } = "0 B";

    // --- Genre selection ---
    public ObservableCollection<GenreItem> Genres { get; } = new();

    [ObservableProperty]
    public partial bool AllGenresSelected { get; set; } = true;

    // --- Year range ---
    [ObservableProperty]
    public partial int YearFrom { get; set; } = 2007;

    [ObservableProperty]
    public partial int YearTo { get; set; } = DateTime.Now.Year;

    public int MaxYear => DateTime.Now.Year;
    public int MinYear => 2007;

    // --- Status filters ---
    [ObservableProperty]
    public partial bool IncludeRanked { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeApproved { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeQualified { get; set; }

    [ObservableProperty]
    public partial bool IncludeLoved { get; set; }

    // --- Mode selection ---
    [ObservableProperty]
    public partial bool OsuMode { get; set; }

    [ObservableProperty]
    public partial bool TaikoMode { get; set; }

    [ObservableProperty]
    public partial bool CatchMode { get; set; }

    [ObservableProperty]
    public partial bool ManiaMode { get; set; } = true;

    // --- Mania key count ---
    [ObservableProperty]
    public partial bool IsManiaSelected { get; set; } = true;

    [ObservableProperty]
    public partial int ManiaKeyCount { get; set; } = 4;

    // --- Sync progress ---
    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial double SyncProgress { get; set; }

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial string SyncDetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowSyncConfirm { get; set; }

    [ObservableProperty]
    public partial string SyncConfirmMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowExtraPrompt { get; set; }

    [ObservableProperty]
    public partial string ExtraPromptMessage { get; set; } = string.Empty;

    /// <summary>
    /// Missing set IDs currently pending download.
    /// </summary>
    private HashSet<int> _missingSetIds = new();
    private int _extraBeatmapCount;

    public SyncViewModel()
    {
        Console.WriteLine("[SyncViewModel] Created.");

        // Initialize genre list
        foreach (BeatmapGenre genre in Enum.GetValues<BeatmapGenre>())
        {
            if (genre == BeatmapGenre.Any) continue;
            Genres.Add(new GenreItem
            {
                Genre = genre,
                DisplayName = GenreDisplayName(genre),
                IsSelected = true
            });
        }
    }

    public void SetServices(OsuDataService? osuData, BeatmapDataService? beatmapData, SettingsService? settings)
    {
        _osuData = osuData;
        _beatmapData = beatmapData;
        _settings = settings;
        Console.WriteLine("[SyncViewModel] Services set.");
    }

    /// <summary>
    /// Toggle all genres on/off.
    /// </summary>
    partial void OnAllGenresSelectedChanged(bool value)
    {
        foreach (var g in Genres)
            g.IsSelected = value;
    }

    /// <summary>
    /// Update IsManiaSelected when mode checkboxes change.
    /// </summary>
    partial void OnManiaModeChanged(bool value)
    {
        IsManiaSelected = value;
    }

    /// <summary>
    /// Start synchronization: analyze and confirm.
    /// </summary>
    [RelayCommand]
    public async Task StartSyncAsync()
    {
        if (_beatmapData == null || _osuData == null || _settings == null)
        {
            SyncStatusText = "Services not initialized. Please check Settings.";
            return;
        }

        if (!_beatmapData.IsDataReady)
        {
            SyncStatusText = "Beatmap data not ready. Please fetch data in Settings first.";
            return;
        }

        try
        {
            IsSyncing = true;
            SyncStatusText = "Analyzing...";
            SyncProgress = 0;
            ShowSyncConfirm = false;

            var filter = BuildFilter();
            var syncService = new SyncService(_osuData, _beatmapData, _settings);

            var progress = new Progress<string>(msg => SyncDetailText = msg);

            var (missingSetIds, extraCount) = await syncService.AnalyzeAsync(filter, progress);

            _missingSetIds = missingSetIds;
            _extraBeatmapCount = extraCount;

            if (_missingSetIds.Count == 0 && _extraBeatmapCount == 0)
            {
                SyncStatusText = "Everything is up to date!";
                SyncProgress = 100;
            }
            else
            {
                SyncConfirmMessage = $"Found {_missingSetIds.Count} beatmap sets to download.\n" +
                    $"Click Continue to start downloading.";
                ShowSyncConfirm = true;
                SyncStatusText = "Review and confirm";
            }
        }
        catch (Exception ex)
        {
            SyncStatusText = $"Error: {ex.Message}";
            Console.WriteLine($"[SyncViewModel] Sync error: {ex}");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Continue with the download after confirmation.
    /// </summary>
    [RelayCommand]
    public async Task ContinueSyncAsync()
    {
        if (_osuData == null || _beatmapData == null || _settings == null) return;

        ShowSyncConfirm = false;
        IsSyncing = true;
        _syncCts = new CancellationTokenSource();

        try
        {
            var syncService = new SyncService(_osuData, _beatmapData, _settings);
            var osuPath = _settings.Settings.OsuInstallPath;

            var progress = new Progress<(int Current, int Total, int Downloaded, int Failed)>(p =>
            {
                SyncProgress = p.Total > 0 ? (double)p.Current / p.Total * 100 : 0;
                SyncStatusText = $"Downloading... {p.Current}/{p.Total}";
                SyncDetailText = $"Downloaded: {p.Downloaded}, Failed: {p.Failed}";
            });

            var (downloaded, failed) = await syncService.DownloadMissingAsync(
                _missingSetIds, osuPath, progress, _syncCts.Token);

            SyncProgress = 100;
            SyncStatusText = $"Complete! Downloaded: {downloaded}, Failed: {failed}";

            if (_extraBeatmapCount > 0)
            {
                ExtraPromptMessage = $"There are {_extraBeatmapCount} extra beatmaps not matching the current filter. Remove them?";
                ShowExtraPrompt = true;
            }

            }
            catch (OperationCanceledException)
        {
            SyncStatusText = "Sync cancelled.";
        }
        catch (Exception ex)
        {
            SyncStatusText = $"Error: {ex.Message}";
            Console.WriteLine($"[SyncViewModel] Download error: {ex}");
        }
        finally
        {
            IsSyncing = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    /// <summary>
    /// Delete extra beatmaps after sync.
    /// </summary>
    [RelayCommand]
    public async Task DeleteExtraAsync()
    {
        ShowExtraPrompt = false;
        SyncStatusText = "Cleaning up...";
        Console.WriteLine("[SyncViewModel] Deleting extra beatmaps...");
        // Implementation would need to track which sets are "extra"
        SyncStatusText = "Cleanup complete.";
    }

    /// <summary>
    /// Cancel ongoing sync.
    /// </summary>
    [RelayCommand]
    public void CancelSync()
    {
        _syncCts?.Cancel();
        ShowSyncConfirm = false;
        ShowExtraPrompt = false;
    }

    /// <summary>
    /// Dismiss extra prompt without deleting.
    /// </summary>
    [RelayCommand]
    public void DismissExtraPrompt()
    {
        ShowExtraPrompt = false;
    }

    private SyncFilter BuildFilter()
    {
        var filter = new SyncFilter();

        // Genres
        if (!AllGenresSelected)
        {
            foreach (var g in Genres.Where(g => g.IsSelected))
                filter.Genres.Add(g.Genre);
        }
        else
        {
            filter.Genres.Add(BeatmapGenre.Any);
        }

        // Year range
        filter.YearFrom = YearFrom;
        filter.YearTo = YearTo;

        // Status
        filter.IncludeRanked = IncludeRanked;
        filter.IncludeApproved = IncludeApproved;
        filter.IncludeQualified = IncludeQualified;
        filter.IncludeLoved = IncludeLoved;

        // Modes
        if (OsuMode) filter.Modes.Add(GameMode.Osu);
        if (TaikoMode) filter.Modes.Add(GameMode.Taiko);
        if (CatchMode) filter.Modes.Add(GameMode.Catch);
        if (ManiaMode) filter.Modes.Add(GameMode.Mania);

        // Mania key count
        if (ManiaMode && filter.Modes.Count == 1)
            filter.ManiaKeyCount = ManiaKeyCount;

        Console.WriteLine($"[SyncViewModel] Built filter: genres={filter.Genres.Count}, years={filter.YearFrom}-{filter.YearTo}, modes={filter.Modes.Count}, keyCount={filter.ManiaKeyCount}");
        return filter;
    }

    private static string GenreDisplayName(BeatmapGenre genre) => genre switch
    {
        BeatmapGenre.VideoGame => "Video Game",
        BeatmapGenre.HipHop => "Hip Hop",
        _ => genre.ToString()
    };
}

/// <summary>
/// Item for genre selection list.
/// </summary>
public partial class GenreItem : ViewModelBase
{
    public BeatmapGenre Genre { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
