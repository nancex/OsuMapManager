using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsuMapManager.Helpers;

namespace OsuMapManager.Views;

public partial class UpdateDialog : Window
{
    private string? _releaseUrl;

    public UpdateDialog()
    {
        InitializeComponent();
    }

    public static void ShowUpdateAvailable(Window owner, string latestVersion, string releaseUrl)
    {
        var dialog = new UpdateDialog();
        dialog.TitleText.Text = "Update Available!";
        dialog.MessageText.Text = $"A new version ({latestVersion}) is available.\nPlease visit GitHub to download the latest release.";
        dialog.OpenButton.IsVisible = true;
        dialog._releaseUrl = releaseUrl;
        dialog.ShowDialog(owner);
    }

    public static void ShowNoUpdate(Window owner)
    {
        var dialog = new UpdateDialog();
        dialog.TitleText.Text = "Up to Date";
        dialog.MessageText.Text = $"You are running the latest version ({AppVersion.Current}).";
        dialog.ShowDialog(owner);
    }

    public static void ShowError(Window owner, string error)
    {
        var dialog = new UpdateDialog();
        dialog.TitleText.Text = "Update Check Failed";
        dialog.MessageText.Text = $"Could not check for updates: {error}";
        dialog.ShowDialog(owner);
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_releaseUrl != null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _releaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdateDialog] Failed to open URL: {ex.Message}");
            }
        }
        Close();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
