using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using OsuMapManager.ViewModels;

namespace OsuMapManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Block fullscreen - keep only maximize
        PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Maximized;
            }
        };

        Console.WriteLine("[MainWindow] Initialized.");
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SaveAllState();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Top navigation bar click handler.
    /// </summary>
    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string tag) return;

        Console.WriteLine($"[MainWindow] Nav clicked: {tag}");

        foreach (var b in new[] { NavSync, NavImportExport, NavQuery, NavSettings })
        {
            b.Classes.Remove("Selected");
            b.Classes.Add("NavButton");
        }
        btn.Classes.Add("Selected");

        SyncView.IsVisible = tag == "0";
        ImportExportView.IsVisible = tag == "1";
        QueryView.IsVisible = tag == "2";
        SettingsView.IsVisible = tag == "3";
    }

    /// <summary>
    /// Import/Export sub-navigation handler.
    /// </summary>
    private void IeNav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string tag) return;

        Console.WriteLine($"[MainWindow] IE nav clicked: {tag}");

        foreach (var b in new[] { IeImportBtn, IeExportBtn })
        {
            b.Classes.Remove("Selected");
            b.Classes.Add("NavButton");
        }
        btn.Classes.Add("Selected");

        ImportMode.IsVisible = tag == "import";
        ExportMode.IsVisible = tag == "export";
    }

    /// <summary>
    /// Copy the text content of a tapped cell to clipboard.
    /// </summary>
    private async void CopyCell_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(tb.Text);
                    Console.WriteLine($"[MainWindow] Copied to clipboard: {tb.Text}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainWindow] Copy failed: {ex.Message}");
            }
        }
    }

}
