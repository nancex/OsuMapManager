using OsuMapManager.Tester.Helpers;
using OsuMapManager.Tester;
using Realms;

namespace OsuMapManager.Tester.Tests;

public static class TestQueryRealm
{
    public static async Task RunAsync()
    {
        Console.Write("Enter osu! lazer install path (contains client.realm): ");
        var osuPath = TesterConfig.GetOsuPath();
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
                var oid = RealmReflection.PInt(bm, "OnlineID");
                if (oid <= 0) { onlineSkip++; continue; }
                onlineOk++;

                var bs = RealmReflection.PObj(bm, "BeatmapSet"); var sid = bs != null ? RealmReflection.PInt(bs, "OnlineID") : 0;
                var rs = RealmReflection.PObj(bm, "Ruleset"); var mode = rs != null ? RealmReflection.PInt(rs, "OnlineID") : -1;
                var stars = RealmReflection.PDbl(bm, "StarRating"); var status = RealmReflection.PInt(bm, "Status");
                var date = bs != null ? RealmReflection.PDate(bs, "DateSubmitted") : null;
                var kc = RealmReflection.PKeyCount(bm);
                var md = RealmReflection.PObj(bm, "Metadata");
                var title = md != null ? (RealmReflection.PStr(md, "TitleUnicode") ?? RealmReflection.PStr(md, "Title") ?? "") : "";
                var artist = md != null ? (RealmReflection.PStr(md, "ArtistUnicode") ?? RealmReflection.PStr(md, "Artist") ?? "") : "";
                var au = md != null ? RealmReflection.PObj(md, "Author") : null;
                var creator = au != null ? (RealmReflection.PStr(au, "Username") ?? "") : "";

                if (onlineOk <= 10)
                    Console.WriteLine($"  [RAW{onlineOk}] set={sid} bm={oid} mode={mode} keys={kc} stars={stars:F2} status={status} date={date?.ToString("yyyy-MM-dd") ?? "null"} title={RealmReflection.T(title,30)}");

                if (mode != 3) { modeSkip++; continue; } modeOk++;
                if (kc.HasValue && kc.Value != 4) { keySkip++; continue; } keyOk++;
                if (date.HasValue && (date.Value.Year < 2021 || date.Value.Year > 2022)) { dateSkip++; continue; } dateOk++;
                if (stars < 4.0 || stars > 5.0) { diffSkip++; continue; } diffOk++;

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
}
