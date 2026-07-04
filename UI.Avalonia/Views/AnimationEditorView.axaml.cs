using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Animation Editor. Modeless floating editor for user-defined
/// procedural animation assets (Animation Roadmap Phase 3c). Host wires the
/// VM events: AnimationSavedToLibrary, AnimationDeletedFromLibrary,
/// CloseRequested, MessageRequested.
/// </summary>
public sealed partial class AnimationEditorView : Window
{
    public AnimationEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }
}
