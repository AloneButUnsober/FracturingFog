using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

// Wave 2.8 — Equation cookbook dialog. Modal pick-list of curated CalcGen
// DSL equations. Owner (UserEquationView) listens for the VM's Accepted
// event before the window closes itself via CloseRequested.
public sealed partial class CookbookView : Window
{
    public CookbookView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CookbookViewModel vm)
                vm.CloseRequested += () => Close();
        };
    }
}
