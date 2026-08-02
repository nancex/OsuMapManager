using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsuMapManager.Views;

public partial class ImportExportViewControl : UserControl
{
    public ImportExportViewControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Import/Export sub-navigation handler.
    /// </summary>
    private void IeNav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string tag) return;

        Console.WriteLine($"[ImportExportView] IE nav clicked: {tag}");

        foreach (var b in new[] { IeImportBtn, IeExportBtn })
        {
            b.Classes.Remove("Selected");
            b.Classes.Add("NavButton");
        }
        btn.Classes.Add("Selected");

        ImportMode.IsVisible = tag == "import";
        ExportMode.IsVisible = tag == "export";
        // Sync ViewModel state with UI tab
        if (DataContext is OsuMapManager.ViewModels.MainViewModel mvm)
        {
            mvm.ImportExportVm.IsImportMode = tag == "import";
        }
    }
}
