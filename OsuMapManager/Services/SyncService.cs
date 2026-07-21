using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        CancellationToken ct = default)
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

        var tasks = setList.Select(async setId =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var success = await DownloadBeatmapSetAsync(setId, downloadDir, ct);
                var cur = Interlocked.Increment(ref current);
                if (success) Interlocked.Increment(ref downloaded);
                else Interlocked.Increment(ref failed);
                progress?.Report((cur, total, downloaded, failed));
                Console.WriteLine($"[SyncService] [{cur}/{total}] Set {setId}: {(success ? "OK" : "FAIL")}");
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"[SyncService] Done. OK: {downloaded}, Failed: {failed}");
        return (downloaded, failed);
    }

    private async Task<bool> DownloadBeatmapSetAsync(int setId, string downloadDir, CancellationToken ct)
    {
        try
        {
            var destPath = Path.Combine(downloadDir, $"{setId}.osz");
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                return true;

            var url = GetDownloadUrl(setId);
            Console.WriteLine($"[SyncService] Downloading: {url}");

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SyncService] HTTP {response.StatusCode} for set {setId}");
                return false;
            }

            await using var fs = File.Create(destPath);
            await response.Content.CopyToAsync(fs, ct);
            return File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SyncService] Error downloading set {setId}: {ex.Message}");
            return false;
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
