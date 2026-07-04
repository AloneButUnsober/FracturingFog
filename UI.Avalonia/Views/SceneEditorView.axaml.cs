using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Scene Editor. Modeless floating editor for SceneData assets (Scene
/// Engine Roadmap Phase S5). Host wires the VM events: SceneSavedToLibrary,
/// SceneDeletedFromLibrary, PreviewShotRequested, StopPreviewRequested,
/// CloseRequested, MessageRequested.
/// </summary>
public sealed partial class SceneEditorView : Window
{
    public SceneEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }
}
