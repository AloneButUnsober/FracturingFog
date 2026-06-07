using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Controls;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Topmost borderless window hosting the MiniDepthControl.
/// Configure(...) on the inner control is wired in MainWindow code-behind
/// the first time the user opens the panel.</summary>
public sealed partial class MiniDepthWindow : Window
{
    public MiniDepthControl Inner { get; private set; } = null!;

    /// <summary>Raised when the user double-taps the drag handle. The host
    /// MainWindow forwards this to MiniWindowTether.ResetAnchor() so the
    /// window snaps back to its default corner position.</summary>
    public event EventHandler? ResetAnchorRequested;

    public MiniDepthWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Inner = this.FindControl<MiniDepthControl>("Depth")
            ?? throw new InvalidOperationException("MiniDepthControl x:Name=Depth missing");

        var handle = this.FindControl<Border>("DragHandle");
        if (handle != null)
        {
            handle.PointerPressed += OnDragHandlePointerPressed;
        }
    }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            ResetAnchorRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        try { BeginMoveDrag(e); }
        catch { /* platform may not support; ignore */ }
    }
}
