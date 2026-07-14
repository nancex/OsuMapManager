using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OsuMapManager.Models;
using OsuMapManager.Models.RealmSchema;
using Realms;

namespace OsuMapManager.Services;

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

    public async Task<List<CollectionInfo>> GetLocalCollectionsAsync() =>
        await _osuData.GetCollectionsAsync();

    // ================================================================
    // TXT Export
    // ================================================================
    public async Task ExportCollectionsAsTxtAsync(
        IEnumerable<CollectionInfo> collections, string outputPath,
        IProgress<string>? progress = null)
    {
        var sb = new StringBuilder();
        foreach (var col in collections)
        {
            progress?.Report($"Exporting {col.Name}...");
            sb.AppendLine($"[{col.Name}]");
            foreach (var id in col.BeatmapOnlineIds) sb.AppendLine(id.ToString());
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[CollectionService] Exported to TXT: {outputPath}");
    }

    // ================================================================
    // ZIP Export
    // ================================================================
    public async Task ExportCollectionsAsZipAsync(
        IEnumerable<CollectionInfo> collections, string outputPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var filesDir = Path.Combine(_osuData.OsuPath, "files");
        var manifest = new ExportManifest { Version = 1 };

        // First, get the full beatmap info to have MD5→Set mapping
        var allBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
        var md5ToSetId = allBeatmaps
            .GroupBy(b => b.BeatmapSetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allFileHashes = new HashSet<string>();
        int processed = 0, total = collections.Sum(c => c.BeatmapOnlineIds.Count);

        // Build mapping: Set OnlineID → all its file hashes (deduplicated)
        var setFileCache = new Dictionary<int, List<(string Hash, string Filename)>>();

        foreach (var col in collections)
        {
            var colEntry = new ExportCollectionEntry { Name = col.Name };
            var seenSets = new HashSet<int>();

            foreach (var beatmapId in col.BeatmapOnlineIds)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress?.Report($"Exporting {col.Name} ({processed}/{total})...");

                // Get set ID for this beatmap
                var bmInfo = allBeatmaps.FirstOrDefault(b => b.OnlineId == beatmapId);
                var setId = bmInfo?.BeatmapSetId ?? beatmapId; // fallback

                if (seenSets.Contains(setId)) continue;
                seenSets.Add(setId);

                // Cache file list per set
                if (!setFileCache.TryGetValue(setId, out var setFiles))
                {
                    setFiles = await _osuData.GetBeatmapSetFilesAsync(setId) ?? new();
                    setFileCache[setId] = setFiles;
                }

                if (setFiles.Count == 0) continue;

                var setEntry = new ExportSetEntry { OnlineId = setId };
                foreach (var (hash, filename) in setFiles)
                {
                    setEntry.Files.Add(new ExportFileEntry { Hash = hash, Filename = filename });
                    allFileHashes.Add(hash);
                }
                colEntry.Sets.Add(setEntry);
            }

            // Store collection-level MD5 hashes
            colEntry.BeatmapMd5Hashes = col.BeatmapOnlineIds
                .Select(id => allBeatmaps.FirstOrDefault(b => b.OnlineId == id))
                .Where(b => b != null)
                .Select(b => b!.MD5Hash)
                .Where(m => !string.IsNullOrEmpty(m))
                .ToList();

            manifest.Collections.Add(colEntry);
        }

        // Write ZIP
        using var zipStream = File.Create(outputPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // manifest.json
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var mEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        using (var ms = mEntry.Open())
        using (var sw = new StreamWriter(ms))
            await sw.WriteAsync(manifestJson);

        // Files
        int copied = 0;
        foreach (var hash in allFileHashes)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(filesDir, hash[..1], hash[..2], hash);
            if (!File.Exists(src)) { Console.WriteLine($"[CollectionService] Missing: {src}"); continue; }
            var entry = archive.CreateEntry($"files/{hash[..1]}/{hash[..2]}/{hash}", CompressionLevel.Fastest);
            using var es = entry.Open();
            using var fs = File.OpenRead(src);
            await fs.CopyToAsync(es, ct);
            copied++;
        }

        progress?.Report($"Done: {manifest.Collections.Count} collections, {copied} files");
        Console.WriteLine($"[CollectionService] ZIP export: {manifest.Collections.Count} collections, {copied} files");
    }

    // ================================================================
    // TXT Import
    // ================================================================
    public async Task<int> ImportFromTxtAsync(string filePath, IProgress<string>? progress = null)
    {
        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        string? currentCollection = null;
        var collections = new Dictionary<string, List<int>>();

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                currentCollection = t[1..^1];
                collections[currentCollection] = new();
            }
            else if (currentCollection != null && int.TryParse(t, out var id))
                collections[currentCollection].Add(id);
        }

        int imported = 0;
        foreach (var (name, ids) in collections)
        {
            progress?.Report($"Importing {name} ({ids.Count} beatmaps)...");
            Console.WriteLine($"[CollectionService] Would import: {name} ({ids.Count} beatmaps)");
            imported += ids.Count;
        }
        return imported;
    }

    // ================================================================
    // ZIP Import: extract files + write collections to Realm
    // ================================================================
    public async Task<int> ImportFromZipAsync(string filePath, IProgress<string>? progress = null)
    {
        var targetOsuPath = _settings.Settings.OsuInstallPath;
        var targetFilesDir = Path.Combine(targetOsuPath, "files");
        var targetRealmPath = Path.Combine(targetOsuPath, "client.realm");

        if (!File.Exists(targetRealmPath))
            throw new FileNotFoundException($"client.realm not found at {targetRealmPath}");

        // Read manifest
        ExportManifest manifest;
        using (var archive = ZipFile.OpenRead(filePath))
        {
            var m = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("manifest.json not found");
            using var s = m.Open();
            manifest = JsonSerializer.Deserialize<ExportManifest>(s)
                ?? throw new InvalidDataException("Bad manifest.json");
        }

        Console.WriteLine($"[CollectionService] Import: {manifest.Collections.Count} collections");

        // Step 1: Extract files
        progress?.Report("Extracting files...");
        int filesCopied = 0;
        using (var archive = ZipFile.OpenRead(filePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith("files/") || string.IsNullOrEmpty(entry.Name))
                    continue;
                var parts = entry.FullName.Split('/');
                if (parts.Length != 4) continue;

                var dest = Path.Combine(targetFilesDir, parts[1], parts[2], parts[3]);
                if (File.Exists(dest)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, false);
                filesCopied++;
            }
        }
        progress?.Report($"Extracted {filesCopied} files");
        Console.WriteLine($"[CollectionService] Extracted {filesCopied} files");

        // Step 2: Write collections to Realm (write mode!)
        progress?.Report("Writing collections to Realm...");
        int written = await WriteCollectionsToRealmAsync(targetRealmPath, manifest, progress);

        progress?.Report($"Done: {written} collections written");
        Console.WriteLine($"[CollectionService] Import done: {filesCopied} files, {written} collections");
        return written;
    }

    private async Task<int> WriteCollectionsToRealmAsync(
        string realmPath, ExportManifest manifest, IProgress<string>? progress)
    {
        return await Task.Run(() =>
        {
            var config = new RealmConfiguration(realmPath)
            {
                SchemaVersion = 51,
                ShouldDeleteIfMigrationNeeded = false
            };

            using var realm = Realm.GetInstance(config);
            int written = 0;

            realm.Write(() =>
            {
                foreach (var colEntry in manifest.Collections)
                {
                    try
                    {
                        // Create RealmFile entries for referenced files
                        foreach (var set in colEntry.Sets)
                            foreach (var file in set.Files)
                                if (realm.Find<RealmFile>(file.Hash) == null) { realm.Add(new RealmFile { Hash = file.Hash }); }

                        // Create or update BeatmapCollection
                        var existing = realm.All<BeatmapCollection>()
                            .FirstOrDefault(c => c.Name == colEntry.Name);

                        if (existing != null)
                        {
                            int added = 0, skipped = 0;
                            foreach (var md5 in colEntry.BeatmapMd5Hashes)
                            {
                                if (!existing.BeatmapMD5Hashes.Contains(md5))
                                {
                                    existing.BeatmapMD5Hashes.Add(md5);
                                    added++;
                                }
                                else skipped++;
                            }
                            existing.LastModified = DateTimeOffset.UtcNow;
                            Console.WriteLine($"[CollectionService]   '{colEntry.Name}': updated ({added} new, {skipped} skipped)");
                        }
                        else
                        {
                            var nc = new BeatmapCollection
                            {
                                Name = colEntry.Name,
                                LastModified = DateTimeOffset.UtcNow
                            };
                            foreach (var md5 in colEntry.BeatmapMd5Hashes)
                                nc.BeatmapMD5Hashes.Add(md5);
                            realm.Add(nc);
                            Console.WriteLine($"[CollectionService]   '{colEntry.Name}': created ({colEntry.BeatmapMd5Hashes.Count} beatmaps)");
                        }

                        written++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CollectionService]   Error '{colEntry.Name}': {ex.Message}");
                    }
                }
            });

            return written;
        });
    }
}

// ================================================================
// Manifest types
// ================================================================
public class ExportManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("collections")] public List<ExportCollectionEntry> Collections { get; set; } = new();
}

public class ExportCollectionEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("beatmapMd5Hashes")] public List<string> BeatmapMd5Hashes { get; set; } = new();
    [JsonPropertyName("sets")] public List<ExportSetEntry> Sets { get; set; } = new();
}

public class ExportSetEntry
{
    [JsonPropertyName("onlineId")] public int OnlineId { get; set; }
    [JsonPropertyName("files")] public List<ExportFileEntry> Files { get; set; } = new();
}

public class ExportFileEntry
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
}






