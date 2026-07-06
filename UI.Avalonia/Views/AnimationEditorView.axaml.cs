using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Animation Editor. Modeless floating editor for user-defined
/// procedural animation assets (Animation Roadmap Phase 3c). Hybrid-shell: a
/// UserControl hosted modeless by MainWindow.SyncAnimationEditor; the host +
/// shell flag own chrome + close => hide, and ShellViewModel wires the VM events
/// (AnimationSavedToLibrary, AnimationDeletedFromLibrary, CloseRequested,
/// MessageRequested).
/// </summary>
public sealed partial class AnimationEditorView : UserControl
{
    public AnimationEditorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
