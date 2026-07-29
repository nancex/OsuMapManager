using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsuMapManager.Views;

public partial class AboutViewControl : UserControl
{
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
}
