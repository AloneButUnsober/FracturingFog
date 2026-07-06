using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

// Wave 2.8 — Equation cookbook picker. Curated CalcGen DSL equations. Hybrid-
// shell: a UserControl hosted modeless by AvaloniaShellBootstrap (over the
// UserEquation editor). The VM raises Accepted (owner applies it) then
// CloseRequested; the Bootstrap launcher wires that Action to the host's close.
public sealed partial class CookbookView : UserControl
{
    public CookbookView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
