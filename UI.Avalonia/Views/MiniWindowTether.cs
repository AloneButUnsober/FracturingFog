// Views/MiniWindowTether.cs
//
// Couples a borderless child Window (MiniMap / MiniDepth) to a main Window.
// Follows the main window's position, size and WindowState while honouring
// the user's manual drag/resize of the child. Double-tap on the child's
// drag handle invokes ResetAnchor() to snap back to the default corner.
//
// Offset semantics
//   The child's top-left position is stored as a delta from the main
//   window's chosen anchor corner (BottomLeft for MiniDepth, BottomRight
//   for MiniMap). When the user has not dragged the child manually, the
//   default delta is recomputed each apply from the child's current size +
//   inset, so a user-resized child still hugs the main's corner. After
//   the user drags the child, the delta is captured and held until the
//   user double-taps the drag handle.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FracturingFog.UI.Avalonia.Views;

internal sealed class MiniWindowTether : IDisposable
{
    public enum AnchorCorner { BottomLeft, BottomRight, TopLeft, TopRight }

    private bool IsRight  => _anchor is AnchorCorner.BottomRight or AnchorCorner.TopRight;
    private bool IsBottom => _anchor is AnchorCorner.BottomLeft or AnchorCorner.BottomRight;

    private readonly Window _main;
    private readonly Window _mini;
    private readonly AnchorCorner _anchor;
    private readonly int _insetDip;

    private bool _userPlaced;
    private PixelPoint _userOffset;
    private PixelPoint _lastApplied;
    private bool _hasApplied;
    private bool _wasVisibleBeforeMinimize;

    private readonly IDisposable _boundsSub;
    private readonly IDisposable _stateSub;

    public MiniWindowTether(Window main, Window mini, AnchorCorner anchor, int insetDip = 12)
    {
        _main = main;
        _mini = mini;
        _anchor = anchor;
        _insetDip = insetDip;

        _main.PositionChanged += OnMainPositionChanged;
        _boundsSub = _main.GetObservable(Visual.BoundsProperty).Subscribe(_ => Apply());
        _stateSub  = _main.GetObservable(Window.WindowStateProperty).Subscribe(OnMainStateChanged);

        _mini.PositionChanged += OnMiniPositionChanged;
    }

    /// <summary>Position the child window per the current anchor + offset.</summary>
    public void Apply()
    {
        if (!_mini.IsVisible) return;
        if (_main.WindowState == WindowState.Minimized) return;

        var anchorPx = MainAnchorCornerPx();
        PixelPoint pos = _userPlaced
            ? new PixelPoint(anchorPx.X + _userOffset.X, anchorPx.Y + _userOffset.Y)
            : DefaultPosition(anchorPx);

        _lastApplied = pos;
        _hasApplied = true;
        _mini.Position = pos;
    }

    /// <summary>Forget user-placed offset; snap back to default corner.</summary>
    public void ResetAnchor()
    {
        _userPlaced = false;
        Apply();
    }

    private PixelPoint MainAnchorCornerPx()
    {
        double scale = _main.DesktopScaling;
        int x = _main.Position.X + (IsRight  ? (int)(_main.Bounds.Width  * scale) : 0);
        int y = _main.Position.Y + (IsBottom ? (int)(_main.Bounds.Height * scale) : 0);
        return new PixelPoint(x, y);
    }

    private PixelPoint DefaultPosition(PixelPoint anchorPx)
    {
        double scale = _main.DesktopScaling;
        int inset  = (int)(_insetDip * scale);
        int miniW  = (int)(_mini.Width  * scale);
        int miniH  = (int)(_mini.Height * scale);
        int x = IsRight  ? anchorPx.X - miniW - inset : anchorPx.X + inset;
        int y = IsBottom ? anchorPx.Y - miniH - inset : anchorPx.Y + inset;
        return new PixelPoint(x, y);
    }

    private void OnMainPositionChanged(object? sender, PixelPointEventArgs e) => Apply();

    private void OnMainStateChanged(WindowState state)
    {
        if (state == WindowState.Minimized)
        {
            if (_mini.IsVisible)
            {
                _wasVisibleBeforeMinimize = true;
                _mini.Hide();
            }
        }
        else
        {
            if (_wasVisibleBeforeMinimize)
            {
                _wasVisibleBeforeMinimize = false;
                _mini.Show(_main);
                // Defer one tick so main's Position/Bounds settle.
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
            }
            else
            {
                Apply();
            }
        }
    }

    private void OnMiniPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!_hasApplied) return;
        if (_mini.Position == _lastApplied) return;

        var anchorPx = MainAnchorCornerPx();
        _userOffset = new PixelPoint(
            _mini.Position.X - anchorPx.X,
            _mini.Position.Y - anchorPx.Y);
        _userPlaced = true;
    }

    public void Dispose()
    {
        _main.PositionChanged -= OnMainPositionChanged;
        _mini.PositionChanged -= OnMiniPositionChanged;
        _boundsSub.Dispose();
        _stateSub.Dispose();
    }
}
