using OsuMapManager.Tester.Services;

namespace OsuMapManager.Tester.Tests;

public static class TestDownloadBeatmap
{
    public static async Task RunAsync()
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
}
