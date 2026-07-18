using System;
using System.Collections.Generic;

namespace OsuMapManager.Tester.Models;

public class SyncFilter
{
    public HashSet<BeatmapGenre> Genres { get; set; } = new();
    public DateTimeOffset? SubmitDateFrom { get; set; }
    public DateTimeOffset? SubmitDateTo { get; set; }
    public double? DifficultyRatingMin { get; set; }
    public double? DifficultyRatingMax { get; set; }
    public string Artist { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public bool IncludeRanked { get; set; } = true;
    public bool IncludeLoved { get; set; }
    public bool IncludeQualified { get; set; }
    public bool IncludeApproved { get; set; } = true;
    public HashSet<GameMode> Modes { get; set; } = new();
    public int? ManiaKeyCount { get; set; } = 4;
}
