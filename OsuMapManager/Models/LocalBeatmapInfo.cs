using System;

namespace OsuMapManager.Models;

public class LocalBeatmapInfo
{
    public int OnlineId { get; set; }
    public int BeatmapSetId { get; set; }
    public GameMode Mode { get; set; }
    public int? KeyCount { get; set; }
    public string DifficultyName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public BeatmapStatus Status { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public string BeatmapSetHash { get; set; } = string.Empty;
    public string MD5Hash { get; set; } = string.Empty;
}
