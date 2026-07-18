using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OsuMapManager.Models;

namespace OsuMapManager.Services;

/// <summary>
/// Reads beatmap metadata from a Python-generated SQLite database
/// (created by fetch_catboy.py).
/// </summary>
public class BeatmapDataService
{
    private readonly string _dbPath;
    private bool _dataReady;

    public bool IsDataReady => _dataReady;

    private List<BeatmapEntry>? _allBeatmaps;
    private Dictionary<int, BeatmapSetEntry>? _beatmapSets;

    public BeatmapDataService(string dbPath)
    {
        _dbPath = dbPath;
        CheckDataReady();
    }

    /// <summary>
    /// Re-check if the database file exists and update IsDataReady.
    /// Call this after changing the path.
    /// </summary>
    public void RefreshDataReady()
    {
        CheckDataReady();
        _allBeatmaps = null;
        _beatmapSets = null;
    }

    private void CheckDataReady()
    {
        _dataReady = File.Exists(_dbPath);
        Console.WriteLine($"[BeatmapDataService] DB path: {_dbPath}, ready: {_dataReady}");
    }

    // ================================================================
    // Public filtered queries
    // ================================================================

    /// <summary>
    /// Get BeatmapSet IDs matching a single SyncFilter.
    /// </summary>
    public async Task<HashSet<int>> GetFilteredBeatmapSetIdsAsync(SyncFilter filter)
    {
        await EnsureDataLoadedAsync();
        if (_allBeatmaps == null || _beatmapSets == null)
            return new HashSet<int>();

        return ApplyFilterOnBeatmaps(_allBeatmaps, _beatmapSets, filter)
            .Select(b => b.BeatmapSetId)
            .ToHashSet();
    }

    /// <summary>
    /// Get BeatmapSet IDs matching ANY of the given filters (union).
    /// </summary>
    public async Task<HashSet<int>> GetUnionBeatmapSetIdsAsync(IEnumerable<SyncFilter> filters)
    {
        await EnsureDataLoadedAsync();
        if (_allBeatmaps == null || _beatmapSets == null)
            return new HashSet<int>();

        var unionIds = new HashSet<int>();
        foreach (var filter in filters)
        {
            var ids = ApplyFilterOnBeatmaps(_allBeatmaps, _beatmapSets, filter)
                .Select(b => b.BeatmapSetId);
            unionIds.UnionWith(ids);
        }

        Console.WriteLine($"[BeatmapDataService] Union filters: {unionIds.Count} unique set IDs.");
        return unionIds;
    }

    /// <summary>
    /// Count BeatmapSet IDs matching a single SyncFilter (for Check Status).
    /// </summary>
    public async Task<int> CountFilteredBeatmapSetsAsync(SyncFilter filter)
    {
        await EnsureDataLoadedAsync();
        if (_allBeatmaps == null || _beatmapSets == null)
            return 0;

        return ApplyFilterOnBeatmaps(_allBeatmaps, _beatmapSets, filter)
            .Select(b => b.BeatmapSetId)
            .Distinct()
            .Count();
    }

    // ================================================================
    // Core filtering logic (beatmap-level)
    // ================================================================

    private static IEnumerable<BeatmapEntry> ApplyFilterOnBeatmaps(
        List<BeatmapEntry> allBeatmaps,
        Dictionary<int, BeatmapSetEntry> beatmapSets,
        SyncFilter filter)
    {
        var filtered = allBeatmaps.AsEnumerable();

        // --- Genre ---
        if (filter.Genres.Count > 0 && !filter.Genres.Contains(BeatmapGenre.Any))
            filtered = filtered.Where(b => filter.Genres.Contains(b.GenreId));

        // --- Mode ---
        if (filter.Modes.Count > 0)
            filtered = filtered.Where(b => filter.Modes.Contains(b.Mode));

        // --- Submit date range ---
        if (filter.SubmitDateFrom.HasValue || filter.SubmitDateTo.HasValue)
        {
            filtered = filtered.Where(b =>
                beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                IsInSubmitDateRange(set, filter.SubmitDateFrom, filter.SubmitDateTo));
        }

        // --- Status ---
        filtered = filtered.Where(b => IsStatusMatch(b.Approved, filter));

        // --- Mania key count ---
        if (filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == filter.ManiaKeyCount.Value);

        // --- Difficulty Rating ---
        if (filter.DifficultyRatingMin.HasValue || filter.DifficultyRatingMax.HasValue)
        {
            filtered = filtered.Where(b =>
                IsInDifficultyRange(b.DifficultyRating, filter.DifficultyRatingMin, filter.DifficultyRatingMax));
        }

        // --- Artist (case-insensitive contains, checked at set level) ---
        if (!string.IsNullOrWhiteSpace(filter.Artist))
        {
            var artistLower = filter.Artist.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                (set.Artist?.ToLowerInvariant().Contains(artistLower) ?? false));
        }

        // --- Creator (case-insensitive contains, checked at set level) ---
        if (!string.IsNullOrWhiteSpace(filter.Creator))
        {
            var creatorLower = filter.Creator.Trim().ToLowerInvariant();
            filtered = filtered.Where(b =>
                beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                (set.Creator?.ToLowerInvariant().Contains(creatorLower) ?? false));
        }

        return filtered;
    }

    // ================================================================
    // Load from Python-generated SQLite
    // ================================================================

    private async Task EnsureDataLoadedAsync()
    {
        if (!_dataReady) return;
        if (_allBeatmaps == null || _beatmapSets == null)
            await LoadBeatmapDataAsync();
    }

    private async Task LoadBeatmapDataAsync()
    {
        _allBeatmaps = new List<BeatmapEntry>();
        _beatmapSets = new Dictionary<int, BeatmapSetEntry>();

        await Task.Run(() =>
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();

            // --- Load beatmap sets ---
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT id, bpm, nsfw, tags, user_id, creator, genre_id,
                           title, title_unicode, video, artist, artist_unicode,
                           ranked, rating, source, language_id, ranked_date, submitted_date
                    FROM beatmap_sets";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var set = new BeatmapSetEntry
                    {
                        BeatmapSetId = reader.GetInt32(0),
                        Artist = SafeString(reader, 10),
                        Title = SafeString(reader, 8),
                        Creator = SafeString(reader, 5),
                        GenreId = (BeatmapGenre)SafeInt(reader, 6),
                        LanguageId = (BeatmapLanguage)SafeInt(reader, 15),
                        HasVideo = SafeBool(reader, 9),
                        FavouriteCount = 0,
                        PlayCount = 0,
                        Approved = MapRankedStatus(SafeInt(reader, 12)),
                    };

                    // Parse ranked_date → ApprovedDate + ReleaseYear
                    var rankedDateStr = SafeString(reader, 16);
                    if (!string.IsNullOrEmpty(rankedDateStr) && DateTimeOffset.TryParse(rankedDateStr, out var rankedDt))
                    {
                        set.ApprovedDate = rankedDt;
                        set.ReleaseYear = rankedDt.Year;
                    }

                    // Parse submitted_date → SubmittedDate
                    var submittedDateStr = SafeString(reader, 17);
                    if (!string.IsNullOrEmpty(submittedDateStr) && DateTimeOffset.TryParse(submittedDateStr, out var submittedDt))
                    {
                        set.SubmittedDate = submittedDt;
                    }

                    _beatmapSets[set.BeatmapSetId] = set;
                }
            }
            Console.WriteLine($"[BeatmapDataService] Loaded {_beatmapSets.Count} beatmap sets.");

            // --- Load beatmaps ---
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
                    SELECT id, beatmapset_id, ar, cs, bpm, mode, drain,
                           ranked, status, version, accuracy, checksum,
                           mode_int, difficulty_rating
                    FROM beatmaps";
                using var reader = cmd2.ExecuteReader();
                while (reader.Read())
                {
                    var entry = new BeatmapEntry
                    {
                        BeatmapId = reader.GetInt32(0),
                        BeatmapSetId = reader.GetInt32(1),
                        Mode = (GameMode)SafeInt(reader, 12),     // mode_int
                        Approved = MapRankedStatus(SafeInt(reader, 7)), // ranked
                        Version = SafeString(reader, 9),
                        DifficultyRating = SafeDouble(reader, 13),
                        BPM = SafeDouble(reader, 3),
                        TotalLength = 0,
                        HitLength = 0,
                    };

                    // Carry over artist/title/genre/language from set
                    if (_beatmapSets.TryGetValue(entry.BeatmapSetId, out var set))
                    {
                        entry.Artist = set.Artist;
                        entry.Title = set.Title;
                        entry.GenreId = set.GenreId;
                        entry.LanguageId = set.LanguageId;
                    }

                    // Mania key count comes from CS (Circle Size) column
                    if (entry.Mode == GameMode.Mania)
                        entry.KeyCount = (int)Math.Round(SafeDouble(reader, 3));

                    _allBeatmaps.Add(entry);
                }
            }
            Console.WriteLine($"[BeatmapDataService] Loaded {_allBeatmaps.Count} beatmaps.");
        });
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string SafeString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static int SafeInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    private static double SafeDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0.0 : reader.GetDouble(ordinal);

    private static bool SafeBool(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) != 0;

    private static BeatmapStatus MapRankedStatus(int ranked)
    {
        return ranked switch
        {
            1 => BeatmapStatus.Ranked,
            4 => BeatmapStatus.Loved,
            3 => BeatmapStatus.Qualified,
            2 => BeatmapStatus.Approved,
            _ => BeatmapStatus.Pending,
        };
    }



    private static bool IsInSubmitDateRange(BeatmapSetEntry set, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!set.SubmittedDate.HasValue) return true;
        var date = set.SubmittedDate.Value;
        if (from.HasValue && date < from.Value) return false;
        if (to.HasValue && date > to.Value) return false;
        return true;
    }

    private static bool IsInDifficultyRange(double difficulty, double? min, double? max)
    {
        if (min.HasValue && difficulty < min.Value) return false;
        if (max.HasValue && difficulty > max.Value) return false;
        return true;
    }

    private static bool IsStatusMatch(BeatmapStatus status, SyncFilter filter)
    {
        return status switch
        {
            BeatmapStatus.Ranked => filter.IncludeRanked,
            BeatmapStatus.Approved => filter.IncludeApproved,
            BeatmapStatus.Loved => filter.IncludeLoved,
            BeatmapStatus.Qualified => filter.IncludeQualified,
            _ => false
        };
    }
}
