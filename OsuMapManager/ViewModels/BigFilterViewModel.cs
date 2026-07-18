using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuMapManager.Models;

namespace OsuMapManager.ViewModels;

public partial class BigFilterViewModel : ViewModelBase
{
    // --- Identity ---
    [ObservableProperty]
    public partial string Name { get; set; } = "Filter";

    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    // --- Genre selection ---
    public ObservableCollection<GenreItem> Genres { get; } = new();

    [ObservableProperty]
    public partial bool AllGenresSelected { get; set; } = true;

    // --- Submit Date range ---
    [ObservableProperty]
    public partial DateTimeOffset? SubmitDateFrom { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? SubmitDateTo { get; set; }

    public DateTimeOffset MinSubmitDate => new DateTimeOffset(2007, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // --- Difficulty Rating ---
    [ObservableProperty]
    public partial double? DifficultyRatingMin { get; set; }

    [ObservableProperty]
    public partial double? DifficultyRatingMax { get; set; }

    // --- Artist / Creator ---
    [ObservableProperty]
    public partial string Artist { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Creator { get; set; } = string.Empty;

    // --- Status filters ---
    [ObservableProperty]
    public partial bool IncludeRanked { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeApproved { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeQualified { get; set; }

    [ObservableProperty]
    public partial bool IncludeLoved { get; set; }

    // --- Mode selection ---
    [ObservableProperty]
    public partial bool OsuMode { get; set; }

    [ObservableProperty]
    public partial bool TaikoMode { get; set; }

    [ObservableProperty]
    public partial bool CatchMode { get; set; }

    [ObservableProperty]
    public partial bool ManiaMode { get; set; } = true;

    // --- Mania key count ---
    [ObservableProperty]
    public partial bool IsManiaSelected { get; set; } = true;

    [ObservableProperty]
    public partial int ManiaKeyCount { get; set; } = 4;

    public BigFilterViewModel()
    {
        // Initialize genre list
        foreach (BeatmapGenre genre in Enum.GetValues<BeatmapGenre>())
        {
            if (genre == BeatmapGenre.Any) continue;
            Genres.Add(new GenreItem
            {
                Genre = genre,
                DisplayName = GenreDisplayName(genre),
                IsSelected = true
            });
        }
    }

    /// <summary>
    /// Toggle all genres on/off.
    /// </summary>
    partial void OnAllGenresSelectedChanged(bool value)
    {
        foreach (var g in Genres)
            g.IsSelected = value;
    }

    /// <summary>
    /// Update IsManiaSelected when ManiaMode changes.
    /// </summary>
    partial void OnManiaModeChanged(bool value)
    {
        IsManiaSelected = value;
    }

    [RelayCommand]
    public void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    /// <summary>
    /// Build a SyncFilter from current UI selections.
    /// </summary>
    public SyncFilter ToSyncFilter()
    {
        var filter = new SyncFilter();

        // Genres
        if (!AllGenresSelected)
        {
            foreach (var g in Genres.Where(g => g.IsSelected))
                filter.Genres.Add(g.Genre);
        }
        else
        {
            filter.Genres.Add(BeatmapGenre.Any);
        }

        // Submit date range (ensure from <= to)
        if (SubmitDateFrom.HasValue && SubmitDateTo.HasValue && SubmitDateFrom.Value > SubmitDateTo.Value)
        {
            filter.SubmitDateFrom = SubmitDateTo;
            filter.SubmitDateTo = SubmitDateFrom;
        }
        else
        {
            filter.SubmitDateFrom = SubmitDateFrom;
            filter.SubmitDateTo = SubmitDateTo;
        }

        // Difficulty Rating
        filter.DifficultyRatingMin = DifficultyRatingMin;
        filter.DifficultyRatingMax = DifficultyRatingMax;

        // Artist / Creator
        filter.Artist = Artist ?? string.Empty;
        filter.Creator = Creator ?? string.Empty;

        // Status
        filter.IncludeRanked = IncludeRanked;
        filter.IncludeApproved = IncludeApproved;
        filter.IncludeQualified = IncludeQualified;
        filter.IncludeLoved = IncludeLoved;

        // Modes
        if (OsuMode) filter.Modes.Add(GameMode.Osu);
        if (TaikoMode) filter.Modes.Add(GameMode.Taiko);
        if (CatchMode) filter.Modes.Add(GameMode.Catch);
        if (ManiaMode) filter.Modes.Add(GameMode.Mania);

        // Mania key count
        if (ManiaMode && filter.Modes.Count == 1)
            filter.ManiaKeyCount = ManiaKeyCount;

        Console.WriteLine($"[BigFilterVM] Built filter: name={Name}, genres={filter.Genres.Count}, modes={filter.Modes.Count}, diffRating={filter.DifficultyRatingMin}-{filter.DifficultyRatingMax}");
        return filter;
    }

    private static string GenreDisplayName(BeatmapGenre genre) => genre switch
    {
        BeatmapGenre.VideoGame => "Video Game",
        BeatmapGenre.HipHop => "Hip Hop",
        _ => genre.ToString()
    };
}

/// <summary>
/// Status result for one BigFilter after Check Status.
/// </summary>
public class BigFilterStatusItem : ViewModelBase
{
    public string FilterName { get; set; } = string.Empty;
    public int LocalCount { get; set; }
    public int DatabaseCount { get; set; }
    public string DisplayText => $"{LocalCount} / {DatabaseCount}";
}

/// <summary>
/// Item for genre selection list.
/// </summary>
public partial class GenreItem : ViewModelBase
{
    public BeatmapGenre Genre { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
