using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>ImagePaletteDialog</c>. Modal palette extractor with
/// drag-drop, 4 algorithms, and a Compare-All result grid. Host wires the
/// VM's events: BrowseRequested (file picker), ResultAccepted (apply the
/// stops), Cancelled (close), MessageRequested (message box). The view
/// itself handles drag-drop file paths and forwards them through the host
/// (which decodes the bitmap and calls VM.SetImage).
/// </summary>
public sealed partial class ImagePaletteView : Window
{
    public ImagePaletteView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImagePaletteViewModel vm)
            {
                vm.Cancelled += (_, _) => Close();
                vm.ResultAccepted += (_, _) => Close();
            }
        };
    }

    /// <summary>
    /// Raised when the user drops files onto the window. Host listens and
    /// decodes the first file, then calls <see cref="ImagePaletteViewModel.SetImage"/>.
    /// </summary>
    public event System.EventHandler<string>? FileDropped;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        FileDropped?.Invoke(this, path);
    }
}
