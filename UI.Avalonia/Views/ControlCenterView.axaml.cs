using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views.ControlCenterSections;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Phase S1 Control Center shell — a SplitView nav-rail + sectioned content
/// that re-presents <see cref="ViewModels.FloatingMenuViewModel"/> /
/// <see cref="ViewModels.ShellViewModel"/>. Hybrid-shell: a UserControl hosted
/// modeless by MainWindow.SyncControlCenter (PanelHostWindow), so it can dock
/// or pop out and is 2nd-monitor aware.
///
/// S2 — each section is its own UserControl. The docked view hosts one instance
/// inline; <see cref="OnDetachRequested"/> pops a second instance into its own
/// PanelHostWindow bound to the same VM, so docked + detached stay in lock-step.
/// </summary>
public sealed partial class ControlCenterView : UserControl
{
    // One floating window per detached section — a second detach of the same
    // section re-focuses the existing window rather than stacking duplicates.
    private readonly Dictionary<ControlCenterSection, PanelHostWindow> _detached = new();
    private ControlCenterViewModel? _wired;

    public ControlCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Rewire();
    }

    private void Rewire()
    {
        if (_wired != null) _wired.DetachRequested -= OnDetachRequested;
        _wired = DataContext as ControlCenterViewModel;
        if (_wired != null) _wired.DetachRequested += OnDetachRequested;
    }

    private void OnDetachRequested(object? sender, ControlCenterSection section)
    {
        if (DataContext is not ControlCenterViewModel vm) return;

        if (_detached.TryGetValue(section, out var existing))
        {
            try { existing.Activate(); } catch { /* window may be closing */ }
            return;
        }

        Control content = section switch
        {
            ControlCenterSection.View       => new ViewSectionView(),
            ControlCenterSection.Explore    => new ExploreSectionView(),
            ControlCenterSection.ColorLight => new ColorLightSectionView(),
            ControlCenterSection.Capture    => new CaptureSectionView(),
            ControlCenterSection.Assets     => new AssetsSectionView(),
            ControlCenterSection.Advanced   => new AdvancedSectionView(),
            _                               => new ViewSectionView(),
        };
        content.DataContext = vm;

        // Wrap so a tall section (e.g. Explore) can scroll inside the window.
        var scroller = new ScrollViewer
        {
            Content = content,
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var host = new PanelHostWindow(
            scroller,
            new PanelHostOptions(
                "Control Center — " + ControlCenterViewModel.LabelFor(section),
                Width: 360, MinWidth: 300, Height: 520, MinHeight: 200,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))));
        host.Closed += (_, _) => _detached.Remove(section);
        _detached[section] = host;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null) host.Show(owner);
        else host.Show();
    }
}
