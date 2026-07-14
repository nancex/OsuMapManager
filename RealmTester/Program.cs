using System.Reflection;
using Realms;

string? osuPath = args.Length > 0 ? args[0] : null;
if (string.IsNullOrEmpty(osuPath))
{
    var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!"), @"D:\osu!", @"E:\osu!", @"C:\osu!" };
    osuPath = candidates.FirstOrDefault(Directory.Exists);
    if (osuPath == null) { Console.WriteLine("Usage: RealmTester <osu-path>"); return 1; }
}

var realmPath = Path.Combine(osuPath, "client.realm");
if (!File.Exists(realmPath)) { Console.WriteLine($"ERROR: not found"); return 1; }
Console.WriteLine($"osu! : {osuPath}");

var config = new RealmConfiguration(realmPath)
{
    IsReadOnly = true, SchemaVersion = 51,
    ShouldDeleteIfMigrationNeeded = false
};

try
{
    using var realm = Realm.GetInstance(config);
    Console.WriteLine($"[OK] Opened. Schema classes: {realm.Schema.Count}");
    foreach (var s in realm.Schema)
        Console.WriteLine($"  [{s.Name}] ({s.Count} props)");

    // Collections
    Console.WriteLine("--- Collections ---");
    var cols = realm.All<BeatmapCollection>().ToList();
    foreach (var c in cols)
        Console.WriteLine($"  \"{c.Name}\" {c.BeatmapMD5Hashes?.Count ?? 0} hashes");

    // Beatmap via DynamicApi -- class name is "Beatmap" (from [MapTo])
    Console.WriteLine("--- Beatmap ---");
    var allBms = realm.DynamicApi.All("Beatmap").ToList();
    Console.WriteLine($"  Count: {allBms.Count}");

    int ok = 0;
    foreach (var bm in allBms.Take(200))
    {
        try { if (Prop<int>(bm, "OnlineID") > 0) ok++; } catch { }
    }
    Console.WriteLine($"  First 200: {ok} OnlineID>0");

    int shown = 0;
    foreach (var bm in allBms)
    {
        var oid = Prop<int>(bm, "OnlineID");
        if (oid <= 0 || shown >= 5) continue;
        var md5 = Prop<string>(bm, "MD5Hash") ?? "?";
        var diff = Prop<string>(bm, "DifficultyName") ?? "?";
        var status = Prop<int>(bm, "Status");
        Console.WriteLine($"  [{oid}] {md5[..Math.Min(8,md5.Length)]}... \"{diff}\" status={status}");
        shown++;
    }

    // MD5 -> OnlineID map
    Console.WriteLine("--- MD5 -> OnlineID ---");
    var md5Map = new Dictionary<string, int>();
    foreach (var bm in allBms)
    {
        try
        {
            var oid = Prop<int>(bm, "OnlineID");
            if (oid <= 0) continue;
            var md5 = Prop<string>(bm, "MD5Hash");
            if (!string.IsNullOrEmpty(md5)) md5Map.TryAdd(md5, oid);
        }
        catch { }
    }
    Console.WriteLine($"  Entries: {md5Map.Count}");

    foreach (var c in cols)
    {
        var total = c.BeatmapMD5Hashes?.Count ?? 0;
        var matched = 0;
        if (c.BeatmapMD5Hashes != null)
            foreach (var md5 in c.BeatmapMD5Hashes)
                if (!string.IsNullOrEmpty(md5) && md5Map.ContainsKey(md5)) matched++;
        Console.WriteLine($"  \"{c.Name}\": {matched}/{total} ({(total>0?(double)matched/total*100:0):F0}%)");
    }

    // Relationship test
    Console.WriteLine("--- Relationships ---");
    int relN = 0;
    foreach (var bm in allBms)
    {
        if (relN >= 3) break;
        try
        {
            var oid = Prop<int>(bm, "OnlineID");
            if (oid <= 0) continue;
            var meta = Prop<dynamic>(bm, "Metadata");
            var artist = meta != null ? (Prop<string>(meta, "ArtistUnicode") ?? Prop<string>(meta, "Artist") ?? "") : "";
            var title = meta != null ? (Prop<string>(meta, "TitleUnicode") ?? Prop<string>(meta, "Title") ?? "") : "";
            var author = meta != null ? Prop<dynamic>(meta, "Author") : null;
            var creator = author != null ? (Prop<string>(author, "Username") ?? "") : "";
            Console.WriteLine($"  [{oid}] \"{artist} - {title}\" by {creator}");
            relN++;
        }
        catch (Exception ex) { Console.WriteLine($"  err: {ex.Message}"); }
    }

    Console.WriteLine("[SUCCESS]");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null) Console.WriteLine($"  Inner: {ex.InnerException.Message}");
}
return 0;

static T Prop<T>(dynamic obj, string name)
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

// ================================================================
// Realm schema matching osu! lazer source exactly
// [MapTo] attributes match Realm class names in the file
// ================================================================

public class BeatmapCollection : RealmObject
{
    [PrimaryKey] public Guid ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public IList<string> BeatmapMD5Hashes { get; } = null!;
    public DateTimeOffset LastModified { get; set; }
}

[MapTo("Beatmap")]
public class BeatmapInfo : RealmObject
{
    [PrimaryKey] public Guid ID { get; set; }
    public string DifficultyName { get; set; } = string.Empty;
    public RulesetInfo Ruleset { get; set; } = null!;
    public BeatmapDifficulty Difficulty { get; set; } = null!;
    public BeatmapMetadata Metadata { get; set; } = null!;
    public BeatmapUserSettings UserSettings { get; set; } = null!;
    public BeatmapSetInfo? BeatmapSet { get; set; }

    [MapTo("Status")] public int StatusInt { get; set; }

    [Indexed] public int OnlineID { get; set; } = -1;
    public double Length { get; set; }
    public double BPM { get; set; }
    public string Hash { get; set; } = string.Empty;
    public double StarRating { get; set; } = -1;

    [Indexed] public string MD5Hash { get; set; } = string.Empty;
    public string OnlineMD5Hash { get; set; } = string.Empty;
    public DateTimeOffset? LastLocalUpdate { get; set; }
    public DateTimeOffset? LastOnlineUpdate { get; set; }
    public bool Hidden { get; set; }
    public int EndTimeObjectCount { get; set; } = -1;
    public int TotalObjectCount { get; set; } = -1;
    public DateTimeOffset? LastPlayed { get; set; }
    public int BeatDivisor { get; set; } = 4;
    public double? EditorTimestamp { get; set; }
}

[MapTo("BeatmapSet")]
public class BeatmapSetInfo : RealmObject
{
    [PrimaryKey] public Guid ID { get; set; }
    [Indexed] public int OnlineID { get; set; } = -1;
    public DateTimeOffset DateAdded { get; set; }
    public DateTimeOffset? DateSubmitted { get; set; }
    public DateTimeOffset? DateRanked { get; set; }

    [MapTo("Status")] public int StatusInt { get; set; }
    public bool DeletePending { get; set; }
    public string Hash { get; set; } = string.Empty;
    public bool Protected { get; set; }

    public IList<BeatmapInfo> Beatmaps { get; } = null!;
}

public class BeatmapMetadata : RealmObject
{
    public string Title { get; set; } = string.Empty;
    public string TitleUnicode { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string ArtistUnicode { get; set; } = string.Empty;
    public RealmUser Author { get; set; } = null!;
    public string Source { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public int PreviewTime { get; set; } = -1;
    public string AudioFile { get; set; } = string.Empty;
    public string BackgroundFile { get; set; } = string.Empty;
}

public class RealmUser : EmbeddedObject
{
    public int OnlineID { get; set; } = 1;
    public string Username { get; set; } = string.Empty;
    [MapTo("CountryCode")] public string CountryString { get; set; } = string.Empty;
}

[MapTo("Ruleset")]
public class RulesetInfo : RealmObject
{
    [PrimaryKey] public string ShortName { get; set; } = string.Empty;
    [Indexed] public int OnlineID { get; set; } = -1;
    public string Name { get; set; } = string.Empty;
    public string InstantiationInfo { get; set; } = string.Empty;
    public int LastAppliedDifficultyVersion { get; set; }
    public bool Available { get; set; }
}

[MapTo("BeatmapDifficulty")]
public class BeatmapDifficulty : EmbeddedObject
{
    public float DrainRate { get; set; }
    public float CircleSize { get; set; }
    public float OverallDifficulty { get; set; }
    public float ApproachRate { get; set; }
    public double SliderMultiplier { get; set; }
    public double SliderTickRate { get; set; }
}

public class BeatmapUserSettings : EmbeddedObject
{
    public double Offset { get; set; }
}
