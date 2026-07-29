using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsuMapManager.Views;

public partial class AboutViewControl : UserControl
{
    private const string CurrentVersion = "v0.1.0";
    private const string ReleasesApiUrl = "https://api.github.com/repos/nancex/OsuMapManager/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/nancex/OsuMapManager/releases/latest";

    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "OsuMapManager" } }
    };

    public AboutViewControl()
    {
        InitializeComponent();
    }

    private void GitHubLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/nancex/OsuMapManager",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AboutView] Failed to open URL: {ex.Message}");
        }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking...";
        await CheckForUpdatesAsync(showNoUpdateDialog: true);
    }

    /// <summary>
    /// Check GitHub for the latest release. If showNoUpdateDialog is false,
    /// only shows a dialog when an update is found (silent mode for startup).
    /// </summary>
    public async Task CheckForUpdatesAsync(bool showNoUpdateDialog = false)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(ReleasesApiUrl);
            using var doc = JsonDocument.Parse(response);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString();

            if (tagName != null && IsNewerVersion(tagName, CurrentVersion))
            {
                UpdateStatusText.Text = $"Update available: {tagName}";
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner != null)
                    UpdateDialog.ShowUpdateAvailable(owner, tagName, ReleasesPageUrl);
            }
            else
            {
                UpdateStatusText.Text = "You are up to date.";
                if (showNoUpdateDialog)
                {
                    var owner = TopLevel.GetTopLevel(this) as Window;
                    if (owner != null)
                        UpdateDialog.ShowNoUpdate(owner);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AboutView] Update check failed: {ex.Message}");
            UpdateStatusText.Text = "Check failed.";
            if (showNoUpdateDialog)
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner != null)
                    UpdateDialog.ShowError(owner, ex.Message);
            }
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.TrimStart('v').Split('.');
            var currentParts = current.TrimStart('v').Split('.');

            for (int i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
            {
                var l = i < latestParts.Length && int.TryParse(latestParts[i], out var lv) ? lv : 0;
                var c = i < currentParts.Length && int.TryParse(currentParts[i], out var cv) ? cv : 0;
                if (l != c) return l > c;
            }
            return false;
        }
        catch
        {
            // If parsing fails, fall back to string comparison
            return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) != 0;
        }
    }
}
