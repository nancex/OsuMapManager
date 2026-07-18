namespace OsuMapManager.Models;

/// <summary>
/// A named, collapsible group of SyncFilter conditions.
/// Multiple BigFilters are unioned for sync purposes.
/// </summary>
public class BigFilter
{
    public string Name { get; set; } = "Filter 1";
    public bool IsCollapsed { get; set; }
    public SyncFilter Filter { get; set; } = new();
}
