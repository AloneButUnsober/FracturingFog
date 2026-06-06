using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
    }
}
