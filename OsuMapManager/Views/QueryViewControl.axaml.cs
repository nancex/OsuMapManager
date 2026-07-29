using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
namespace OsuMapManager.Views;

public partial class QueryViewControl : UserControl
{
    public QueryViewControl()
    {
        InitializeComponent();
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
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(tb.Text);
                    Console.WriteLine($"[QueryView] Copied to clipboard: {tb.Text}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QueryView] Copy failed: {ex.Message}");
            }
        }
    }
}
