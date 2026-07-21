using System.Collections.Generic;

namespace OsuMapManager.Models;

public class AppSettings
{
    public string OsuInstallPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public int DownloadThreads { get; set; } = 4;
    public string DownloadSource { get; set; } = "official"; // "official" or "catboy"
    public string DownloadPath { get; set; } = string.Empty;
    public List<BigFilter> SavedBigFilters { get; set; } = new();
}