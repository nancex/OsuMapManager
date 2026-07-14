using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OsuMapManager.Models;

namespace OsuMapManager.Services;

/// <summary>
/// Handles import/export of beatmap collections.
/// </summary>
public class CollectionService
{
    private readonly OsuDataService _osuData;
    private readonly SettingsService _settings;
    private readonly HttpClient _http = new();

    public CollectionService(OsuDataService osuData, SettingsService settings)
    {
        _osuData = osuData;
        _settings = settings;
    }

    /// <summary>
    /// Gets all local collections from osu!.
    /// </summary>
    public List<CollectionInfo> GetLocalCollections()
    {
        return _osuData.GetCollections();
    }

    /// <summary>
    /// Exports selected collections as a TXT file.
    /// Format: [CollectionName] on a line, followed by one online beatmap ID per line.
    /// </summary>
    public async Task ExportCollectionsAsTxtAsync(
        IEnumerable<CollectionInfo> collections,
        string outputPath,
        IProgress<string>? progress = null)
    {
        var sb = new StringBuilder();
        foreach (var col in collections)
        {
            progress?.Report($"Exporting {col.Name}...");
            sb.AppendLine($"[{col.Name}]");
            foreach (var id in col.BeatmapOnlineIds)
            {
                sb.AppendLine(id.ToString());
            }
            sb.AppendLine(); // blank line between collections
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[CollectionService] Exported {collections.Count()} collections to TXT: {outputPath}");
    }

    /// <summary>
    /// Exports selected collections as a ZIP archive.
    /// Each collection is a folder containing .osz beatmap files.
    /// </summary>
    public async Task ExportCollectionsAsZipAsync(
        IEnumerable<CollectionInfo> collections,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var downloadDir = Path.Combine(_settings.Settings.OsuInstallPath, "downloads");
        Directory.CreateDirectory(downloadDir);

        using var zipStream = File.Create(outputPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var col in collections)
        {
            progress?.Report($"Exporting {col.Name} ({col.BeatmapCount} beatmaps)...");
            Console.WriteLine($"[CollectionService] Exporting collection: {col.Name}");

            foreach (var beatmapSetId in col.BeatmapOnlineIds)
            {
                ct.ThrowIfCancellationRequested();

                var oszPath = Path.Combine(downloadDir, $"{beatmapSetId}.osz");

                // Download if not already present
                if (!File.Exists(oszPath))
                {
                    try
                    {
                        var url = $"https://osu.ppy.sh/beatmapsets/{beatmapSetId}/download";
                        var response = await _http.GetAsync(url, ct);
                        if (response.IsSuccessStatusCode)
                        {
                            await using var fs = File.Create(oszPath);
                            await response.Content.CopyToAsync(fs, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CollectionService] Failed to download {beatmapSetId}: {ex.Message}");
                        continue;
                    }
                }

                if (File.Exists(oszPath))
                {
                    var entryName = $"{col.Name}/{beatmapSetId}.osz";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(oszPath);
                    await fileStream.CopyToAsync(entryStream, ct);
                }
            }
        }

        Console.WriteLine($"[CollectionService] Exported {collections.Count()} collections to ZIP: {outputPath}");
    }

    /// <summary>
    /// Imports collections from a TXT file.
    /// </summary>
    public async Task<int> ImportFromTxtAsync(string filePath, IProgress<string>? progress = null)
    {
        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        string? currentCollection = null;
        var collections = new Dictionary<string, List<int>>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentCollection = trimmed[1..^1];
                collections[currentCollection] = new List<int>();
            }
            else if (currentCollection != null && int.TryParse(trimmed, out var id))
            {
                collections[currentCollection].Add(id);
            }
        }

        int imported = 0;
        foreach (var (name, ids) in collections)
        {
            progress?.Report($"Importing {name} ({ids.Count} beatmaps)...");
            Console.WriteLine($"[CollectionService] Importing collection: {name} with {ids.Count} beatmaps.");

            // Get MD5 hashes for the online IDs
            // Note: This requires mapping online IDs to MD5 hashes.
            // In a full implementation, we'd look this up from the beatmap data.
            // For now, log what would happen.
            Console.WriteLine($"[CollectionService] Would import {ids.Count} beatmaps into collection '{name}'");
            imported += ids.Count;
        }

        return imported;
    }

    /// <summary>
    /// Imports collections from a ZIP file.
    /// </summary>
    public async Task<int> ImportFromZipAsync(string filePath, IProgress<string>? progress = null)
    {
        int imported = 0;

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(filePath);
            var collections = new Dictionary<string, List<string>>();

            foreach (var entry in archive.Entries)
            {
                // Entry path format: CollectionName/beatmapId.osz
                var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;

                var collectionName = parts[0];
                var fileName = parts[1];

                if (!collections.ContainsKey(collectionName))
                    collections[collectionName] = new List<string>();

                // Extract .osz to osu! downloads folder
                var osuPath = _settings.Settings.OsuInstallPath;
                var destPath = Path.Combine(osuPath, "downloads", fileName);

                if (!File.Exists(destPath))
                {
                    entry.ExtractToFile(destPath, overwrite: false);
                    Console.WriteLine($"[CollectionService] Extracted: {fileName} to downloads.");
                }

                collections[collectionName].Add(fileName);
            }

            foreach (var (name, files) in collections)
            {
                progress?.Report($"Imported {name}: {files.Count} beatmaps.");
                Console.WriteLine($"[CollectionService] Imported collection '{name}' with {files.Count} beatmaps.");
            }

            imported = collections.Sum(c => c.Value.Count);
        });

        return imported;
    }
}
