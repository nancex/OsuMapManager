using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OsuMapManager.Models;
using OsuMapManager.Models.RealmSchema;
using Realms;

namespace OsuMapManager.Services;

public class CollectionService
{
    private readonly OsuDataService _osuData;
    private readonly SettingsService _settings;

    public CollectionService(OsuDataService osuData, SettingsService settings)
    {
        _osuData = osuData;
        _settings = settings;
    }

    public async Task<List<CollectionInfo>> GetLocalCollectionsAsync() =>
        await _osuData.GetCollectionsAsync();

    // ================================================================
    // TXT Export: format "[CollectionName]", then "setId diffId" per line
    // ================================================================
    public async Task ExportCollectionsAsTxtAsync(
        IEnumerable<CollectionInfo> collections, string outputPath,
        double? diffMin = null, double? diffMax = null,
        IProgress<string>? progress = null)
    {
        var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();

        // Build difficulty ID -> star rating lookup for filtering
        var diffStars = localBeatmaps
            .Where(b => b.OnlineId > 0)
            .ToDictionary(b => b.OnlineId, b => b.StarRating);

        var sb = new StringBuilder();
        foreach (var col in collections)
        {
            progress?.Report($"Exporting {col.Name}...");
            sb.AppendLine($"[{col.Name}]");

            var filtered = col.Beatmaps.AsEnumerable();
            if (diffMin.HasValue || diffMax.HasValue)
            {
                filtered = filtered.Where(b =>
                    diffStars.TryGetValue(b.DifficultyId, out var sr) &&
                    (!diffMin.HasValue || sr >= diffMin.Value) &&
                    (!diffMax.HasValue || sr <= diffMax.Value));
            }

            foreach (var bm in filtered)
                sb.AppendLine($"{bm.BeatmapSetId} {bm.DifficultyId}");

            sb.AppendLine();
        }
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[CollectionService] Exported to TXT: {outputPath}");
    }

    // ================================================================
    // TXT Parse: reads "setId diffId" format per line
    // ================================================================
    public static async Task<Dictionary<string, List<BeatmapRef>>> ParseTxtFileAsync(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        string? currentCollection = null;
        var collections = new Dictionary<string, List<BeatmapRef>>();

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                currentCollection = t[1..^1];
                collections[currentCollection] = new();
            }
            else if (currentCollection != null)
            {
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var setId) &&
                    int.TryParse(parts[1], out var diffId))
                {
                    collections[currentCollection].Add(new BeatmapRef
                    {
                        BeatmapSetId = setId,
                        DifficultyId = diffId
                    });
                }
                // Backward compat: single number = difficulty ID only
                else if (int.TryParse(parts[0], out var singleId))
                {
                    collections[currentCollection].Add(new BeatmapRef
                    {
                        BeatmapSetId = 0,
                        DifficultyId = singleId
                    });
                }
            }
        }

        Console.WriteLine($"[CollectionService] Parsed {collections.Count} collections from TXT.");
        return collections;
    }

    // ================================================================
    // Get per-collection import status (total vs local)
    // ================================================================
    public async Task<List<ImportCollectionStatus>> GetImportStatusAsync(
        Dictionary<string, List<BeatmapRef>> txtCollections)
    {
        var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
        var localOnlineIds = localBeatmaps.Select(b => b.OnlineId).ToHashSet();

        var statuses = new List<ImportCollectionStatus>();
        foreach (var (name, beatmaps) in txtCollections)
        {
            var local = beatmaps.Count(bm => localOnlineIds.Contains(bm.DifficultyId));

            // Build MD5 hash set for this collection's beatmaps that exist locally
            var localMd5ForCollection = beatmaps
                .Where(bm => localOnlineIds.Contains(bm.DifficultyId))
                .Select(bm => localBeatmaps.FirstOrDefault(b => b.OnlineId == bm.DifficultyId)?.MD5Hash)
                .Where(md5 => !string.IsNullOrEmpty(md5))
                .Select(md5 => md5!)
                .ToHashSet();

            // Check how many of these are already in the target collection in Realm
            int inCollection = 0;
            try
            {
                var existingCollections = await _osuData.GetCollectionsAsync();
                var targetCol = existingCollections.FirstOrDefault(c => c.Name == name);
                if (targetCol != null)
                {
                    var targetMd5Set = targetCol.Beatmaps
                        .Where(bm => !string.IsNullOrEmpty(localBeatmaps.FirstOrDefault(lb => lb.OnlineId == bm.DifficultyId)?.MD5Hash))
                        .Select(bm => localBeatmaps.First(lb => lb.OnlineId == bm.DifficultyId).MD5Hash)
                        .Where(md5 => !string.IsNullOrEmpty(md5))
                        .ToHashSet();
                    inCollection = localMd5ForCollection.Count(md5 => md5 != null && targetMd5Set.Contains(md5));
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CollectionService] Could not check existing collection: {ex.Message}"); }

            statuses.Add(new ImportCollectionStatus
            {
                Name = name,
                TotalBeatmaps = beatmaps.Count,
                LocalBeatmaps = local,
                MissingBeatmaps = beatmaps.Count - local,
                InCollection = inCollection
            });
        }

        Console.WriteLine($"[CollectionService] Import status: {statuses.Count} collections");
        return statuses;
    }

    // ================================================================
    // Get missing beatmap SET IDs (for download)
    // ================================================================
    public async Task<HashSet<int>> GetMissingBeatmapIdsAsync(
        Dictionary<string, List<BeatmapRef>> txtCollections)
    {
        var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
        var localSetIds = localBeatmaps.Select(b => b.BeatmapSetId).ToHashSet();
        var allTxtSetIds = txtCollections.Values
            .SelectMany(bms => bms.Select(bm => bm.BeatmapSetId))
            .Where(id => id > 0)
            .ToHashSet();
        var missing = allTxtSetIds.Where(id => !localSetIds.Contains(id)).ToHashSet();
        Console.WriteLine($"[CollectionService] Missing beatmap set IDs: {missing.Count}");
        return missing;
    }

    // ================================================================
    // Apply collections: write TXT-based collections to client.realm
    // Uses difficulty IDs to find MD5 hashes
    // ================================================================
    public async Task ApplyCollectionsAsync(
        Dictionary<string, List<BeatmapRef>> txtCollections)
    {
        var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
        var diffIdToMd5 = localBeatmaps
            .Where(b => !string.IsNullOrEmpty(b.MD5Hash) && b.OnlineId > 0)
            .GroupBy(b => b.OnlineId)
            .ToDictionary(g => g.Key, g => g.First().MD5Hash ?? "");

        var realmPath = Path.Combine(_osuData.OsuPath, "client.realm");

        await Task.Run(() =>
        {
            var config = new RealmConfiguration(realmPath)
            {
                SchemaVersion = 51,
                ShouldDeleteIfMigrationNeeded = false
            };

            using var realm = Realm.GetInstance(config);

            realm.Write(() =>
            {
                foreach (var (name, beatmaps) in txtCollections)
                {
                    var md5Hashes = new List<string>();
                    foreach (var bm in beatmaps)
                    {
                        if (diffIdToMd5.TryGetValue(bm.DifficultyId, out var md5) && !string.IsNullOrEmpty(md5))
                            md5Hashes.Add(md5);
                    }

                    if (md5Hashes.Count == 0)
                    {
                        Console.WriteLine($"[CollectionService]   '{name}': no local beatmaps, skipping.");
                        continue;
                    }

                    var existing = realm.All<BeatmapCollection>()
                        .FirstOrDefault(c => c.Name == name);

                    if (existing != null)
                    {
                        int added = 0, skipped = 0;
                        foreach (var md5 in md5Hashes)
                        {
                            if (!existing.BeatmapMD5Hashes.Contains(md5))
                            {
                                existing.BeatmapMD5Hashes.Add(md5);
                                added++;
                            }
                            else skipped++;
                        }
                        existing.LastModified = DateTimeOffset.UtcNow;
                        Console.WriteLine($"[CollectionService]   '{name}': updated ({added} new, {skipped} skipped)");
                    }
                    else
                    {
                        var col = new BeatmapCollection
                        {
                            ID = Guid.NewGuid(),
                            Name = name,
                            LastModified = DateTimeOffset.UtcNow
                        };
                        foreach (var md5 in md5Hashes)
                            col.BeatmapMD5Hashes.Add(md5);
                        realm.Add(col);
                        Console.WriteLine($"[CollectionService]   '{name}': created ({md5Hashes.Count} beatmaps)");
                    }
                }
            });
        });

        Console.WriteLine("[CollectionService] ApplyCollections complete.");
    }
}
