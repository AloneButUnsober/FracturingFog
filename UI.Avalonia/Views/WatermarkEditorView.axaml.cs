using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Watermark Editor. Modeless floating editor for user-defined
/// watermarks. Hybrid-shell: a UserControl hosted modeless by
/// MainWindow.SyncWatermarkEditor; the host + shell flag own chrome + close =>
/// hide, and ShellViewModel wires the VM events (PreviewRequested,
/// WatermarkSavedToLibrary, WatermarkDeletedFromLibrary, HelpRequested,
/// CloseRequested, MessageRequested).
/// </summary>
public sealed partial class WatermarkEditorView : UserControl
{
    public WatermarkEditorView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/Avalonia-UserGuide.md",
            "Watermark",
            "Watermark Editor — Help");
}
