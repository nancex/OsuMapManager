using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Models;
using OsuMapManager.Services;

namespace OsuMapManager.ViewModels;

public partial class QueryViewModel : ViewModelBase
{
    private OsuDataService? _osuData;
    private BeatmapDataService? _beatmapData;

    // --- Mode toggle ---
    [ObservableProperty]
    public partial bool IsLocalMode { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDatabaseMode { get; set; }

    // --- Filter ---
    public BigFilterViewModel QueryFilter { get; } = new() { Name = "Query Filter", IsCollapsed = false };

    // --- Results (separate collections per mode) ---
    public ObservableCollection<QueryBeatmapSetResult> LocalResults { get; } = new();
    public ObservableCollection<QueryBeatmapSetResult> DatabaseResults { get; } = new();

    // Currently visible collection
    [ObservableProperty]
    public partial ObservableCollection<QueryBeatmapSetResult> Results { get; set; }

    [ObservableProperty]
    public partial bool IsQuerying { get; set; }

    [ObservableProperty]
    public partial string QueryStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    public QueryViewModel()
    {
        Console.WriteLine("[QueryViewModel] Created.");
        Results = LocalResults;
    }

    public void SetServices(OsuDataService? osuData, BeatmapDataService? beatmapData)
    {
        _osuData = osuData;
        _beatmapData = beatmapData;
        Console.WriteLine("[QueryViewModel] Services set.");
    }

    [RelayCommand]
    public void SetLocalMode()
    {
        IsLocalMode = true;
        IsDatabaseMode = false;
        Results = LocalResults;
        HasResults = LocalResults.Count > 0;
        QueryStatus = HasResults ? $"Showing {LocalResults.Count} local result(s)." : string.Empty;
    }

    [RelayCommand]
    public void SetDatabaseMode()
    {
        IsLocalMode = false;
        IsDatabaseMode = true;
        Results = DatabaseResults;
        HasResults = DatabaseResults.Count > 0;
        QueryStatus = HasResults ? $"Showing {DatabaseResults.Count} database result(s)." : string.Empty;
    }

    [RelayCommand]
    public async Task ExecuteQueryAsync()
    {
        if (IsLocalMode)
        {
            if (_osuData == null)
            {
                QueryStatus = "osu! data not available.";
                return;
            }
            await QueryLocalAsync();
        }
        else
        {
            if (_beatmapData == null || !_beatmapData.IsDataReady)
            {
                QueryStatus = "Beatmap database not ready.";
                return;
            }
            await QueryDatabaseAsync();
        }
    }

    private async Task QueryLocalAsync()
    {
        IsQuerying = true;
        QueryStatus = "Querying local data...";

        try
        {
            var filter = QueryFilter.ToSyncFilter();

            // Run heavy I/O on background thread
            var results = await Task.Run(() => _osuData!.QueryLocalBeatmapSetsAsync(filter));

            // Clear and repopulate on UI thread
            LocalResults.Clear();
            foreach (var r in results)
                LocalResults.Add(r);

            Results = LocalResults;
            HasResults = LocalResults.Count > 0;
            QueryStatus = $"Found {LocalResults.Count} beatmap set(s).";
            Console.WriteLine($"[QueryViewModel] Local query: {LocalResults.Count} results.");
        }
        catch (Exception ex)
        {
            QueryStatus = $"Error: {ex.Message}";
            Console.WriteLine($"[QueryViewModel] Local query error: {ex}");
        }
        finally
        {
            IsQuerying = false;
        }
    }

    private async Task QueryDatabaseAsync()
    {
        IsQuerying = true;
        QueryStatus = "Querying database...";

        try
        {
            var filter = QueryFilter.ToSyncFilter();

            var results = await Task.Run(() => _beatmapData!.QueryBeatmapSetsAsync(filter));

            DatabaseResults.Clear();
            foreach (var r in results)
                DatabaseResults.Add(r);

            Results = DatabaseResults;
            HasResults = DatabaseResults.Count > 0;
            QueryStatus = $"Found {DatabaseResults.Count} beatmap set(s).";
            Console.WriteLine($"[QueryViewModel] DB query: {DatabaseResults.Count} results.");
        }
        catch (Exception ex)
        {
            QueryStatus = $"Error: {ex.Message}";
            Console.WriteLine($"[QueryViewModel] DB query error: {ex}");
        }
        finally
        {
            IsQuerying = false;
        }
    }
}
