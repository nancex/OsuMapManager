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

    private SettingsService? _settings;

    public void SetServices(OsuDataService? osuData, BeatmapDataService? beatmapData, SettingsService? settings = null)
    {
        _osuData = osuData;
        _beatmapData = beatmapData;
        _settings = settings;
        Console.WriteLine("[QueryViewModel] Services set.");
    }

    [RelayCommand]
    public void SetLocalMode()
    {
        IsLocalMode = true;
        IsDatabaseMode = false;
        HasResults = LocalResults.Count > 0;
        QueryStatus = HasResults ? $"Showing {LocalResults.Count} local result(s)." : string.Empty;
    }

    [RelayCommand]
    public void SetDatabaseMode()
    {
        IsLocalMode = false;
        IsDatabaseMode = true;
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
            var rawResults = await Task.Run(() => _osuData!.QueryLocalBeatmapSetsAsync(filter));

            // Determine whether to cross-reference genre from Map Database
            var enableLookup = _settings?.Settings.EnableLocalGenreLookup ?? true;

            if (enableLookup && _beatmapData != null && _beatmapData.IsDataReady)
            {
                // Ensure DB data is loaded (lazy-load may not have triggered yet)
                await _beatmapData.EnsureLoadedAsync();

                foreach (var r in rawResults)
                {
                    var genre = _beatmapData.GetGenreDisplayName(r.Id);
                    r.Genre = genre ?? "Unspecified";
                }

                // Apply genre filter if active
                if (filter.Genres.Count > 0 && !filter.Genres.Contains(BeatmapGenre.Any))
                {
                    rawResults = rawResults.Where(r =>
                    {
                        // Map display name back to enum for comparison
                        var g = GenreFromDisplayName(r.Genre);
                        return filter.Genres.Contains(g);
                    }).ToList();
                }
            }
            else
            {
                // Genre lookup disabled — keep "N/A" and skip genre filtering
                // (local query already doesn'"t filter by genre)
            }

            // Sort by ID
            var sorted = rawResults.OrderBy(r => r.Id).ToList();

            // Clear and repopulate on UI thread
            LocalResults.Clear();
            foreach (var r in sorted)
                LocalResults.Add(r);

            HasResults = LocalResults.Count > 0;
            QueryStatus = $"Found {LocalResults.Count} beatmap set(s).";
            Console.WriteLine($"[QueryViewModel] Local query: {LocalResults.Count} results (genreLookup={enableLookup}).");
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

    /// <summary>
    /// Map a genre display name back to the BeatmapGenre enum.
    /// </summary>
    private static BeatmapGenre GenreFromDisplayName(string displayName)
    {
        return displayName switch
        {
            "Unspecified" => BeatmapGenre.Unspecified,
            "Video Game" => BeatmapGenre.VideoGame,
            "Anime" => BeatmapGenre.Anime,
            "Rock" => BeatmapGenre.Rock,
            "Pop" => BeatmapGenre.Pop,
            "Other" => BeatmapGenre.Other,
            "Novelty" => BeatmapGenre.Novelty,
            "Hip Hop" => BeatmapGenre.HipHop,
            "Electronic" => BeatmapGenre.Electronic,
            "Metal" => BeatmapGenre.Metal,
            "Classical" => BeatmapGenre.Classical,
            "Folk" => BeatmapGenre.Folk,
            "Jazz" => BeatmapGenre.Jazz,
            _ => BeatmapGenre.Any
        };
    }

    private async Task QueryDatabaseAsync()
    {
        IsQuerying = true;
        QueryStatus = "Querying database...";

        try
        {
            var filter = QueryFilter.ToSyncFilter();

            var rawResults = await Task.Run(() => _beatmapData!.QueryBeatmapSetsAsync(filter));

            // Sort by ID
            var sorted = rawResults.OrderBy(r => r.Id).ToList();

            DatabaseResults.Clear();
            foreach (var r in sorted)
                DatabaseResults.Add(r);

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
