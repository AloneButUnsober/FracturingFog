using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of the legacy WinForms <c>FractalParamsDialog</c>.
/// Live-edit modal — host wires <see cref="FractalParamsViewModel.ParamChanged"/>
/// to a re-render and shows the window non-modal (or modal) as desired.
/// Closes when the user clicks Close or fires the VM's CloseCommand.
///
/// Lighting & FX controls used to live inline as Expanders in this view.
/// They were extracted to <see cref="LightingFxDialog"/> in Phase 26b so the
/// Params dialog stays compact; the "Open Lighting & FX…" button below
/// shows the secondary window, both bound to the same VM.
/// </summary>
public sealed partial class FractalParamsView : Window
{
    private LightingFxDialog? _lightingFxWin;

    public FractalParamsView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FractalParamsViewModel vm)
            {
                vm.CloseRequested -= OnVmCloseRequested;
                vm.CloseRequested += OnVmCloseRequested;
            }
        };
        Closing += (_, _) =>
        {
            // Window-chrome close (X / Alt+F4) bypasses the VM's CloseCommand,
            // so stop any timers explicitly to avoid a leaked DispatcherTimer
            // ticking against a stale Julia animation. Also close any open
            // Lighting FX child window so it can't outlive its parent.
            (DataContext as FractalParamsViewModel)?.StopAnimations();
            _lightingFxWin?.Close();
            _lightingFxWin = null;
        };
    }

    private void OnVmCloseRequested(object? sender, System.EventArgs e) => Close();

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(this,
            "User/Avalonia-UserGuide.md",
            "Params",
            "Fractal Params — Help");

    /// <summary>Open or re-focus the Lighting &amp; FX dialog. The child dialog
    /// shares the same <see cref="FractalParamsViewModel"/> so edits there
    /// fire ParamChanged through the same path as edits in this view.</summary>
    private void OnOpenLightingFxClick(object? sender, RoutedEventArgs e)
    {
        if (_lightingFxWin is { IsVisible: true })
        {
            _lightingFxWin.Activate();
            return;
        }

        _lightingFxWin = new LightingFxDialog { DataContext = DataContext };
        _lightingFxWin.Closed += (_, _) => _lightingFxWin = null;
        _lightingFxWin.Show(this);
    }
}
