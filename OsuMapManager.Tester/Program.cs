using Microsoft.Data.Sqlite;
using OsuMapManager.Tester.Services;
using Realms;
using OsuMapManager.Tester;

while (true)
{
    Console.Clear();
    Console.WriteLine("============================================");
    Console.WriteLine("  OsuMapManager.Tester");
    Console.WriteLine("============================================");
    Console.WriteLine();
    Console.WriteLine("  1. Parse SQLite database (show tables & columns)");
    Console.WriteLine("  2. Download beatmap set by Online ID");
    Console.WriteLine("  3. Test: Query client.realm with filter (debug)");
    Console.WriteLine("  4. Test: Query SQLite DB with filter (debug)");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("Select option: ");

    var key = Console.ReadKey().KeyChar;
    Console.WriteLine();
    Console.WriteLine();

    switch (key)
    {
        case '1': await TestParseDatabaseAsync(); break;
        case '2': await TestDownloadBeatmapAsync(); break;
        case '3': await TestQueryRealmAsync(); break;
        case '4': await TestQueryDbAsync(); break;
        case '0': return 0;
        default: Console.WriteLine("Invalid option."); Console.ReadKey(); break;
    }
}

static async Task TestParseDatabaseAsync()
{
    Console.Write("Enter path to .db file: ");
    var dbPath = (Console.ReadLine()?.Trim() ?? "").Trim('"');
    if (!File.Exists(dbPath)) { Console.WriteLine("Not found."); Console.ReadKey(); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    await conn.OpenAsync();
    using var tc = conn.CreateCommand();
    tc.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
    using var tr = await tc.ExecuteReaderAsync();
    var tables = new List<string>();
    while (await tr.ReadAsync()) tables.Add(tr.GetString(0));
    Console.WriteLine($"Tables: {tables.Count}");
    foreach (var t in tables)
    {
        Console.WriteLine($"  {t}");
        using var cc = conn.CreateCommand();
        cc.CommandText = $"PRAGMA table_info('{t}')";
        using var cr = await cc.ExecuteReaderAsync();
        while (await cr.ReadAsync())
            Console.WriteLine($"    {cr.GetInt32(0)} {cr.GetString(1)} {cr.GetString(2)}");
        using var cnt = conn.CreateCommand();
        cnt.CommandText = $"SELECT COUNT(*) FROM [{t}]";
        Console.WriteLine($"  Rows: {await cnt.ExecuteScalarAsync()}");
    }
    Console.WriteLine("Done."); Console.ReadKey();
}

static async Task TestDownloadBeatmapAsync()
{
    Console.Write("Enter Beatmap Set Online ID: ");
    if (!int.TryParse(Console.ReadLine()?.Trim(), out var setId) || setId <= 0)
    { Console.WriteLine("Invalid ID."); Console.ReadKey(); return; }

    Console.Write("Source [1=official, 2=catboy]: ");
    var source = Console.ReadKey().KeyChar == '2' ? "catboy" : "official";
    Console.WriteLine();

    var dir = Path.Combine(AppContext.BaseDirectory, "downloads");
    var svc = new BeatmapDownloadService(dir) { DownloadSource = source };
    Console.WriteLine($"Downloading set {setId} from {source}...");
    var r = await svc.DownloadBeatmapSetAsync(setId);
    Console.WriteLine(r != null ? $"[OK] {r}" : "[FAIL]");
    Console.ReadKey();
}

static async Task TestQueryRealmAsync()
{
    Console.Write("Enter osu! lazer install path (contains client.realm): ");
    var osuPath = (Console.ReadLine()?.Trim() ?? "").Trim('"');
    var realmPath = Path.Combine(osuPath, "client.realm");
    if (!File.Exists(realmPath)) { Console.WriteLine("Not found: " + realmPath); Console.ReadKey(); return; }

    Console.WriteLine($"Realm: {realmPath} ({new FileInfo(realmPath).Length / 1024.0 / 1024.0:F1} MB)");
    Console.WriteLine("Filter: Mode=Mania(3), KeyCount=4, Date=2021~2022, Diff=4~5");

    var config = new RealmConfiguration(realmPath) { IsReadOnly = true, SchemaVersion = 51, ShouldDeleteIfMigrationNeeded = false };
    using var realm = Realm.GetInstance(config);
    var all = realm.DynamicApi.All("Beatmap").ToList();
    Console.WriteLine($"Total Beatmap rows: {all.Count}");

    int onlineOk = 0, onlineSkip = 0, modeOk = 0, modeSkip = 0, keyOk = 0, keySkip = 0;
    int dateOk = 0, dateSkip = 0, diffOk = 0, diffSkip = 0, statusOk = 0, statusSkip = 0, final = 0;
    var setIds = new HashSet<int>();

    foreach (var bm in all)
    {
        try
        {
            var oid = PInt(bm, "OnlineID");
            if (oid <= 0) { onlineSkip++; continue; }
            onlineOk++;

            var bs = PObj(bm, "BeatmapSet"); var sid = bs != null ? PInt(bs, "OnlineID") : 0;
            var rs = PObj(bm, "Ruleset"); var mode = rs != null ? PInt(rs, "OnlineID") : -1;
            var stars = PDbl(bm, "StarRating"); var status = PInt(bm, "Status");
            var date = bs != null ? PDate(bs, "DateSubmitted") : null;
            var kc = PKeyCount(bm);
            var md = PObj(bm, "Metadata");
            var title = md != null ? (PStr(md, "TitleUnicode") ?? PStr(md, "Title") ?? "") : "";
            var artist = md != null ? (PStr(md, "ArtistUnicode") ?? PStr(md, "Artist") ?? "") : "";
            var au = md != null ? PObj(md, "Author") : null;
            var creator = au != null ? (PStr(au, "Username") ?? "") : "";

            if (onlineOk <= 10)
                Console.WriteLine($"  [RAW{onlineOk}] set={sid} bm={oid} mode={mode} keys={kc} stars={stars:F2} status={status} date={date?.ToString("yyyy-MM-dd") ?? "null"} title={T(title,30)}");

            if (mode != 3) { modeSkip++; continue; } modeOk++;
            if (kc.HasValue && kc.Value != 4) { keySkip++; continue; } keyOk++;
            if (date.HasValue && (date.Value.Year < 2021 || date.Value.Year > 2022)) { dateSkip++; continue; } dateOk++;
            if (stars < 4.0 || stars > 5.0) { diffSkip++; continue; } diffOk++;
            // Status not reliable in client.realm (always 0), skip status filter for local data

            final++; setIds.Add(sid);
        }
        catch { }
    }

    Console.WriteLine();
    Console.WriteLine($"{"Total rows:",-22} {all.Count,6}");
    Console.WriteLine($"{"  OnlineID>0:",-22} {onlineOk,6}  (skip: {onlineSkip})");
    Console.WriteLine($"{"  After Mode(3):",-22} {modeOk,6}  (skip: {modeSkip})");
    Console.WriteLine($"{"  After KeyCount(4):",-22} {keyOk,6}  (skip: {keySkip})");
    Console.WriteLine($"{"  After Date(2021-22):",-22} {dateOk,6}  (skip: {dateSkip})");
    Console.WriteLine($"{"  After Diff(4-5):",-22} {diffOk,6}  (skip: {diffSkip})");
    Console.WriteLine($"");
    Console.WriteLine($"FINAL: {final} beatmaps in {setIds.Count} sets");
    Console.ReadKey();
}

static async Task TestQueryDbAsync()
{
    Console.Write("Enter path to catboy_ranked.db: ");
    var dbPath = (Console.ReadLine()?.Trim() ?? "").Trim('"');
    if (!File.Exists(dbPath)) { Console.WriteLine("Not found."); Console.ReadKey(); return; }

    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"); conn.Open();
    var sets = new Dictionary<int, (DateTimeOffset? D, int G, string T, string A, string C)>();
    using (var c = conn.CreateCommand())
    {
        c.CommandText = "SELECT id, genre_id, title, artist, creator, submitted_date FROM beatmap_sets";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            DateTimeOffset? d = null;
            if (!r.IsDBNull(5) && DateTimeOffset.TryParse(r.GetString(5), out var dt)) d = dt;
            sets[r.GetInt32(0)] = (d, r.IsDBNull(1)?0:r.GetInt32(1), r.IsDBNull(2)?"":r.GetString(2), r.IsDBNull(3)?"":r.GetString(3), r.IsDBNull(4)?"":r.GetString(4));
        }
    }
    Console.WriteLine($"Sets loaded: {sets.Count}");

    int total = 0, mS = 0, kS = 0, dS = 0, gS = 0, rS = 0, sS = 0, ok = 0;
    var ids = new HashSet<int>();
    using (var c = conn.CreateCommand())
    {
        c.CommandText = "SELECT id, beatmapset_id, mode_int, cs, ranked, difficulty_rating FROM beatmaps";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            total++;
            var sid = r.GetInt32(1); var mi = r.IsDBNull(2)?0:r.GetInt32(2);
            var cs = r.IsDBNull(3)?0.0:r.GetDouble(3); var rk = r.IsDBNull(4)?0:r.GetInt32(4);
            var dr = r.IsDBNull(5)?0.0:r.GetDouble(5); var kc = (int)Math.Round(cs);
            if (mi != 3) { mS++; continue; } if (kc != 4) { kS++; continue; }
            if (!sets.TryGetValue(sid, out var si)) continue;
            if (si.D.HasValue && (si.D.Value.Year < 2021 || si.D.Value.Year > 2022)) { dS++; continue; }
            if (si.G != 9) { gS++; continue; } if (dr < 4.0 || dr > 5.0) { rS++; continue; }
            if (rk != 1 && rk != 2) { sS++; continue; }
            ok++; ids.Add(sid);
        }
    }
    Console.WriteLine($"Total: {total} | Mode:-{mS} Key:-{kS} Date:off Genre:off Diff:off Status:-{sS} | FINAL: {ok} bms, {ids.Count} sets");
    Console.ReadKey();
}

static int PInt(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v == null ? 0 : (int)v; } catch { return 0; } }
static dynamic? PObj(dynamic o, string n) { try { return o.GetType().GetProperty(n)?.GetValue(o); } catch { return null; } }
static string? PStr(dynamic o, string n) { try { return o.GetType().GetProperty(n)?.GetValue(o) as string; } catch { return null; } }
static double PDbl(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; } }
static DateTimeOffset? PDate(dynamic o, string n) { try { var v = o.GetType().GetProperty(n)?.GetValue(o); return v is DateTimeOffset d ? d : null; } catch { return null; } }
static int? PKeyCount(dynamic bm) { try { var d = PObj(bm, "Difficulty"); if (d != null) return (int)Math.Round(PDbl(d, "CircleSize")); } catch { } return null; }
static string T(string s, int m) => s.Length <= m ? s : s[..(m - 3)] + "...";