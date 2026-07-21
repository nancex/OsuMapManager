using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using OsuMapManager.Models;
using OsuMapManager.Models.RealmSchema;
using Realms;

namespace OsuMapManager.Services;

public class OsuDataService : IDisposable
{
    private readonly string _osuPath;
    private readonly RealmConfiguration _realmConfig;
    private Realm? _realm;
    private bool _disposed;
    private bool _opened;

    private readonly object _cacheLock = new();
    private List<LocalBeatmapInfo>? _cachedBeatmaps;
    private Dictionary<string, int>? _md5ToOnlineId;

    public OsuDataService(string osuPath)
    {
        _osuPath = osuPath;
        var realmPath = Path.Combine(_osuPath, "client.realm");

        _realmConfig = new RealmConfiguration(realmPath)
        {
            IsReadOnly = true,
            SchemaVersion = 51,
            ShouldDeleteIfMigrationNeeded = false
        };

        Console.WriteLine($"[OsuDataService] Config ready: {realmPath} (exists={File.Exists(realmPath)})");
    }

    public bool TryOpen()
    {
        if (_opened) return true;
        var sw = Stopwatch.StartNew();
        try
        {
            _realm = Realm.GetInstance(_realmConfig);
            _opened = true;
            sw.Stop();
            Console.WriteLine($"[OsuDataService] Realm opened in {sw.ElapsedMilliseconds}ms.");
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"[OsuDataService] Failed to open Realm in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return false;
        }
    }

    public string OsuPath => _osuPath;

    private async Task<T> RunOnRealmThreadAsync<T>(Func<Realm, T> query)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (_realm == null) TryOpen();
            if (_realm == null) return default!;
            return query(_realm);
        }
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_realm == null) TryOpen();
            if (_realm == null) return default!;
            return query(_realm);
        });
    }

    public async Task<List<LocalBeatmapInfo>> GetLocalBeatmapInfoAsync()
    {
        lock (_cacheLock)
        {
            if (_cachedBeatmaps != null)
            {
                Console.WriteLine($"[OsuDataService] Return cached beatmaps: {_cachedBeatmaps.Count}, md5Map={_md5ToOnlineId?.Count ?? 0}");
                return _cachedBeatmaps;
            }
        }

        Console.WriteLine("[OsuDataService] Reading beatmaps from Realm...");
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await RunOnRealmThreadAsync(realm =>
            {
                var list = new List<LocalBeatmapInfo>();
                var md5Map = new Dictionary<string, int>();
                var allBeatmaps = realm.DynamicApi.All("Beatmap").ToList();

                int total = allBeatmaps.Count;
                int onlineOk = 0, onlineSkip = 0, parseErr = 0;

                foreach (var bm in allBeatmaps)
                {
                    try
                    {
                        var onlineId = Prop<int>(bm, "OnlineID");
                        if (onlineId <= 0) { onlineSkip++; continue; }
                        onlineOk++;

                        var md5Hash = Prop<string>(bm, "MD5Hash") ?? "";
                        var diffName = Prop<string>(bm, "DifficultyName") ?? "";
                        var status = Prop<int>(bm, "Status");
                        var starRating = Prop<double>(bm, "StarRating");

                        var beatmapSet = Prop<dynamic>(bm, "BeatmapSet");
                        var beatmapSetId = beatmapSet != null ? Prop<int>(beatmapSet, "OnlineID") : 0;
                        var dateSubmitted = beatmapSet != null ? Prop<DateTimeOffset?>(beatmapSet, "DateSubmitted") : null;

                        var metadata = Prop<dynamic>(bm, "Metadata");
                        var artist = metadata != null
                            ? (Prop<string>(metadata, "Artist") ?? Prop<string>(metadata, "ArtistUnicode") ?? "")
                            : "";
                        var title = metadata != null
                            ? (Prop<string>(metadata, "Title") ?? Prop<string>(metadata, "TitleUnicode") ?? "")
                            : "";
                        var author = metadata != null ? Prop<dynamic>(metadata, "Author") : null;
                        var creator = author != null ? (Prop<string>(author, "Username") ?? "") : "";

                        var ruleset = Prop<dynamic>(bm, "Ruleset");
                        var rulesetOnlineId = ruleset != null ? Prop<int>(ruleset, "OnlineID") : -1;
                        var mode = GameMode.Osu;
                        if (rulesetOnlineId >= 0 && Enum.IsDefined(typeof(GameMode), rulesetOnlineId))
                            mode = (GameMode)rulesetOnlineId;

                        // Mania key count from BeatmapDifficulty.CircleSize
                        int? keyCount = null;
                        if (mode == GameMode.Mania)
                        {
                            var difficulty = Prop<dynamic>(bm, "Difficulty");
                            if (difficulty != null)
                            {
                                var cs = Prop<float>(difficulty, "CircleSize");
                                keyCount = (int)Math.Round(cs);
                            }
                        }

                        var info = new LocalBeatmapInfo
                        {
                            OnlineId = onlineId,
                            BeatmapSetId = beatmapSetId,
                            Mode = mode,
                            KeyCount = keyCount,
                            DifficultyName = diffName,
                            Artist = artist,
                            Creator = creator,
                            Title = title,
                            Status = (BeatmapStatus)status,
                            StarRating = starRating,
                            DateSubmitted = dateSubmitted,
                            MD5Hash = md5Hash,
                        };

                        list.Add(info);

                        if (!string.IsNullOrEmpty(md5Hash))
                            md5Map[md5Hash] = onlineId;
                    }
                    catch
                    {
                        parseErr++;
                    }
                }

                Console.WriteLine($"[OsuDataService] Parsed {list.Count} beatmaps (total={total}, ok={onlineOk}, skip={onlineSkip}, err={parseErr})");

                lock (_cacheLock)
                {
                    _cachedBeatmaps = list;
                    _md5ToOnlineId = md5Map;
                }

                return list;
            });

            sw.Stop();
            Console.WriteLine($"[OsuDataService] Realm read took {sw.ElapsedMilliseconds}ms.");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuDataService] Error: {ex}");
            return new List<LocalBeatmapInfo>();
        }
    }

    /// <summary>
    /// Count how many local BeatmapSets match a given SyncFilter.
    /// Used for Check Status display.
    /// </summary>
    public async Task<int> CountLocalMatchingSetsAsync(SyncFilter filter)
    {
        var localBeatmaps = await GetLocalBeatmapInfoAsync();

        // Apply the same filtering logic as BeatmapDataService, but on local data
        var filtered = localBeatmaps.AsEnumerable();

        // Genre (not available locally, skip)
        // Mode
        if (filter.Modes.Count > 0)
            filtered = filtered.Where(b => filter.Modes.Contains(b.Mode));

        // Submit date
        if (filter.SubmitDateFrom.HasValue || filter.SubmitDateTo.HasValue)
        {
            filtered = filtered.Where(b =>
                b.DateSubmitted.HasValue &&
                (!filter.SubmitDateFrom.HasValue || b.DateSubmitted.Value >= filter.SubmitDateFrom.Value) &&
                (!filter.SubmitDateTo.HasValue || b.DateSubmitted.Value <= filter.SubmitDateTo.Value));
        }

        // Status
        // Status not reliable in client.realm, skipped for local queries

        // Mania key count
        if (filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == null || b.KeyCount == filter.ManiaKeyCount.Value);

        // Difficulty Rating
        if (filter.DifficultyRatingMin.HasValue || filter.DifficultyRatingMax.HasValue)
        {
            filtered = filtered.Where(b =>
                (!filter.DifficultyRatingMin.HasValue || b.StarRating >= filter.DifficultyRatingMin.Value) &&
                (!filter.DifficultyRatingMax.HasValue || b.StarRating <= filter.DifficultyRatingMax.Value));
        }

        // Artist (case-insensitive contains)
        if (!string.IsNullOrWhiteSpace(filter.Artist))
        {
            var artistLower = filter.Artist.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Artist?.ToLowerInvariant().Contains(artistLower) ?? false));
        }


        // Title (case-insensitive contains)
        if (!string.IsNullOrWhiteSpace(filter.Title))
        {
            var titleLower = filter.Title.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Title?.ToLowerInvariant().Contains(titleLower) ?? false));
        }
        // Creator (case-insensitive contains)
        if (!string.IsNullOrWhiteSpace(filter.Creator))
        {
            var creatorLower = filter.Creator.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Creator?.ToLowerInvariant().Contains(creatorLower) ?? false));
        }

        return filtered.Select(b => b.BeatmapSetId).Distinct().Count();
    }

    /// <summary>
    /// Get the set of BeatmapSet IDs that match the filter locally.
    /// Used for Check Status intersection calculation.
    /// </summary>
    public async Task<HashSet<int>> GetLocalMatchingSetIdsAsync(SyncFilter filter)
    {
        var localBeatmaps = await GetLocalBeatmapInfoAsync();

        var filtered = localBeatmaps.AsEnumerable();

        if (filter.Modes.Count > 0)
            filtered = filtered.Where(b => filter.Modes.Contains(b.Mode));

        if (filter.SubmitDateFrom.HasValue || filter.SubmitDateTo.HasValue)
        {
            filtered = filtered.Where(b =>
                b.DateSubmitted.HasValue &&
                (!filter.SubmitDateFrom.HasValue || b.DateSubmitted.Value >= filter.SubmitDateFrom.Value) &&
                (!filter.SubmitDateTo.HasValue || b.DateSubmitted.Value <= filter.SubmitDateTo.Value));
        }

        if (filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == null || b.KeyCount == filter.ManiaKeyCount.Value);

        if (filter.DifficultyRatingMin.HasValue || filter.DifficultyRatingMax.HasValue)
        {
            filtered = filtered.Where(b =>
                (!filter.DifficultyRatingMin.HasValue || b.StarRating >= filter.DifficultyRatingMin.Value) &&
                (!filter.DifficultyRatingMax.HasValue || b.StarRating <= filter.DifficultyRatingMax.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Artist))
        {
            var artistLower = filter.Artist.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Artist?.ToLowerInvariant().Contains(artistLower) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(filter.Title))
        {
            var titleLower = filter.Title.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Title?.ToLowerInvariant().Contains(titleLower) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(filter.Creator))
        {
            var creatorLower = filter.Creator.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                (b.Creator?.ToLowerInvariant().Contains(creatorLower) ?? false));
        }

        return filtered.Select(b => b.BeatmapSetId).ToHashSet();
    }






    public async Task<List<CollectionInfo>> GetCollectionsAsync()
    {
        if (_cachedBeatmaps == null)
        {
            await GetLocalBeatmapInfoAsync();
        }

        Dictionary<string, int> md5Map;
        lock (_cacheLock)
        { md5Map = _md5ToOnlineId != null ? new(_md5ToOnlineId) : new(); }

        Console.WriteLine($"[OsuDataService] GetCollectionsAsync: using MD5 map with {md5Map.Count} entries");

        try
        {
            return await RunOnRealmThreadAsync(realm =>
            {
                var result = new List<CollectionInfo>();
                var allCollections = realm.All<BeatmapCollection>().ToList();
                Console.WriteLine($"[OsuDataService] Collections: {allCollections.Count}");

                foreach (var col in allCollections)
                {
                    try
                    {
                        var name = col.Name ?? "Unnamed";
                        var onlineIds = new List<int>();
                        foreach (var md5 in col.BeatmapMD5Hashes)
                        {
                            if (!string.IsNullOrEmpty(md5) && md5Map.TryGetValue(md5, out var oid))
                                onlineIds.Add(oid);
                        }
                        Console.WriteLine($"[OsuDataService]   Collection \"{name}\": {onlineIds.Count}/{col.BeatmapMD5Hashes.Count} beatmaps matched");
                        result.Add(new CollectionInfo
                        {
                            Name = name,
                            BeatmapCount = onlineIds.Count,
                            BeatmapOnlineIds = onlineIds
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OsuDataService] Parse collection err: {ex.Message}");
                    }
                }

                Console.WriteLine($"[OsuDataService] Parsed {result.Count} collections.");
                return result;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuDataService] Collection error: {ex}");
            return new List<CollectionInfo>();
        }
    }

    /// <summary>
    /// Gets all files belonging to a beatmap set, identified by its OnlineID.
    /// Returns list of (Hash, Filename) tuples for reading from local files/ directory.
    /// </summary>
    public async Task<List<(string Hash, string Filename)>?> GetBeatmapSetFilesAsync(int beatmapSetOnlineId)
    {
        try
        {
            return await RunOnRealmThreadAsync(realm =>
            {
                var set = realm.All<BeatmapSetInfo>().FirstOrDefault(s => s.OnlineID == beatmapSetOnlineId);
                if (set == null) return null;

                var files = new List<(string Hash, string Filename)>();
                foreach (var usage in set.Files)
                {
                    if (!string.IsNullOrEmpty(usage.File?.Hash) && !string.IsNullOrEmpty(usage.Filename))
                        files.Add((usage.File.Hash, usage.Filename));
                }
                Console.WriteLine($"[OsuDataService] Set {beatmapSetOnlineId}: {files.Count} files found");
                return files;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuDataService] GetBeatmapSetFilesAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<(int Count, long TotalBytes)> GetLocalStatsAsync()
    {
        if (_cachedBeatmaps == null) await GetLocalBeatmapInfoAsync();
        List<LocalBeatmapInfo>? beatmaps;
        lock (_cacheLock) { beatmaps = _cachedBeatmaps; }
        if (beatmaps == null) return (0, 0);

        var uniqueSets = beatmaps.GroupBy(b => b.BeatmapSetId).ToList();
        long totalSize = 0;
        var filesDir = Path.Combine(_osuPath, "files");
        if (Directory.Exists(filesDir))
        {
            try { totalSize = Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch { }
        }
        return (uniqueSets.Count, totalSize);
    }

    public void Close()
    {
        if (!_disposed)
        {
            _realm?.Dispose();
            _realm = null;
            _opened = false;
            Console.WriteLine("[OsuDataService] Realm closed for external write access.");
        }
    }

    public void ClearCache() { lock (_cacheLock) { _cachedBeatmaps = null; _md5ToOnlineId = null; } }
    public void Dispose() { if (!_disposed) { _realm?.Dispose(); _disposed = true; } }

    #region DynamicApi reflection helpers

    private static T Prop<T>(dynamic obj, string name)
    {
        try
        {
            var val = obj.GetType().GetProperty(name)!.GetValue(obj);
            if (val == null) return default!;
            if (val is T t) return t;
            return (T)Convert.ChangeType(val, typeof(T));
        }
        catch { return default!; }
    }

    #endregion

    public async Task<List<QueryBeatmapSetResult>> QueryLocalBeatmapSetsAsync(SyncFilter filter)
    {
        var localBeatmaps = await GetLocalBeatmapInfoAsync();
        var filtered = localBeatmaps.AsEnumerable();

        if (filter.Modes.Count > 0)
            filtered = filtered.Where(b => filter.Modes.Contains(b.Mode));

        if (filter.SubmitDateFrom.HasValue || filter.SubmitDateTo.HasValue)
            filtered = filtered.Where(b =>
                b.DateSubmitted.HasValue &&
                (!filter.SubmitDateFrom.HasValue || b.DateSubmitted.Value >= filter.SubmitDateFrom.Value) &&
                (!filter.SubmitDateTo.HasValue || b.DateSubmitted.Value <= filter.SubmitDateTo.Value));

        // Status not reliable in client.realm, skipped for local queries

        if (filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == null || b.KeyCount == filter.ManiaKeyCount.Value);

        if (filter.DifficultyRatingMin.HasValue || filter.DifficultyRatingMax.HasValue)
            filtered = filtered.Where(b =>
                (!filter.DifficultyRatingMin.HasValue || b.StarRating >= filter.DifficultyRatingMin.Value) &&
                (!filter.DifficultyRatingMax.HasValue || b.StarRating <= filter.DifficultyRatingMax.Value));

        if (!string.IsNullOrWhiteSpace(filter.Artist))
        {
            var a = filter.Artist.Trim().ToLowerInvariant();
            filtered = filtered.Where(b => (b.Artist?.ToLowerInvariant().Contains(a) ?? false));
        }
        if (!string.IsNullOrWhiteSpace(filter.Creator))
        {
            var c = filter.Creator.Trim().ToLowerInvariant();
            filtered = filtered.Where(b => (b.Creator?.ToLowerInvariant().Contains(c) ?? false));
        }
        if (!string.IsNullOrWhiteSpace(filter.Title))
        {
            var t = filter.Title.Trim().ToLowerInvariant();
            filtered = filtered.Where(b => (b.Title?.ToLowerInvariant().Contains(t) ?? false));
        }

        var results = filtered
            .GroupBy(b => b.BeatmapSetId)
            .Select(g =>
            {
                var first = g.First();
                return new QueryBeatmapSetResult
                {
                    Id = first.BeatmapSetId,
                    Genre = "N/A",
                    Title = first.Title ?? string.Empty,
                    Artist = first.Artist ?? string.Empty,
                    Creator = first.Creator ?? string.Empty,
                    SubmitDate = first.DateSubmitted,
                    BeatmapCount = g.Count()
                };
            })
            .ToList();

        Console.WriteLine($"[OsuDataService] QueryLocal: {results.Count} sets.");
        return results;
    }
}
