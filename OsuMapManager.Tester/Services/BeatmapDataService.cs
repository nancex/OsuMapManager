using System.Data;
using System.Net.Http;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Data.Sqlite;
using OsuMapManager.Tester.Models;

namespace OsuMapManager.Tester.Services;

/// <summary>
/// Manages beatmap metadata by downloading and parsing osu! data dumps.
/// Decoupled from Avalonia/Realm for console testing.
/// </summary>
public class BeatmapDataService
{
    private readonly HttpClient _http = new();
    private readonly string _dataDir;
    private bool _dataReady;

    public bool IsDataReady => _dataReady;

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

    private void CheckDataReady()
    {
        var beatmapDb = Path.Combine(_dataDir, "osu_beatmaps.db");
        var beatmapSetDb = Path.Combine(_dataDir, "osu_beatmapsets.db");
        _dataReady = File.Exists(beatmapDb) && File.Exists(beatmapSetDb);
        Console.WriteLine($"[BeatmapDataService] Data ready: {_dataReady}");
    }

    public async Task<bool> FetchBeatmapDataAsync(
        Action<string, double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Invoke("Downloading beatmap data...", 0.05);

            var beatmapsArchive = Path.Combine(_dataDir, "osu_beatmaps.tar.bz2");
            await DownloadFileAsync(BEATMAPS_URL, beatmapsArchive,
                p => progress?.Invoke("Downloading beatmaps...", 0.05 + p * 0.40), ct);

            progress?.Invoke("Downloading beatmap sets...", 0.45);
            var beatmapSetsArchive = Path.Combine(_dataDir, "osu_beatmapsets.tar.bz2");
            await DownloadFileAsync(BEATMAPSETS_URL, beatmapSetsArchive,
                p => progress?.Invoke("Downloading beatmap sets...", 0.45 + p * 0.40), ct);

            progress?.Invoke("Extracting beatmaps...", 0.85);
            await Task.Run(() => ExtractBz2Tar(beatmapsArchive, _dataDir), ct);

            progress?.Invoke("Extracting beatmap sets...", 0.90);
            await Task.Run(() => ExtractBz2Tar(beatmapSetsArchive, _dataDir), ct);

            progress?.Invoke("Building database...", 0.93);
            await Task.Run(() => BuildSqliteDb("osu_beatmaps.sql", "osu_beatmaps.db"), ct);
            await Task.Run(() => BuildSqliteDb("osu_beatmapsets.sql", "osu_beatmapsets.db"), ct);

            TryDelete(beatmapsArchive);
            TryDelete(beatmapSetsArchive);
            TryDelete(Path.Combine(_dataDir, "osu_beatmaps.sql"));
            TryDelete(Path.Combine(_dataDir, "osu_beatmapsets.sql"));

            _dataReady = true;
            _allBeatmaps = null;
            _beatmapSets = null;

            progress?.Invoke("Done!", 1.0);
            Console.WriteLine("[BeatmapDataService] Beatmap data fetched and processed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BeatmapDataService] Failed to fetch data: {ex.Message}");
            return false;
        }
    }

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

        // Filter by date range (year-month-day), falls back to year range
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

                    if (!reader.IsDBNull(9))
                    {
                        if (DateTimeOffset.TryParse(reader.GetString(9), out var dt))
                            entry.LastUpdate = dt;
                    }

                    if (_beatmapSets.TryGetValue(entry.BeatmapSetId, out var set))
                    {
                        entry.Artist = set.Artist;
                        entry.Title = set.Title;
                    }

                    if (entry.Mode == GameMode.Mania)
                        entry.KeyCount = ExtractManiaKeyCount(entry.Version);

                    _allBeatmaps.Add(entry);
                }
                Console.WriteLine($"[BeatmapDataService] Loaded {_allBeatmaps.Count} beatmaps.");
            }
        });
    }

    private async Task DownloadFileAsync(string url, string destPath,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        Console.WriteLine($"[BeatmapDataService] Downloading {url} -> {Path.GetFileName(destPath)}");

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

        Console.WriteLine($"[BeatmapDataService] Download complete: {Path.GetFileName(destPath)}");
    }

    private static void ExtractBz2Tar(string archivePath, string destDir)
    {
        Console.WriteLine($"[BeatmapDataService] Extracting {Path.GetFileName(archivePath)}...");

        using var fs = File.OpenRead(archivePath);
        using var bz2Stream = new BZip2InputStream(fs);
        using var tar = TarArchive.CreateInputTarArchive(bz2Stream, System.Text.Encoding.UTF8);

        tar.ExtractContents(destDir);
        Console.WriteLine("[BeatmapDataService] Extraction complete.");
    }

    private void BuildSqliteDb(string sqlFile, string dbFile)
    {
        var sqlPath = Path.Combine(_dataDir, sqlFile);
        var dbPath = Path.Combine(_dataDir, dbFile);

        if (!File.Exists(sqlPath))
        {
            Console.WriteLine($"[BeatmapDataService] SQL file not found: {sqlPath}");
            return;
        }

        Console.WriteLine($"[BeatmapDataService] Building SQLite DB: {dbFile}");

        TryDelete(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var sql = File.ReadAllText(sqlPath);
        using var cmd = conn.CreateCommand();

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
                if (!trimmed.StartsWith("/*") && !trimmed.StartsWith("--"))
                    Console.WriteLine($"[BeatmapDataService] SQL statement skipped: {ex.Message}");
            }
        }

        Console.WriteLine($"[BeatmapDataService] SQLite DB built: {dbFile}");
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
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

    private static int? ExtractManiaKeyCount(string version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        if (version.Length >= 2 && version[1] == 'K' && char.IsDigit(version[0]))
            return int.Parse(version[0].ToString());
        if (version.Length >= 3 && version[2] == 'K' && char.IsDigit(version[0]) && char.IsDigit(version[1]))
            return int.Parse(version[..2]);
        return null;
    }

    /// <summary>
    /// Checks if a beatmap set's approved date falls within the given date range (year-month-day).
    /// </summary>
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Console.WriteLine($"[BeatmapDataService] Could not delete {path}: {ex.Message}"); }
    }
}
