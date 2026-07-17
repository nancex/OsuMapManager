namespace OsuMapManager.Tester.Models;

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

    // Date range (year-month-day)  used when DateFrom/DateTo are set
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
}
