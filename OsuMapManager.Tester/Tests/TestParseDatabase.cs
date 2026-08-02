using Microsoft.Data.Sqlite;

namespace OsuMapManager.Tester.Tests;

public static class TestParseDatabase
{
    public static async Task RunAsync()
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
}
