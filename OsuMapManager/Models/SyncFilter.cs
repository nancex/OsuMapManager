using System;
using System.Collections.Generic;

namespace OsuMapManager.Models;

public class SyncFilter
{
    public HashSet<BeatmapGenre> Genres { get; set; } = new();
    public int YearFrom { get; set; } = 2007;
    public int YearTo { get; set; } = DateTime.Now.Year;
    public bool IncludeRanked { get; set; } = true;
    public bool IncludeLoved { get; set; }
    public bool IncludeQualified { get; set; }
    public bool IncludeApproved { get; set; } = true;
    public HashSet<GameMode> Modes { get; set; } = new();
    public int? ManiaKeyCount { get; set; } = 4;
}
