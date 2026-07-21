using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OsuMapManager.Models;

namespace OsuMapManager.Services;

public class SyncService
{
    private readonly OsuDataService _osuData;
    private readonly BeatmapDataService _beatmapData;
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    /// <summary>
    /// Pause gate for rate-limiting. Signaled = not paused; non-signaled = paused.
    /// </summary>
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);

    public SyncService(OsuDataService osuData, BeatmapDataService beatmapData, SettingsService settings)
    {
        _osuData = osuData;
        _beatmapData = beatmapData;
        _settings = settings;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13 |
                                       System.Security.Authentication.SslProtocols.Tls12,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestVersion = System.Net.HttpVersion.Version11;
        _http.DefaultRequestHeaders.Add("User-Agent", "OsuMapManager/1.0");
    }

    public async Task<(HashSet<int> MissingSetIds, int ExtraCount)> AnalyzeAsync(
        IEnumerable<SyncFilter> filters, IProgress<string>? progress = null)
    {
        progress?.Report("Loading local beatmap data...");
        var localBeatmaps = await _osuData.GetLocalBeatmapInfoAsync();
        var localSetIds = localBeatmaps.Select(b => b.BeatmapSetId).ToHashSet();

        progress?.Report("Loading beatmap metadata...");
        var targetSetIds = await _beatmapData.GetUnionBeatmapSetIdsAsync(filters);

        progress?.Report("Comparing...");
        var missingSetIds = targetSetIds.Except(localSetIds).ToHashSet();
        var extraBeatmaps = localBeatmaps.Where(b => !targetSetIds.Contains(b.BeatmapSetId)).ToList();

        Console.WriteLine($"[SyncService] Local sets: {localSetIds.Count}, Target (union): {targetSetIds.Count}, Missing: {missingSetIds.Count}, Extra: {extraBeatmaps.Count}");
        return (missingSetIds, extraBeatmaps.Count);
    }

    public async Task<(int Downloaded, int Failed)> DownloadMissingAsync(
        HashSet<int> missingSetIds, string osuPath,
        IProgress<(int Current, int Total, int Downloaded, int Failed)>? progress = null,
        CancellationToken ct = default,
        Action<string>? onStatus = null)
    {
        int total = missingSetIds.Count;
        int current = 0;
        int downloaded = 0;
        int failed = 0;

        var downloadDir = osuPath;
        Directory.CreateDirectory(downloadDir);

        var threadCount = _settings.Settings.DownloadThreads;
        Console.WriteLine($"[SyncService] Starting download of {total} sets, {threadCount} threads.");

        var setList = missingSetIds.ToList();
        var semaphore = new SemaphoreSlim(threadCount);

        using var rateLimitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = rateLimitCts.Token;

        var tasks = setList.Select(async setId =>
        {
            try
            {
                await semaphore.WaitAsync(linkedCt);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                // Wait if rate-limit pause is active
                await _rateLimitGate.WaitAsync(linkedCt).ConfigureAwait(false);
                _rateLimitGate.Release();

                var (success, isRateLimited, retryAfterDate, retryAfterSeconds) =
                    await DownloadBeatmapSetWithRateLimitAsync(setId, downloadDir, linkedCt);

                if (isRateLimited)
                {
                    // First thread to detect 429 initiates the pause
                    if (Interlocked.CompareExchange(ref _rateLimitHandlerFlag, 1, 0) == 0)
                    {
                        await HandleRateLimitPauseAsync(retryAfterDate, retryAfterSeconds,
                            linkedCt, onStatus);
                        Interlocked.Exchange(ref _rateLimitHandlerFlag, 0);
                    }
                    else
                    {
                        // Another thread is handling the pause — just wait for it
                        await _rateLimitGate.WaitAsync(linkedCt).ConfigureAwait(false);
                        _rateLimitGate.Release();
                    }
                }

                var cur = Interlocked.Increment(ref current);
                if (success) Interlocked.Increment(ref downloaded);
                else Interlocked.Increment(ref failed);
                progress?.Report((cur, total, downloaded, failed));
                Console.WriteLine($"[SyncService] [{cur}/{total}] Set {setId}: {(success ? "OK" : "FAIL")}");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref current);
                Interlocked.Increment(ref failed);
                progress?.Report((current, total, downloaded, failed));
            }
            finally
            {
                try { semaphore.Release(); } catch { }
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine($"[SyncService] Done. OK: {downloaded}, Failed: {failed}");
        return (downloaded, failed);
    }

    /// <summary>
    /// Called by the first thread that hits a 429. Pauses all downloads,
    /// waits for the retry-after delay, then resumes.
    /// </summary>
    private async Task HandleRateLimitPauseAsync(
        DateTimeOffset? retryAfterDate, int? retryAfterSeconds,
        CancellationToken ct, Action<string>? onStatus)
    {
        // Acquire the gate permit to pause all other threads
        await _rateLimitGate.WaitAsync(ct).ConfigureAwait(false);
        onStatus?.Invoke("Reach ratelimit, pausing...");
        Console.WriteLine($"[SyncService] Rate limit hit. Pausing all downloads.");

        // Calculate wait duration
        TimeSpan delay;
        if (retryAfterDate.HasValue)
        {
            delay = retryAfterDate.Value - DateTimeOffset.UtcNow;
            Console.WriteLine($"[SyncService] Retry-After (date): {retryAfterDate:O}, delay={delay.TotalSeconds:F1}s");
        }
        else if (retryAfterSeconds.HasValue)
        {
            delay = TimeSpan.FromSeconds(retryAfterSeconds.Value);
            Console.WriteLine($"[SyncService] Retry-After (seconds): {retryAfterSeconds}, delay={delay.TotalSeconds:F1}s");
        }
        else
        {
            // No retry info — default to 60 seconds
            delay = TimeSpan.FromSeconds(60);
            Console.WriteLine($"[SyncService] No Retry-After info, using default delay of 60s.");
        }

        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromMinutes(5)) delay = TimeSpan.FromMinutes(5); // safety cap

        Console.WriteLine($"[SyncService] Waiting {delay.TotalSeconds:F1}s before resuming...");

        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[SyncService] Rate limit pause cancelled.");
            _rateLimitGate.Release();
            throw;
        }

        // Resume all threads
        _rateLimitGate.Release();
        onStatus?.Invoke("Rate limit pause over, resuming...");
        Console.WriteLine($"[SyncService] Rate limit pause over. Resuming downloads.");
    }

    /// <summary>
    /// Interlocked flag to ensure only one thread handles the rate-limit pause.
    /// </summary>
    private int _rateLimitHandlerFlag;

    /// <summary>
    /// Download a single beatmap set.
    /// Returns (success, isRateLimited, retryAfterDate, retryAfterSeconds).
    /// </summary>
    private async Task<(bool Success, bool IsRateLimited, DateTimeOffset? RetryAfterDate, int? RetryAfterSeconds)> DownloadBeatmapSetWithRateLimitAsync(
        int setId, string downloadDir, CancellationToken ct)
    {
        try
        {
            var destPath = Path.Combine(downloadDir, $"{setId}.osz");
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                return (true, false, null, null);

            var url = GetDownloadUrl(setId);
            Console.WriteLine($"[SyncService] Downloading: {url}");

            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                Console.WriteLine($"[SyncService] RATE LIMITED (429) on set {setId}");

                var retryAfter = response.Headers.RetryAfter;
                DateTimeOffset? retryDate = retryAfter?.Date;
                int? retrySeconds = retryAfter?.Delta.HasValue == true
                    ? (int)retryAfter.Delta.Value.TotalSeconds
                    : null;

                Console.WriteLine($"[SyncService] Retry-After Date={retryDate}, Seconds={retrySeconds}");

                // Still log the full response for debugging
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[SyncService] 429 body: {body}");

                return (false, true, retryDate, retrySeconds);
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] HTTP {response.StatusCode} for set {setId}");
                return (false, false, null, null);
            }

            await using var fs = File.Create(destPath);
            await response.Content.CopyToAsync(fs, ct);
            return (File.Exists(destPath) && new FileInfo(destPath).Length > 0, false, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] Error downloading set {setId}: {ex.Message}");
            return (false, false, null, null);
        }
    }

    public async Task<int> DeleteExtraSetsAsync(IEnumerable<int> extraSetIds, string osuPath,
        IProgress<string>? progress = null)
    {
        int deleted = 0;
        var downloadDir = Path.Combine(osuPath, "downloads");
        foreach (var setId in extraSetIds)
        {
            try
            {
                var oszFile = Path.Combine(downloadDir, $"{setId}.osz");
                if (File.Exists(oszFile)) { File.Delete(oszFile); deleted++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SyncService] Delete error {setId}: {ex.Message}");
            }
        }
        progress?.Report($"Deleted {deleted} extra sets.");
        return deleted;
    }

    private string GetDownloadUrl(int setId)
    {
        var source = _settings.Settings.DownloadSource;
        return source == "catboy"
            ? $"https://catboy.best/d/{setId}n"
            : $"https://osu.ppy.sh/beatmapsets/{setId}/download";
    }
}
