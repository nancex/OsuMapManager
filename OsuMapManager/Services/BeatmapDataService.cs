using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Data.Sqlite;
using OsuMapManager.Models;

namespace OsuMapManager.Services;

/// <summary>
/// Manages beatmap metadata by downloading and parsing osu! data dumps.
/// </summary>
public class BeatmapDataService
{
    private readonly HttpClient _http = new();
    private readonly string _dataDir;
    private bool _dataReady;

    public bool IsDataReady => _dataReady;

    // Cached beatmap data for filtering
    private List<BeatmapEntry>? _allBeatmaps;
    private Dictionary<int, BeatmapSetEntry>? _beatmapSets;

    private const string BEATMAPS_URL = "https://data.ppy.sh/osu_beatmaps.tar.bz2";
    private const string BEATMAPSETS_URL = "https://data.ppy.sh/osu_beatmapsets.tar.bz2";

    public BeatmapDataService(string baseDataDir)
    {
        _dataDir = baseDataDir;
        Directory.CreateDirectory(_dataDir);
        CheckDataReady();
    }

    /// <summary>
    /// Checks if beatmap data files already exist and are ready.
    /// </summary>
    private void CheckDataReady()
    {
        var beatmapDb = Path.Combine(_dataDir, "osu_beatmaps.db");
        var beatmapSetDb = Path.Combine(_dataDir, "osu_beatmapsets.db");
        _dataReady = File.Exists(beatmapDb) && File.Exists(beatmapSetDb);
        Console.WriteLine($"[BeatmapDataService] Data ready: {_dataReady}");
    }

    /// <summary>
    /// Downloads and extracts beatmap data from data.ppy.sh.
    /// Reports progress via callback.
    /// </summary>
    public async Task<bool> FetchBeatmapDataAsync(
        IProgress<(string Stage, double Progress)>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(("Downloading beatmap data...", 0.05));

            // Download beatmaps tar.bz2
            var beatmapsArchive = Path.Combine(_dataDir, "osu_beatmaps.tar.bz2");
            await DownloadFileAsync(BEATMAPS_URL, beatmapsArchive, p => progress?.Report(("Downloading beatmaps...", 0.05 + p * 0.40)), ct);

            progress?.Report(("Downloading beatmap sets...", 0.45));
            var beatmapSetsArchive = Path.Combine(_dataDir, "osu_beatmapsets.tar.bz2");
            await DownloadFileAsync(BEATMAPSETS_URL, beatmapSetsArchive, p => progress?.Report(("Downloading beatmap sets...", 0.45 + p * 0.40)), ct);

            // Extract beatmaps SQL
            progress?.Report(("Extracting beatmaps...", 0.85));
            await Task.Run(() => ExtractBz2Tar(beatmapsArchive, _dataDir), ct);

            // Extract beatmap sets SQL
            progress?.Report(("Extracting beatmap sets...", 0.90));
            await Task.Run(() => ExtractBz2Tar(beatmapSetsArchive, _dataDir), ct);

            // Convert SQL to SQLite for efficient querying
            progress?.Report(("Building database...", 0.93));
            await Task.Run(() => BuildSqliteDb("osu_beatmaps.sql", "osu_beatmaps.db"), ct);
            await Task.Run(() => BuildSqliteDb("osu_beatmapsets.sql", "osu_beatmapsets.db"), ct);

            // Clean up archives
            TryDelete(beatmapsArchive);
            TryDelete(beatmapSetsArchive);
            TryDelete(Path.Combine(_dataDir, "osu_beatmaps.sql"));
            TryDelete(Path.Combine(_dataDir, "osu_beatmapsets.sql"));

            _dataReady = true;
            _allBeatmaps = null; // Reset cache
            _beatmapSets = null;

            progress?.Report(("Done!", 1.0));
            Console.WriteLine("[BeatmapDataService] Beatmap data fetched and processed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BeatmapDataService] Failed to fetch data: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets all beatmap entries matching the given filter.
    /// </summary>
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

        // Filter by year (via beatmap set)
        if (_beatmapSets != null)
        {
            filtered = filtered.Where(b =>
                _beatmapSets.TryGetValue(b.BeatmapSetId, out var set) &&
                IsInYearRange(set, filter.YearFrom, filter.YearTo));
        }

        // Filter by status
        filtered = filtered.Where(b => IsStatusMatch(b.Approved, filter));

        // Filter by mania key count
        if (filter.Modes.Count == 1 && filter.Modes.Contains(GameMode.Mania) && filter.ManiaKeyCount.HasValue)
            filtered = filtered.Where(b => b.KeyCount == filter.ManiaKeyCount.Value);

        return filtered.ToList();
    }

    /// <summary>
    /// Gets distinct beatmap set IDs matching the filter (for download counting).
    /// </summary>
    public async Task<HashSet<int>> GetFilteredBeatmapSetIdsAsync(SyncFilter filter)
    {
        var beatmaps = await GetFilteredBeatmapsAsync(filter);
        return beatmaps.Select(b => b.BeatmapSetId).ToHashSet();
    }

    private async Task LoadBeatmapDataAsync()
    {
        _allBeatmaps = new List<BeatmapEntry>();
        _beatmapSets = new Dictionary<int, BeatmapSetEntry>();

        await Task.Run(() =>
        {
            // Load beatmap sets first
            var setsDb = Path.Combine(_dataDir, "osu_beatmapsets.db");
            if (File.Exists(setsDb))
            {
                using var conn = new SqliteConnection($"Data Source={setsDb};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT beatmapset_id, artist, title, creator, genre_id, language_id, approved, approved_date, favourite_count, play_count FROM osu_beatmapsets";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var set = new BeatmapSetEntry
                    {
                        BeatmapSetId = reader.GetInt32(0),
                        Artist = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Creator = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        GenreId = (BeatmapGenre)(reader.IsDBNull(4) ? 0 : reader.GetInt32(4)),
                        LanguageId = (BeatmapLanguage)(reader.IsDBNull(5) ? 0 : reader.GetInt32(5)),
                        Approved = (BeatmapStatus)(reader.IsDBNull(6) ? 0 : reader.GetInt32(6)),
                        FavouriteCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        PlayCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                    };

                    if (!reader.IsDBNull(7))
                    {
                        set.ApprovedDate = DateTimeOffset.Parse(reader.GetString(7));
                        set.ReleaseYear = set.ApprovedDate.Value.Year;
                    }

                    _beatmapSets[set.BeatmapSetId] = set;
                }
                Console.WriteLine($"[BeatmapDataService] Loaded {_beatmapSets.Count} beatmap sets.");
            }

            // Load beatmaps
            var beatmapsDb = Path.Combine(_dataDir, "osu_beatmaps.db");
            if (File.Exists(beatmapsDb))
            {
                using var conn = new SqliteConnection($"Data Source={beatmapsDb};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT beatmap_id, beatmapset_id, mode, approved, genre_id, language_id,
                           version, total_length, hit_length, last_update, difficultyrating, bpm
                    FROM osu_beatmaps";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var entry = new BeatmapEntry
                    {
                        BeatmapId = reader.GetInt32(0),
                        BeatmapSetId = reader.GetInt32(1),
                        Mode = (GameMode)(reader.IsDBNull(2) ? 0 : reader.GetInt32(2)),
                        Approved = (BeatmapStatus)(reader.IsDBNull(3) ? 0 : reader.GetInt32(3)),
                        GenreId = (BeatmapGenre)(reader.IsDBNull(4) ? 0 : reader.GetInt32(4)),
                        LanguageId = (BeatmapLanguage)(reader.IsDBNull(5) ? 0 : reader.GetInt32(5)),
                        Version = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        TotalLength = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        HitLength = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        DifficultyRating = reader.IsDBNull(10) ? 0.0 : reader.GetDouble(10),
                        BPM = reader.IsDBNull(11) ? 0.0 : reader.GetDouble(11),
                    };

                    // Parse last_update
                    if (!reader.IsDBNull(9))
                    {
                        if (DateTimeOffset.TryParse(reader.GetString(9), out var dt))
                            entry.LastUpdate = dt;
                    }

                    // Carry over artist/title from set
                    if (_beatmapSets.TryGetValue(entry.BeatmapSetId, out var set))
                    {
                        entry.Artist = set.Artist;
                        entry.Title = set.Title;
                    }

                    // Extract mania key count from version string (e.g., "4K MX" -> 4)
                    if (entry.Mode == GameMode.Mania)
                        entry.KeyCount = ExtractManiaKeyCount(entry.Version);

                    _allBeatmaps.Add(entry);
                }
                Console.WriteLine($"[BeatmapDataService] Loaded {_allBeatmaps.Count} beatmaps.");
            }
        });
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private async Task DownloadFileAsync(string url, string destPath,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        Console.WriteLine($"[BeatmapDataService] Downloading {url} -> {destPath}");

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = File.Create(destPath);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (totalBytes > 0)
                progress?.Invoke((double)totalRead / totalBytes);
        }

        Console.WriteLine($"[BeatmapDataService] Download complete: {destPath}");
    }

    /// <summary>
    /// Extracts a .tar.bz2 archive.
    /// </summary>
    private static void ExtractBz2Tar(string archivePath, string destDir)
    {
        Console.WriteLine($"[BeatmapDataService] Extracting {archivePath}");

        using var fs = File.OpenRead(archivePath);
        using var bz2Stream = new BZip2InputStream(fs);
        using var tar = TarArchive.CreateInputTarArchive(bz2Stream, System.Text.Encoding.UTF8);

        tar.ExtractContents(destDir);
        Console.WriteLine($"[BeatmapDataService] Extraction complete: {archivePath}");
    }

    /// <summary>
    /// Imports a SQL dump file into a SQLite database.
    /// </summary>
    private void BuildSqliteDb(string sqlFile, string dbFile)
    {
        var sqlPath = Path.Combine(_dataDir, sqlFile);
        var dbPath = Path.Combine(_dataDir, dbFile);

        if (!File.Exists(sqlPath))
        {
            Console.WriteLine($"[BeatmapDataService] SQL file not found: {sqlPath}");
            return;
        }

        Console.WriteLine($"[BeatmapDataService] Building SQLite DB: {dbPath}");

        // Delete existing DB if present
        TryDelete(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var sql = File.ReadAllText(sqlPath);
        using var cmd = conn.CreateCommand();

        // Split by semicolons and execute each statement
        foreach (var statement in SplitSqlStatements(sql))
        {
            var trimmed = statement.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            try
            {
                cmd.CommandText = trimmed;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Some statements may fail (e.g., USE, SET, etc.) - skip them
                if (!trimmed.StartsWith("/*") && !trimmed.StartsWith("--"))
                    Console.WriteLine($"[BeatmapDataService] SQL statement skipped: {ex.Message}");
            }
        }

        Console.WriteLine($"[BeatmapDataService] SQLite DB built: {dbPath}");
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        // Simple split by semicolons, respecting string literals minimally
        var statements = new List<string>();
        int start = 0;
        bool inString = false;
        char stringChar = '\'';

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (inString)
            {
                if (c == '\\' && i + 1 < sql.Length) { i++; continue; }
                if (c == stringChar) inString = false;
            }
            else
            {
                if (c == '\'' || c == '"') { inString = true; stringChar = c; }
                else if (c == ';')
                {
                    statements.Add(sql[start..(i + 1)]);
                    start = i + 1;
                }
            }
        }

        if (start < sql.Length)
            statements.Add(sql[start..]);

        return statements;
    }

    /// <summary>
    /// Extracts mania key count from difficulty name (e.g., "4K Hyper" -> 4).
    /// </summary>
    private static int? ExtractManiaKeyCount(string version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        if (version.Length >= 2 && version[1] == 'K' && char.IsDigit(version[0]))
        {
            return int.Parse(version[0].ToString());
        }
        if (version.Length >= 3 && version[2] == 'K' && char.IsDigit(version[0]) && char.IsDigit(version[1]))
        {
            return int.Parse(version[..2]);
        }
        return null;
    }

    private static bool IsInYearRange(BeatmapSetEntry set, int from, int to)
    {
        if (!set.ApprovedDate.HasValue) return true; // Include if no date
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Console.WriteLine($"[BeatmapDataService] Could not delete {path}: {ex.Message}"); }
    }
}
