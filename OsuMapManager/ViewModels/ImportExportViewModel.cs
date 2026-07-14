using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private BeatmapDataService? _beatmapData;

    // --- Tab navigation ---
    [ObservableProperty]
    public partial bool IsImportMode { get; set; } = true;

    // --- Import ---
    [ObservableProperty]
    public partial string ImportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsImporting { get; set; }

    // --- Export: Collections ---
    public ObservableCollection<CollectionItem> Collections { get; } = new();

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    public partial string ExportStatus { get; set; } = string.Empty;

    // --- Export format dialog ---
    [ObservableProperty]
    public partial bool ShowExportFormatDialog { get; set; }

    [ObservableProperty]
    public partial string ExportFilePath { get; set; } = string.Empty;

    public ImportExportViewModel()
    {
        Console.WriteLine("[ImportExportViewModel] Created.");
    }

    public void SetServices(OsuDataService? osuData, SettingsService? settings, BeatmapDataService? beatmapData)
    {
        _osuData = osuData;
        _settings = settings;
        _beatmapData = beatmapData;
        Console.WriteLine("[ImportExportViewModel] Services set.");
    }

    /// <summary>
    /// Switch to Import tab.
    /// </summary>
    [RelayCommand]
    public void ShowImport()
    {
        IsImportMode = true;
        ImportStatus = string.Empty;
    }

    /// <summary>
    /// Switch to Export tab and load collections.
    /// </summary>
    [RelayCommand]
    public async Task ShowExportAsync()
    {
        IsImportMode = false;
        await LoadCollectionsAsync();
    }

    /// <summary>
    /// Load collections from osu! for export.
    /// </summary>
    [RelayCommand]
    public async Task LoadCollectionsAsync()
    {
        if (_osuData == null)
        {
            ExportStatus = "osu! path not configured.";
            return;
        }

        Collections.Clear();
        ExportStatus = "Loading collections...";

        try
        {
            var service = new CollectionService(_osuData, _settings!);
            var collections = await Task.Run(() => service.GetLocalCollections());

            foreach (var col in collections)
            {
                Collections.Add(new CollectionItem
                {
                    Name = col.Name,
                    BeatmapCount = col.BeatmapCount,
                    BeatmapOnlineIds = col.BeatmapOnlineIds,
                    IsSelected = false
                });
            }

            ExportStatus = $"Loaded {collections.Count} collections.";
            Console.WriteLine($"[ImportExportViewModel] Loaded {collections.Count} collections.");
        }
        catch (Exception ex)
        {
            ExportStatus = $"Error: {ex.Message}";
            Console.WriteLine($"[ImportExportViewModel] Error loading collections: {ex}");
        }
    }

    /// <summary>
    /// Select a file for import.
    /// </summary>
    [RelayCommand]
    public async Task BrowseImportFileAsync()
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(
            Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null);

        if (topLevel != null)
        {
            var fileTypes = new List<Avalonia.Platform.Storage.FilePickerFileType>
            {
                new("Collection Files") { Patterns = new[] { "*.txt", "*.zip" } },
                new("All Files") { Patterns = new[] { "*.*" } }
            };

            var result = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select Collection File",
                    AllowMultiple = false,
                    FileTypeFilter = fileTypes
                });

            if (result.Count > 0)
            {
                ImportFilePath = result[0].Path.LocalPath;
                Console.WriteLine($"[ImportExportViewModel] Selected import file: {ImportFilePath}");
            }
        }
    }

    /// <summary>
    /// Execute import.
    /// </summary>
    [RelayCommand]
    public async Task ImportAsync()
    {
        if (string.IsNullOrEmpty(ImportFilePath) || _osuData == null || _settings == null)
        {
            ImportStatus = "Please select a file and configure osu! path.";
            return;
        }

        IsImporting = true;
        ImportStatus = "Importing...";

        try
        {
            var service = new CollectionService(_osuData, _settings);
            var progress = new Progress<string>(msg => ImportStatus = msg);

            int imported;
            if (ImportFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                imported = await service.ImportFromZipAsync(ImportFilePath, progress);
            else
                imported = await service.ImportFromTxtAsync(ImportFilePath, progress);

            ImportStatus = $"Imported {imported} beatmaps.";
            Console.WriteLine($"[ImportExportViewModel] Import complete: {imported} beatmaps.");
        }
        catch (Exception ex)
        {
            ImportStatus = $"Error: {ex.Message}";
            Console.WriteLine($"[ImportExportViewModel] Import error: {ex}");
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Show export format selection dialog.
    /// </summary>
    [RelayCommand]
    public void ShowExportDialog()
    {
        var selected = Collections.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ExportStatus = "Please select at least one collection.";
            return;
        }

        ShowExportFormatDialog = true;
    }

    /// <summary>
    /// Export as TXT.
    /// </summary>
    [RelayCommand]
    public async Task ExportAsTxtAsync()
    {
        ShowExportFormatDialog = false;
        await DoExportAsync("txt");
    }

    /// <summary>
    /// Export as ZIP.
    /// </summary>
    [RelayCommand]
    public async Task ExportAsZipAsync()
    {
        ShowExportFormatDialog = false;
        await DoExportAsync("zip");
    }

    private async Task DoExportAsync(string format)
    {
        if (_osuData == null || _settings == null) return;

        var selected = Collections.Where(c => c.IsSelected).Select(c => new CollectionInfo
        {
            Name = c.Name,
            BeatmapCount = c.BeatmapCount,
            BeatmapOnlineIds = c.BeatmapOnlineIds
        }).ToList();

        if (selected.Count == 0) return;

        IsExporting = true;
        ExportStatus = "Exporting...";

        try
        {
            // Choose save path
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(
                Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null);

            if (topLevel != null)
            {
                var ext = format == "txt" ? "txt" : "zip";
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Save Collections Export",
                        DefaultExtension = $".{ext}",
                        SuggestedFileName = $"collections_export.{ext}"
                    });

                if (file != null)
                {
                    ExportFilePath = file.Path.LocalPath;
                }
                else
                {
                    IsExporting = false;
                    return;
                }
            }

            var service = new CollectionService(_osuData, _settings);
            var progress = new Progress<string>(msg => ExportStatus = msg);

            if (format == "txt")
                await service.ExportCollectionsAsTxtAsync(selected, ExportFilePath, progress);
            else
                await service.ExportCollectionsAsZipAsync(selected, ExportFilePath, progress);

            ExportStatus = $"Exported {selected.Count} collections to {ExportFilePath}";
            Console.WriteLine($"[ImportExportViewModel] Export complete.");
        }
        catch (Exception ex)
        {
            ExportStatus = $"Error: {ex.Message}";
            Console.WriteLine($"[ImportExportViewModel] Export error: {ex}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Cancel export dialog.
    /// </summary>
    [RelayCommand]
    public void CancelExportDialog()
    {
        ShowExportFormatDialog = false;
    }
}

/// <summary>
/// Item for collection selection list.
/// </summary>
public partial class CollectionItem : ViewModelBase
{
    public string Name { get; set; } = string.Empty;
    public int BeatmapCount { get; set; }
    public List<int> BeatmapOnlineIds { get; set; } = new();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
