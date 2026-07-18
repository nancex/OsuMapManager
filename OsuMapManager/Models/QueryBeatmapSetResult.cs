using System;

namespace OsuMapManager.Models;

public class QueryBeatmapSetResult
{
    public int Id { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public DateTimeOffset? SubmitDate { get; set; }
    public int BeatmapCount { get; set; }
    public string SubmitDateDisplay => SubmitDate?.ToString("yyyy-MM-dd") ?? "-";
}
