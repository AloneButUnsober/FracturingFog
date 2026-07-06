using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of the legacy WinForms <c>FractalParamsDialog</c>.
/// Hybrid-shell: a UserControl hosted modeless by AvaloniaShellBootstrap
/// (PanelHostWindow). Live-edit — the host wires
/// <see cref="FractalParamsViewModel.ParamChanged"/> to a re-render.
///
/// Lighting & FX controls used to live inline as Expanders in this view.
/// They were extracted to <see cref="LightingFxDialog"/> in Phase 26b so the
/// Params panel stays compact; the "Open Lighting & FX…" button below shows
/// the secondary window (its own PanelHostWindow), both bound to the same VM.
/// </summary>
public sealed partial class FractalParamsView : UserControl
{
    private PanelHostWindow? _lightingFxWin;

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
        // Detach from the visual tree = the host window closed (close-and-
        // destroy). Stop any Julia timers explicitly to avoid a leaked
        // DispatcherTimer ticking against a stale animation, and close any open
        // Lighting FX child so it can't outlive its parent. (Was Window.Closing
        // before the UserControl conversion.)
        Unloaded += (_, _) =>
        {
            (DataContext as FractalParamsViewModel)?.StopAnimations();
            _lightingFxWin?.Close();
            _lightingFxWin = null;
        };
    }

    // The VM's Close button routes to the host window (a UserControl can't
    // close itself).
    private void OnVmCloseRequested(object? sender, System.EventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
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

        _lightingFxWin = new PanelHostWindow(
            new LightingFxDialog(),
            new PanelHostOptions(
                "Lighting & FX",
                Width: 520, Height: 720, MinWidth: 440, MinHeight: 400,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
        {
            DataContext = DataContext,
        };
        _lightingFxWin.Closed += (_, _) => _lightingFxWin = null;
        _lightingFxWin.Show(TopLevel.GetTopLevel(this) as Window);
    }
}
