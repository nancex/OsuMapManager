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
/// (created by CatboyDataFetcher/fetch_catboy.py).
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
    // Filtered queries
    // ================================================================

    public async Task<List<BeatmapEntry>> GetFilteredBeatmapsAsync(SyncFilter filter)
    {
        if (!_dataReady)
            return new List<BeatmapEntry>();

        if (_allBeatmaps == null)
            await LoadBeatmapDataAsync();

        if (_allBeatmaps == null)
            return new List<BeatmapEntry>();

        var filtered = _allBeatmaps.AsEnumerable();

        // Filter by genre
        if (filter.Genres.Count > 0 && !filter.Genres.Contains(BeatmapGenre.Any))
            filtered = filtered.Where(b => filter.Genres.Contains(b.GenreId));

        // Filter by mode
        if (filter.Modes.Count > 0)
            filtered = filtered.Where(b => filter.Modes.Contains(b.Mode));

        // Filter by date range (year-month-day) or year range
        if (_beatmapSets != null)
        {
            if (filter.DateFrom.HasValue || filter.DateTo.HasValue)
            {
                filtered = filtered.Where(b =>
                    _beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                    IsInDateRange(set, filter.DateFrom, filter.DateTo));
            }
            else
            {
                filtered = filtered.Where(b =>
                    _beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                    IsInYearRange(set, filter.YearFrom, filter.YearTo));
            }
        }

        // Filter by status
        filtered = filtered.Where(b => IsStatusMatch(b.Approved, filter));

        // Filter by mania key count
        if (filter.Modes.Count == 1 && filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == filter.ManiaKeyCount.Value);

        return filtered.ToList();
    }

    public async Task<HashSet<int>> GetFilteredBeatmapSetIdsAsync(SyncFilter filter)
    {
        var beatmaps = await GetFilteredBeatmapsAsync(filter);
        return beatmaps.Select(b => b.BeatmapSetId).ToHashSet();
    }

    // ================================================================
    // Load from Python-generated SQLite
    // ================================================================

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
                           ranked, rating, source, language_id, ranked_date
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
                        Approved = MapRankedStatus(SafeInt(reader, 12)),
                        FavouriteCount = 0,
                        PlayCount = 0,
                    };

                    // Parse ranked_date
                    var dateStr = SafeString(reader, 16);
                    if (!string.IsNullOrEmpty(dateStr) && DateTimeOffset.TryParse(dateStr, out var dt))
                    {
                        set.ApprovedDate = dt;
                        set.ReleaseYear = dt.Year;
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

                    // Extract mania key count from version string
                    if (entry.Mode == GameMode.Mania)
                        entry.KeyCount = ExtractManiaKeyCount(entry.Version);

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

    /// <summary>
    /// Maps catboy "ranked" field values to BeatmapStatus.
    /// catboy: ranked=1 → Ranked, ranked=0 → Pending/other
    /// </summary>
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

    private static int? ExtractManiaKeyCount(string version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        if (version.Length >= 2 && version[1] == 'K' && char.IsDigit(version[0]))
            return int.Parse(version[0].ToString());
        if (version.Length >= 3 && version[2] == 'K' && char.IsDigit(version[0]) && char.IsDigit(version[1]))
            return int.Parse(version[..2]);
        return null;
    }

    private static bool IsInDateRange(BeatmapSetEntry set, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!set.ApprovedDate.HasValue) return true;
        var date = set.ApprovedDate.Value;
        if (from.HasValue && date < from.Value) return false;
        if (to.HasValue && date > to.Value) return false;
        return true;
    }

    private static bool IsInYearRange(BeatmapSetEntry set, int from, int to)
    {
        if (!set.ApprovedDate.HasValue) return true;
        var year = set.ApprovedDate.Value.Year;
        return year >= from && year <= to;
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
