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
    private MainViewModel? _mainVm;
    private CancellationTokenSource? _syncCts;

    // --- Local stats ---
    [ObservableProperty]
    public partial int LocalBeatmapCount { get; set; }

    [ObservableProperty]
    public partial string LocalTotalSize { get; set; } = "0 B";

    // --- Big Filters ---
    public ObservableCollection<BigFilterViewModel> BigFilters { get; } = new();

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

    // --- Check Status ---
    [ObservableProperty]
    public partial bool IsCheckingStatus { get; set; }

    [ObservableProperty]
    public partial string CheckStatusText { get; set; } = string.Empty;

    public ObservableCollection<BigFilterStatusItem> FilterStatuses { get; } = new();

    [ObservableProperty]
    public partial bool HasFilterStatuses { get; set; }

    /// <summary>
    /// Missing set IDs currently pending download.
    /// </summary>
    private HashSet<int> _missingSetIds = new();
    private int _extraBeatmapCount;
    private int _filterCounter;

    public SyncViewModel()
    {
        Console.WriteLine("[SyncViewModel] Created.");
        AddNewBigFilter();
    }

    public void SetServices(OsuDataService? osuData, BeatmapDataService? beatmapData,
        SettingsService? settings, MainViewModel? mainVm)
    {
        _osuData = osuData;
        _beatmapData = beatmapData;
        _settings = settings;
        _mainVm = mainVm;
        Console.WriteLine("[SyncViewModel] Services set.");
        LoadFilterState();
    }

    private bool ValidateServices()
    {
        if (_osuData == null || _settings == null || string.IsNullOrEmpty(_settings.Settings.OsuInstallPath))
        {
            _mainVm?.ShowError("osu! installation path not configured. Check Settings.");
            SyncStatusText = "osu! path not set.";
            return false;
        }
        if (_beatmapData == null || !_beatmapData.IsDataReady)
        {
            _mainVm?.ShowError("Beatmap database not ready. Check Settings.");
            SyncStatusText = "Database not ready.";
            return false;
        }
        _mainVm?.ClearStatus();
        return true;
    }

    // ================================================================
    // BigFilter management
    // ================================================================

    [RelayCommand]
    public void AddBigFilter()
    {
        AddNewBigFilter();
    }

    [RelayCommand]
    public void DeleteBigFilter(BigFilterViewModel? filter)
    {
        if (filter == null) return;
        BigFilters.Remove(filter);
        Console.WriteLine($"[SyncViewModel] Deleted BigFilter: {filter.Name}");
    }

    private void AddNewBigFilter()
    {
        _filterCounter++;
        var vm = new BigFilterViewModel
        {
            Name = $"Filter {_filterCounter}",
            IsCollapsed = false
        };
        BigFilters.Add(vm);
        Console.WriteLine($"[SyncViewModel] Added BigFilter: {vm.Name}");
    }

    // ================================================================
    // Check Status
    // ================================================================

    [RelayCommand]
    public async Task CheckStatusAsync()
    {
        if (!ValidateServices()) return;

        IsCheckingStatus = true;
        CheckStatusText = "Checking...";
        FilterStatuses.Clear();

        try
        {
            var filters = BigFilters
                .Select(f => f.ToSyncFilter())
                .ToList();

            for (int i = 0; i < filters.Count; i++)
            {
                var syncFilter = filters[i];
                var name = BigFilters[i].Name;

                CheckStatusText = $"Checking filter {i + 1}/{filters.Count}...";

                var dbCount = await Task.Run(() => _beatmapData!.CountFilteredBeatmapSetsAsync(syncFilter));
                var localCount = await _osuData!.CountLocalMatchingSetsAsync(syncFilter);

                FilterStatuses.Add(new BigFilterStatusItem
                {
                    FilterName = name,
                    LocalCount = localCount,
                    DatabaseCount = dbCount
                });

                Console.WriteLine($"[SyncViewModel] CheckStatus '{name}': local={localCount}, db={dbCount}");
            }

            HasFilterStatuses = FilterStatuses.Count > 0;
            CheckStatusText = $"Checked {filters.Count} filter(s).";
        }
        catch (Exception ex)
        {
            _mainVm?.ShowError($"Check status failed: {ex.Message}");
            CheckStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsCheckingStatus = false;
        }
    }

    // ================================================================
    // Start Sync
    // ================================================================

    [RelayCommand]
    public async Task StartSyncAsync()
    {
        if (!ValidateServices()) return;

        try
        {
            IsSyncing = true;
            SyncStatusText = "Analyzing...";
            SyncProgress = 0;
            ShowSyncConfirm = false;

            var filters = BigFilters
                .Select(f => f.ToSyncFilter())
                .ToList();

            if (filters.Count == 0)
            {
                SyncStatusText = "No filters configured.";
                return;
            }

            var syncService = new SyncService(_osuData!, _beatmapData!, _settings!);
            var progress = new Progress<string>(msg => SyncStatusText = msg);

            var (missingSetIds, extraCount) = await syncService.AnalyzeAsync(filters, progress);
            _missingSetIds = missingSetIds;
            _extraBeatmapCount = extraCount;

            if (missingSetIds.Count == 0)
            {
                SyncStatusText = "Already in sync! No missing beatmaps.";
                SyncProgress = 100;
            }
            else
            {
                SyncConfirmMessage =
                    $"Analysis complete.\n\n" +
                    $"Missing beatmap sets: {missingSetIds.Count}\n" +
                    $"Extra beatmaps: {extraCount}\n\n" +
                    $"Click Continue to start downloading.";
                ShowSyncConfirm = true;
                SyncStatusText = "Review and confirm";
            }
        }
        catch (Exception ex)
        {
            _mainVm?.ShowError($"Sync error: {ex.Message}");
            SyncStatusText = $"Error: {ex.Message}";
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
                ExtraPromptMessage = $"There are {_extraBeatmapCount} extra beatmaps not matching the current filters. Remove them?";
                ShowExtraPrompt = true;
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatusText = "Sync cancelled.";
        }
        catch (Exception ex)
        {
            _mainVm?.ShowError($"Download error: {ex.Message}");
            SyncStatusText = $"Error: {ex.Message}";
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

    /// <summary>
    /// Save current BigFilter state to settings for persistence.
    /// </summary>
    public void SaveFilterState()
    {
        if (_settings == null) return;
        try
        {
            _settings.Settings.SavedBigFilters = BigFilters
                .Select(vm => new BigFilter
                {
                    Name = vm.Name,
                    IsCollapsed = vm.IsCollapsed,
                    Filter = vm.ToSyncFilter()
                })
                .ToList();
            _settings.Save();
            Console.WriteLine($"[SyncViewModel] Saved {BigFilters.Count} BigFilters to settings.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncViewModel] Failed to save filter state: {ex.Message}");
        }
    }

    /// <summary>
    /// Load BigFilter state from saved settings.
    /// </summary>
    private void LoadFilterState()
    {
        if (_settings == null) return;
        try
        {
            var saved = _settings.Settings.SavedBigFilters;
            if (saved.Count > 0)
            {
                BigFilters.Clear();
                foreach (var bf in saved)
                {
                    _filterCounter++;
                    var vm = BigFilterViewModel.FromBigFilter(bf);
                    BigFilters.Add(vm);
                }
                Console.WriteLine($"[SyncViewModel] Loaded {BigFilters.Count} BigFilters from settings.");
            }
            else
            {
                // No saved filters, create one default
                AddNewBigFilter();
                Console.WriteLine("[SyncViewModel] No saved filters, created default.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncViewModel] Failed to load filter state: {ex.Message}");
            if (BigFilters.Count == 0)
                AddNewBigFilter();
        }
    }
}
