namespace OsuMapManager.Models;

public class CollectionInfo
{
    public string Name { get; set; } = string.Empty;
    public int BeatmapCount { get; set; }
    public System.Collections.Generic.List<int> BeatmapOnlineIds { get; set; } = new();
}
