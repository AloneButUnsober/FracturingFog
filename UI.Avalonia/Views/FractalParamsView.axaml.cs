using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of the legacy WinForms <c>FractalParamsDialog</c>.
/// Live-edit modal — host wires <see cref="FractalParamsViewModel.ParamChanged"/>
/// to a re-render and shows the window non-modal (or modal) as desired.
/// Closes when the user clicks Close or fires the VM's CloseCommand.
/// </summary>
public sealed partial class FractalParamsView : Window
{
    public FractalParamsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FractalParamsViewModel vm)
            {
                vm.CloseRequested -= OnVmCloseRequested;
                vm.CloseRequested += OnVmCloseRequested;
            }
        };
    }

    private void OnVmCloseRequested(object? sender, System.EventArgs e) => Close();
}
