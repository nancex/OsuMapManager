using System;

namespace OsuMapManager.Models;

public class BeatmapEntry
{
    public int BeatmapId { get; set; }
    public int BeatmapSetId { get; set; }
    public GameMode Mode { get; set; }
    public BeatmapStatus Approved { get; set; }
    public BeatmapGenre GenreId { get; set; }
    public BeatmapLanguage LanguageId { get; set; }
    public int? KeyCount { get; set; }
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int TotalLength { get; set; }
    public int HitLength { get; set; }
    public DateTimeOffset LastUpdate { get; set; }
    public double DifficultyRating { get; set; }
    public double BPM { get; set; }
}
