using Realms;
using OsuMapManager.Tester;
using OsuMapManager.Models.RealmSchema;

namespace OsuMapManager.Tester.Tests;

public static class TestCreateCollection
{
    public static Task RunAsync()
    {
        Console.Write("Enter osu! lazer install path (contains client.realm): ");
        var osuPath = TesterConfig.GetOsuPath();
        var realmPath = Path.Combine(osuPath, "client.realm");
        if (!File.Exists(realmPath)) { Console.WriteLine("client.realm not found."); Console.ReadKey(); return Task.CompletedTask; }

        Console.Write("Enter collection name: ");
        var name = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { Console.WriteLine("Name required."); Console.ReadKey(); return Task.CompletedTask; }

        Console.WriteLine("Enter beatmap MD5 hashes (one per line, empty line to finish):");
        var hashes = new List<string>();
        while (true)
        {
            Console.Write("  > ");
            var h = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(h)) break;
            hashes.Add(h);
        }

        if (hashes.Count == 0)
        {
            Console.WriteLine("No hashes provided.");
            Console.ReadKey(); return Task.CompletedTask;
        }

        var config = new RealmConfiguration(realmPath)
        {
            SchemaVersion = 51,
            ShouldDeleteIfMigrationNeeded = false
        };

        using var realm = Realm.GetInstance(config);

        realm.Write(() =>
        {
            var existing = realm.All<BeatmapCollection>().FirstOrDefault(c => c.Name == name);
            if (existing != null)
            {
                Console.WriteLine($"\nCollection '{name}' already exists. Adding {hashes.Count} hashes...");
                int added = 0;
                foreach (var h in hashes)
                {
                    if (!existing.BeatmapMD5Hashes.Contains(h))
                    {
                        existing.BeatmapMD5Hashes.Add(h);
                        added++;
                    }
                }
                existing.LastModified = DateTimeOffset.UtcNow;
                Console.WriteLine($"  Updated: {added} new hashes added (total: {existing.BeatmapMD5Hashes.Count})");
            }
            else
            {
                var col = new BeatmapCollection
                {
                    ID = Guid.NewGuid(),
                    Name = name,
                    LastModified = DateTimeOffset.UtcNow
                };
                foreach (var h in hashes)
                    col.BeatmapMD5Hashes.Add(h);

                realm.Add(col);
                Console.WriteLine($"\nCreated collection '{name}' with {hashes.Count} beatmaps.");
            }
        });

        Console.ReadKey();
        return Task.CompletedTask;
    }
}
