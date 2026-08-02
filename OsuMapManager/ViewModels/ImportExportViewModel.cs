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

public partial class ImportExportViewModel : ViewModelBase
{
    private OsuDataService? _osuData;
    private SettingsService? _settings;
    private SyncService? _syncService;
    private CancellationTokenSource? _syncCts;

    // --- Tab navigation ---
    [ObservableProperty]
    public partial bool IsImportMode { get; set; } = true;

    // --- Import: file ---
    [ObservableProperty]
    public partial string ImportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasImportedFile { get; set; }

    // --- Import: parsed collections ---
    public ObservableCollection<ImportCollectionStatus> ImportCollections { get; } = new();

    [ObservableProperty]
    public partial bool ShowImportStatus { get; set; }

    // --- Import: download progress ---
    [ObservableProperty]
    public partial bool ShowImportProgress { get; set; }

    [ObservableProperty]
    public partial double ImportProgress { get; set; }

    [ObservableProperty]
    public partial string ImportProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportDetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    // --- Import: confirm dialog ---
    [ObservableProperty]
    public partial bool ShowImportConfirm { get; set; }

    [ObservableProperty]
    public partial string ImportConfirmMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanOperate { get; set; } = true;

    // --- Export: Collections ---
    public ObservableCollection<CollectionItem> Collections { get; } = new();

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    public partial string ExportStatus { get; set; } = string.Empty;

    // --- Export: difficulty filter ---
    [ObservableProperty]
    public partial double? ExportDiffMin { get; set; }

    [ObservableProperty]
    public partial double? ExportDiffMax { get; set; }

    // Cached parsed data
    private Dictionary<string, List<BeatmapRef>>? _parsedCollections;
    private HashSet<int>? _missingIds;

    public ImportExportViewModel()
    {
        Console.WriteLine("[ImportExportViewModel] Created.");
    }

    public void SetServices(OsuDataService? osuData, SettingsService? settings, BeatmapDataService? beatmapData)
    {
        _osuData = osuData;
        _settings = settings;
        if (osuData != null && beatmapData != null && settings != null)
            _syncService = new SyncService(osuData, beatmapData, settings);
        Console.WriteLine("[ImportExportViewModel] Services set.");
    }

    // ================================================================
    // Tab switching
    // ================================================================

    [RelayCommand]
    public void ShowImport()
    {
        IsImportMode = true;
        ImportStatus = string.Empty;
    }

    [RelayCommand]
    public async Task ShowExportAsync()
    {
        IsImportMode = false;
        await LoadCollectionsAsync();
    }

    // ================================================================
    // File browse
    // ================================================================

    [RelayCommand]
    public async Task BrowseImportFileAsync()
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);

        if (topLevel != null)
        {
            var result = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select Collection File",
                    AllowMultiple = false,
                    FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
                    {
                        new("Collection Files") { Patterns = new[] { "*.txt" } },
                        new("All Files") { Patterns = new[] { "*.*" } }
                    }
                });

            if (result.Count > 0)
            {
                ImportFilePath = result[0].Path.LocalPath;
                HasImportedFile = true;
                ShowImportStatus = false;
                ShowImportProgress = false;
                ImportCollections.Clear();
                ImportStatus = "File selected. Use Check Status to begin.";
            }
        }
    }

    // ================================================================
    // Check Status
    // ================================================================

    [RelayCommand]
    public async Task CheckImportStatusAsync()
    {
        if (_osuData == null || string.IsNullOrEmpty(ImportFilePath))
        {
            ImportStatus = "Please select a .txt file and configure osu! path.";
            return;
        }

        CanOperate = false;
        ImportStatus = "Checking...";
        ShowImportProgress = false;

        try
        {
            _parsedCollections = await CollectionService.ParseTxtFileAsync(ImportFilePath);
            var service = new CollectionService(_osuData, _settings!);
            var statuses = await service.GetImportStatusAsync(_parsedCollections);

            ImportCollections.Clear();
            foreach (var s in statuses) ImportCollections.Add(s);

            ShowImportStatus = true;
            ImportStatus = $"{statuses.Sum(s => s.LocalBeatmaps)} / {statuses.Sum(s => s.TotalBeatmaps)} beatmaps locally available.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Error: {ex.Message}";
        }
        finally { CanOperate = true; }
    }

    // ================================================================
    // Start Sync
    // ================================================================

    [RelayCommand]
    public async Task StartImportSyncAsync()
    {
        if (_osuData == null || _settings == null || _syncService == null)
        { ImportStatus = "Services not initialized."; return; }
        if (_parsedCollections == null) { ImportStatus = "Run Check Status first."; return; }

        CanOperate = false;

        try
        {
            var service = new CollectionService(_osuData, _settings);
            _missingIds = await service.GetMissingBeatmapIdsAsync(_parsedCollections);

            if (_missingIds.Count == 0)
            {
                ImportStatus = "All beatmaps already exist locally.";
                CanOperate = true;
                return;
            }

            ImportConfirmMessage = $"{_missingIds.Count} beatmap sets need to be downloaded.\nDownloaded .osz files go to your osu! directory.\nAfter download, import in osu! lazer, then click Apply Collection.";
            ShowImportConfirm = true;
        }
        catch (Exception ex) { ImportStatus = $"Error: {ex.Message}"; CanOperate = true; }
    }

    [RelayCommand]
    public async Task ContinueImportSyncAsync()
    {
        ShowImportConfirm = false;
        if (_missingIds == null || _missingIds.Count == 0 || _syncService == null || _settings == null)
        { CanOperate = true; return; }

        IsDownloading = true;
        ShowImportProgress = true;
        ImportProgress = 0;
        ImportProgressText = "Downloading beatmaps...";
        _syncCts = new CancellationTokenSource();

        try
        {
            var (downloaded, failed) = await _syncService.DownloadMissingAsync(
                _missingIds,
                _settings.Settings.DownloadPath ?? _settings.Settings.OsuInstallPath,
                progress: new Progress<(int Current, int Total, int Downloaded, int Failed)>(p =>
                {
                    ImportProgress = p.Total > 0 ? (double)p.Current / p.Total * 100 : 0;
                    ImportProgressText = $"Downloading ({p.Current}/{p.Total})";
                    ImportDetailText = $"OK: {p.Downloaded}, Failed: {p.Failed}";
                }),
                ct: _syncCts.Token);

            ImportProgressText = "Download Complete!";
            ImportDetailText = $"Downloaded: {downloaded}, Failed: {failed}. Import .osz in osu! lazer, then Apply Collection.";
            ImportStatus = "Download finished.";
        }
        catch (OperationCanceledException)
        { ImportProgressText = "Cancelled"; ImportStatus = "Download cancelled."; }
        catch (Exception ex)
        { ImportProgressText = "Error"; ImportDetailText = ex.Message; ImportStatus = $"Error: {ex.Message}"; }
        finally { IsDownloading = false; CanOperate = true; }
    }

    [RelayCommand] public void CancelImportSync() => _syncCts?.Cancel();
    [RelayCommand] public void DismissImportConfirm() { ShowImportConfirm = false; CanOperate = true; }

    // ================================================================
    // Apply Collection
    // ================================================================

    [RelayCommand]
    public async Task ApplyCollectionsAsync()
    {
        if (_osuData == null || _settings == null) { ImportStatus = "osu! path not configured."; return; }
        if (_parsedCollections == null) { ImportStatus = "Run Check Status first."; return; }

        CanOperate = false;
        ImportStatus = "Applying collections...";

        try
        {
            _osuData.Close();
            var service = new CollectionService(_osuData, _settings);
            await service.ApplyCollectionsAsync(_parsedCollections);
            ImportStatus = "Collections applied! Restart osu! lazer to see changes.";
        }
        catch (Exception ex) { ImportStatus = $"Error: {ex.Message}"; }
        finally { CanOperate = true; }
    }

    // ================================================================
    // Export
    // ================================================================

    [RelayCommand]
    public async Task LoadCollectionsAsync()
    {
        if (_osuData == null) { ExportStatus = "osu! path not configured."; return; }

        Collections.Clear();
        ExportStatus = "Loading collections...";

        try
        {
            var service = new CollectionService(_osuData, _settings!);
            var collections = await service.GetLocalCollectionsAsync();

            // Get star ratings for filtering display
            var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
            var diffStars = localBeatmaps.Where(b => b.OnlineId > 0)
                .ToDictionary(b => b.OnlineId, b => b.StarRating);

            foreach (var col in collections)
            {
                var filtered = col.Beatmaps.AsEnumerable();
                if (ExportDiffMin.HasValue || ExportDiffMax.HasValue)
                {
                    filtered = filtered.Where(b =>
                        diffStars.TryGetValue(b.DifficultyId, out var sr) &&
                        (!ExportDiffMin.HasValue || sr >= ExportDiffMin.Value) &&
                        (!ExportDiffMax.HasValue || sr <= ExportDiffMax.Value));
                }

                var filteredList = filtered.ToList();
                Collections.Add(new CollectionItem
                {
                    Name = col.Name,
                    BeatmapCount = filteredList.Count,
                    Beatmaps = filteredList,
                    IsSelected = false
                });
            }

            ExportStatus = $"Loaded {Collections.Count} collections.";
        }
        catch (Exception ex) { ExportStatus = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task ExportAsync()
    {
        if (_osuData == null || _settings == null) return;

        var selected = Collections.Where(c => c.IsSelected).Select(c => new CollectionInfo
        {
            Name = c.Name,
            BeatmapCount = c.BeatmapCount,
            Beatmaps = c.Beatmaps
        }).ToList();

        if (selected.Count == 0) { ExportStatus = "Please select at least one collection."; return; }

        IsExporting = true;
        ExportStatus = "Exporting...";

        try
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(
                Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null);

            string? exportPath = null;
            if (topLevel != null)
            {
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Save Collections Export",
                        DefaultExtension = ".txt",
                        SuggestedFileName = "collections_export.txt"
                    });
                if (file != null) exportPath = file.Path.LocalPath;
            }

            if (exportPath == null) { IsExporting = false; return; }

            var service = new CollectionService(_osuData, _settings);
            var progress = new Progress<string>(msg => ExportStatus = msg);
            await service.ExportCollectionsAsTxtAsync(selected, exportPath, ExportDiffMin, ExportDiffMax, progress);
            ExportStatus = $"Exported {selected.Count} collections.";
        }
        catch (Exception ex) { ExportStatus = $"Error: {ex.Message}"; }
        finally { IsExporting = false; }
    }
}

public partial class CollectionItem : ViewModelBase
{
    public string Name { get; set; } = string.Empty;
    public int BeatmapCount { get; set; }
    public List<BeatmapRef> Beatmaps { get; set; } = new();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
