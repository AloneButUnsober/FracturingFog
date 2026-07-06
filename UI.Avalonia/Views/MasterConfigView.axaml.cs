// Views/MasterConfigView.axaml.cs
// D-5e. No background polling — values change rarely and a timer would
// clobber an in-progress edit. Triggers one Load on first appearance so the
// form reflects what the master is currently running; subsequent refreshes
// are operator-driven via the Load button.
//
// Hybrid-shell rule: this is a UserControl (docks or pops out). The modeless
// host window is built in MainWindow.SyncMasterConfig, which owns the close =>
// hide lifecycle and wires vm.CloseRequested -> host.Close().

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class MasterConfigView : UserControl
{
    public MasterConfigView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Loaded fires once when the control attaches to the visual tree (first
    // show) — the UserControl analogue of the former Window.Opened Load hook.
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MasterConfigViewModel vm)
            _ = vm.LoadAsync();
    }

    // D-6d — open the Distributed Rendering user guide jumped to the
    // "Master Config Dialog" section so the operator gets immediate
    // context for each live-tunable knob.
    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/Distributed-UserGuide.md",
            "Master Config Dialog",
            "Master Config — Help");
}
