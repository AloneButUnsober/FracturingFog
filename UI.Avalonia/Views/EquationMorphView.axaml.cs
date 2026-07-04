using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

// Wave 2.9 — Equation morph dialog. Modeless picker that drives a per-frame
// hot-load + render loop via the VM's RenderAndSaveRequested delegate.
public sealed partial class EquationMorphView : Window
{
    public EquationMorphView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is EquationMorphViewModel vm)
                vm.CloseRequested += () => Close();
        };
    }
}
