using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Watermark Editor. Modeless floating editor for user-defined
/// watermarks. Host wires the VM events:
/// PreviewRequested, WatermarkSavedToLibrary, WatermarkDeletedFromLibrary,
/// HelpRequested, CloseRequested, MessageRequested.
/// </summary>
public sealed partial class WatermarkEditorView : Window
{
    public WatermarkEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(this,
            "User/Avalonia-UserGuide.md",
            "Watermark",
            "Watermark Editor — Help");
}
