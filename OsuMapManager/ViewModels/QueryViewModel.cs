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

    // --- Results ---
    public ObservableCollection<QueryBeatmapSetResult> Results { get; } = new();

    [ObservableProperty]
    public partial bool IsQuerying { get; set; }

    [ObservableProperty]
    public partial string QueryStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    public QueryViewModel()
    {
        Console.WriteLine("[QueryViewModel] Created.");
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
        Results.Clear();
        HasResults = false;
        QueryStatus = string.Empty;
    }

    [RelayCommand]
    public void SetDatabaseMode()
    {
        IsLocalMode = false;
        IsDatabaseMode = true;
        Results.Clear();
        HasResults = false;
        QueryStatus = string.Empty;
    }

    [RelayCommand]
    public async Task ExecuteQueryAsync()
    {
        Results.Clear();
        HasResults = false;

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
            var results = await _osuData!.QueryLocalBeatmapSetsAsync(filter);
            foreach (var r in results)
                Results.Add(r);

            HasResults = Results.Count > 0;
            QueryStatus = $"Found {Results.Count} beatmap set(s).";
            Console.WriteLine($"[QueryViewModel] Local query: {Results.Count} results.");
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
            var results = await _beatmapData!.QueryBeatmapSetsAsync(filter);
            foreach (var r in results)
                Results.Add(r);

            HasResults = Results.Count > 0;
            QueryStatus = $"Found {Results.Count} beatmap set(s).";
            Console.WriteLine($"[QueryViewModel] DB query: {Results.Count} results.");
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
