namespace OsuMapManager.Models;

public class ImportCollectionStatus
{
    public string Name { get; set; } = string.Empty;
    public int TotalBeatmaps { get; set; }
    public int LocalBeatmaps { get; set; }
    public int MissingBeatmaps { get; set; }
    public int InCollection { get; set; }
}
