using Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Phase D shared launcher for the markdown-backed contextual Help viewer.
/// Each dialog's `?` button raises a (docId, anchor, title) request; the
/// view code-behind calls <see cref="Show(Window?, string, string?, string)"/>
/// so we don't repeat the HelpViewerView constructor + DataContext wiring
/// across every dialog. Modeless — opens above the dialog without blocking it.
/// </summary>
internal static class HelpViewerLauncher
{
    public static void Show(Window? owner, string docId, string? anchor, string title)
    {
        var view = new HelpViewerView
        {
            DataContext = new HelpViewerViewModel(docId, anchor, title),
        };
        if (owner != null) view.Show(owner);
        else view.Show();
    }
}
