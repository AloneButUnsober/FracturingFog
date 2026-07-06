using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

// Wave 2.9 — Equation morph picker. Drives a per-frame hot-load + render loop
// via the VM's RenderAndSaveRequested delegate. Hybrid-shell: a UserControl
// hosted modeless by AvaloniaShellBootstrap; the VM's CloseRequested Action is
// wired to the host's close by the Bootstrap launcher.
public sealed partial class EquationMorphView : UserControl
{
    public EquationMorphView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
