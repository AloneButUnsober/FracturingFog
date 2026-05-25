using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>ColorThemeEditor</c>. Floating modeless editor for
/// data-driven colour themes. The host wires the VM's events:
/// PreviewRequested, RegionRequested, EditorThemeSelected,
/// ThemeSavedToLibrary, HelpRequested, MessageRequested, SaveFileRequested,
/// FromImageRequested.
/// </summary>
public sealed partial class ColorThemeEditorView : Window
{
    public ColorThemeEditorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
