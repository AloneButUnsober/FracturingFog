using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Region Editor. Modeless floating editor for a saved region's
/// metadata (Animation Roadmap Sub-goal B, Phase R1). Host wires the VM
/// events: RegionSavedToLibrary, CloseRequested, MessageRequested. Geometry
/// is read-only here — Save Region from the live view handles re-framing.
/// </summary>
public sealed partial class RegionEditorView : Window
{
    public RegionEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }
}
