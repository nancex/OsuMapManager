using System.Net.Http;

namespace OsuMapManager.Tester.Services;

/// <summary>
/// Downloads individual beatmap sets by Online ID.
/// Decoupled from Avalonia/Realm/Settings for console testing.
/// </summary>
public class BeatmapDownloadService
{
    private readonly HttpClient _http;
    private readonly string _downloadDir;

    // "official" = osu.ppy.sh, "catboy" = catboy.best mirror
    public string DownloadSource { get; set; } = "official";

    public BeatmapDownloadService(string downloadDir)
    {
        _downloadDir = downloadDir;
        Directory.CreateDirectory(_downloadDir);

        var handler = new SocketsHttpHandler
        {
            // Force HTTP/1.1  HTTP/2 ALPN can trigger SEC_E_ILLEGAL_MESSAGE on some servers
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 4,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13 |
                                       System.Security.Authentication.SslProtocols.Tls12,
                // Bypass cert validation for catboy.best (Cloudflare edge certs)
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestVersion = System.Net.HttpVersion.Version11;
        _http.DefaultRequestHeaders.Add("User-Agent", "OsuMapManager.Tester/1.0");
    }

    /// <summary>
    /// Downloads a beatmap set by its Online ID.
    /// Returns the path to the downloaded .osz file, or null on failure.
    /// </summary>
    public async Task<string?> DownloadBeatmapSetAsync(int setId, CancellationToken ct = default)
    {
        try
        {
            var destPath = Path.Combine(_downloadDir, $"{setId}.osz");

            // Skip if already downloaded
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                Console.WriteLine($"[DownloadService] Set {setId}: already exists, skipping.");
                return destPath;
            }

            var url = DownloadSource == "catboy"
                ? $"https://catboy.best/d/{setId}n"
                : $"https://osu.ppy.sh/beatmapsets/{setId}/download";

            Console.WriteLine($"[DownloadService] Downloading set {setId} from {url}...");

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[DownloadService] Set {setId}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            await using var fs = File.Create(destPath);
            await response.Content.CopyToAsync(fs, ct);

            var size = new FileInfo(destPath).Length;
            Console.WriteLine($"[DownloadService] Set {setId}: downloaded ({size} bytes)");

            return size > 0 ? destPath : null;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[DownloadService] Set {setId}: cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DownloadService] Set {setId}: error - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads multiple beatmap sets in parallel.
    /// </summary>
    public async Task<(int Downloaded, int Failed)> DownloadMultipleAsync(
        IEnumerable<int> setIds,
        int threadCount = 4,
        CancellationToken ct = default)
    {
        var setList = setIds.ToList();
        int total = setList.Count;
        int downloaded = 0;
        int failed = 0;
        int current = 0;

        Console.WriteLine($"[DownloadService] Starting batch download: {total} sets, {threadCount} threads.");

        var semaphore = new SemaphoreSlim(threadCount);

        var tasks = setList.Select(async setId =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await DownloadBeatmapSetAsync(setId, ct);
                var cur = Interlocked.Increment(ref current);
                if (result != null)
                    Interlocked.Increment(ref downloaded);
                else
                    Interlocked.Increment(ref failed);

                Console.WriteLine($"[DownloadService] [{cur}/{total}] Set {setId}: {(result != null ? "OK" : "FAIL")}");
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"[DownloadService] Batch done. OK: {downloaded}, Failed: {failed}");
        return (downloaded, failed);
    }
}
