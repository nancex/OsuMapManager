using System;

namespace OsuMapManager.Models;

public class BeatmapSetEntry
{
    public int BeatmapSetId { get; set; }
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public BeatmapGenre GenreId { get; set; }
    public BeatmapLanguage LanguageId { get; set; }
    public int? ReleaseYear { get; set; }
    public DateTimeOffset? ApprovedDate { get; set; }
    public BeatmapStatus Approved { get; set; }
    public bool HasVideo { get; set; }
    public int FavouriteCount { get; set; }
    public int PlayCount { get; set; }
}
