using Microsoft.Data.Sqlite;

namespace OsuMapManager.Tester.Tests;

public static class TestQueryDb
{
    public static async Task RunAsync()
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
}
