using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        // Auto-check for updates silently (only shows dialog if update found)
        await AboutView.CheckForUpdatesAsync(showNoUpdateDialog: false);
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

        foreach (var b in new[] { NavSync, NavImportExport, NavQuery, NavSettings, NavAbout })
        {
            b.Classes.Remove("Selected");
            b.Classes.Add("NavButton");
        }
        btn.Classes.Add("Selected");

        SyncView.IsVisible = tag == "0";
        ImportExportView.IsVisible = tag == "1";
        QueryView.IsVisible = tag == "2";
        SettingsView.IsVisible = tag == "3";
        AboutView.IsVisible = tag == "4";
    }
}

