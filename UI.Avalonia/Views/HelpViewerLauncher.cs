using Avalonia.Controls;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Phase D shared launcher for the markdown-backed contextual Help viewer.
/// Each dialog's `?` button raises a (docId, anchor, title) request; the
/// view code-behind calls <see cref="Show(Window?, string, string?, string)"/>
/// so we don't repeat the HelpViewerView + host wiring across every dialog.
/// Modeless — opens above the dialog without blocking it. Hybrid-shell: the
/// viewer is a UserControl wrapped in a <see cref="PanelHostWindow"/> here.
/// </summary>
internal static class HelpViewerLauncher
{
    public static void Show(Window? owner, string docId, string? anchor, string title)
    {
        var vm = new HelpViewerViewModel(docId, anchor, title);
        var host = new PanelHostWindow(
            new HelpViewerView { DataContext = vm },
            new PanelHostOptions(
                string.IsNullOrEmpty(vm.Title) ? "Help" : vm.Title,
                Width: 820, Height: 640, MinWidth: 480, MinHeight: 320,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))));
        // VM's Close button (parameterless Action) closes the host window.
        vm.CloseRequested += () => host.Close();
        if (owner != null) host.Show(owner);
        else host.Show();
    }
}
