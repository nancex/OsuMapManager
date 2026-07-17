using Microsoft.Data.Sqlite;
using OsuMapManager.Tester.Services;

// ================================================================
// OsuMapManager.Tester  Interactive Test Menu
// ================================================================

while (true)
{
    Console.Clear();
    Console.WriteLine("============================================");
    Console.WriteLine("  OsuMapManager.Tester");
    Console.WriteLine("============================================");
    Console.WriteLine();
    Console.WriteLine("  1. Parse SQLite database (show tables & columns)");
    Console.WriteLine("  2. Download beatmap set by Online ID");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("Select option: ");

    var key = Console.ReadKey().KeyChar;
    Console.WriteLine();
    Console.WriteLine();

    switch (key)
    {
        case '1':
            await TestParseDatabaseAsync();
            break;
        case '2':
            await TestDownloadBeatmapAsync();
            break;
        case '0':
            return 0;
        default:
            Console.WriteLine("Invalid option. Press any key...");
            Console.ReadKey();
            break;
    }
}

// ================================================================
// Test 1: Parse SQLite database  show tables and columns
// ================================================================

static async Task TestParseDatabaseAsync()
{
    Console.Write("Enter path to .db file: ");
    var dbPath = Console.ReadLine()?.Trim() ?? "";
    // Remove quotes if user pasted a quoted path
    dbPath = dbPath.Trim('"');

    if (!File.Exists(dbPath))
    {
        Console.WriteLine();
        Console.WriteLine($"[FAIL] File not found: {dbPath}");
        Console.WriteLine();
        Console.Write("Press any key to return...");
        Console.ReadKey();
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"Opening: {dbPath}");
    Console.WriteLine($"Size:    {new FileInfo(dbPath).Length / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine();

    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();

        // Get list of tables
        using var tableCmd = conn.CreateCommand();
        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var tableReader = await tableCmd.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await tableReader.ReadAsync())
            tables.Add(tableReader.GetString(0));

        Console.WriteLine($"Tables found: {tables.Count}");
        Console.WriteLine();

        foreach (var table in tables)
        {
            Console.WriteLine($"  {table}");
            Console.WriteLine($"  {new string('-', table.Length)}");

            // Get columns via PRAGMA
            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info('{table}')";
            using var colReader = await colCmd.ExecuteReaderAsync();

            Console.WriteLine("  {0,-5} {1,-22} {2,-12} {3,-6} {4}",
                "cid", "name", "type", "notnull", "pk");
            Console.WriteLine("  " + new string('-', 80));

            while (await colReader.ReadAsync())
            {
                // PRAGMA table_info: cid(0) name(1) type(2,nullable) notnull(3) dflt_value(4,nullable) pk(5)
                var cid = colReader.GetInt32(0);
                var name = colReader.GetString(1);
                var type = colReader.IsDBNull(2) ? "(none)" : colReader.GetString(2);
                var notNull = colReader.GetInt32(3);
                var pk = colReader.GetInt32(5);

                Console.WriteLine("  {0,-5} {1,-22} {2,-12} {3,-6} {4}",
                    cid, name, type, notNull, pk);
            }

            // Also show row count
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            var rowCount = (long)(await countCmd.ExecuteScalarAsync())!;
            Console.WriteLine();
            Console.WriteLine($"  Total rows: {rowCount:N0}");
            Console.WriteLine();
        }

        Console.WriteLine("[PASS] Database parsed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
    Console.Write("Press any key to return...");
    Console.ReadKey();
}

// ================================================================
// Test 2: Download beatmap by Set ID
// ================================================================

static async Task TestDownloadBeatmapAsync()
{
    Console.Write("Enter Beatmap Set Online ID: ");
    var input = Console.ReadLine()?.Trim() ?? "";
    if (!int.TryParse(input, out var setId) || setId <= 0)
    {
        Console.WriteLine("[FAIL] Invalid ID.");
        Console.WriteLine();
        Console.Write("Press any key to return...");
        Console.ReadKey();
        return;
    }

    Console.WriteLine();
    Console.WriteLine("Download source:");
    Console.WriteLine("  1. Official (osu.ppy.sh)");
    Console.WriteLine("  2. Mirror (catboy.best)");
    Console.Write("Select: ");
    var sourceKey = Console.ReadKey().KeyChar;
    Console.WriteLine();

    var source = sourceKey == '2' ? "catboy" : "official";
    var downloadDir = Path.Combine(AppContext.BaseDirectory, "downloads");

    var downloadService = new BeatmapDownloadService(downloadDir)
    {
        DownloadSource = source
    };

    Console.WriteLine();
    Console.WriteLine($"Source:   {source}");
    Console.WriteLine($"Save to:  {downloadDir}");
    Console.WriteLine($"Set ID:   {setId}");
    Console.WriteLine();
    Console.WriteLine("Downloading...");

    var result = await downloadService.DownloadBeatmapSetAsync(setId);

    if (result != null)
    {
        var fi = new FileInfo(result);
        Console.WriteLine();
        Console.WriteLine($"[PASS] Downloaded: {result}");
        Console.WriteLine($"       Size: {fi.Length / 1024.0:F1} KB");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("[FAIL] Download failed.");
        if (source == "official")
            Console.WriteLine("       Official source may require osu! login. Try the catboy mirror.");
        else
            Console.WriteLine("       Check the Set ID or try the official source.");
    }

    Console.WriteLine();
    Console.Write("Press any key to return...");
    Console.ReadKey();
}
