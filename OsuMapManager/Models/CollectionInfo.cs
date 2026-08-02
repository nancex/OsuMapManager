using System.Collections.Generic;

namespace OsuMapManager.Models;

public class CollectionInfo
{
    public string Name { get; set; } = string.Empty;
    public int BeatmapCount { get; set; }
    public List<BeatmapRef> Beatmaps { get; set; } = new();
}
