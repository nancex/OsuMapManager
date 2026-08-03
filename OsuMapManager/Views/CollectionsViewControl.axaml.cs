using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsuMapManager.Views;

public partial class CollectionsViewControl : UserControl
{
    public CollectionsViewControl()
    {
        InitializeComponent();
    }

    private void IeNav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string tag) return;

        Console.WriteLine($"[CollectionsView] IE nav clicked: {tag}");

        foreach (var b in new[] { IeImportBtn, IeExportBtn, IeTrimBtn })
        {
            b.Classes.Remove("Selected");
            b.Classes.Add("NavButton");
        }
        btn.Classes.Add("Selected");

        ImportMode.IsVisible = tag == "import";
        ExportMode.IsVisible = tag == "export";
        TrimMode.IsVisible = tag == "trim";

        if (DataContext is OsuMapManager.ViewModels.MainViewModel mvm)
        {
            mvm.CollectionsVm.IsImportMode = tag == "import";
            mvm.CollectionsVm.IsTrimMode = tag == "trim";
        }
    }
}


