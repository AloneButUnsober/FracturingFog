// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Finder click-mode (#1187) — the controller side. An armed PointPickHandler
// takes the next left press instead of a pan start, clears itself (one shot),
// and drives the cross cursor while armed.

using System.Collections.Generic;

using Xunit;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Server.Tests;

public sealed class PointPickHandlerTests
{
    private const int W = 800, H = 600;

    private static (FractalInputController c, FractalViewState s) Make()
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, Zoom = 1.0 };
        return (new FractalInputController(s), s);
    }

    private static PointerInput Press(int x, int y, PointerButton b = PointerButton.Left) =>
        new(x, y, W, H, b, InputModifiers.None);

    private static PointerInput Move(int x, int y) =>
        new(x, y, W, H, PointerButton.Left, InputModifiers.None);

    [Fact]
    public void ArmedPick_TakesThePress_ThenDisarms()
    {
        var (c, _) = Make();
        var seen = new List<(int, int)>();
        c.PointPickHandler = e => { seen.Add((e.X, e.Y)); return true; };

        c.OnPointerDown(Press(100, 120));

        Assert.Equal(new[] { (100, 120) }, seen);
        Assert.Null(c.PointPickHandler);   // one shot
    }

    [Fact]
    public void ConsumedPress_StartsNoPan()
    {
        var (c, s) = Make();
        double cx = s.CenterX, cy = s.CenterY;
        int viewChanges = 0;
        c.ViewChanged += (_, _) => viewChanges++;
        c.PointPickHandler = _ => true;

        c.OnPointerDown(Press(400, 300));
        c.OnPointerMove(Move(500, 380));    // would drag the view if a pan had started
        c.OnPointerUp(Press(500, 380));

        Assert.Equal(cx, s.CenterX);
        Assert.Equal(cy, s.CenterY);
        Assert.Equal(0, viewChanges);
    }

    [Fact]
    public void RightPress_DoesNotTriggerThePick()
    {
        var (c, _) = Make();
        int picks = 0;
        c.PointPickHandler = _ => { picks++; return true; };

        c.OnPointerDown(Press(10, 10, PointerButton.Right));

        Assert.Equal(0, picks);
        Assert.NotNull(c.PointPickHandler);   // still armed
    }

    [Fact]
    public void ArmingAndDisarming_DriveTheCrossCursor()
    {
        var (c, _) = Make();
        var cursors = new List<InputCursor>();
        c.CursorRequested += (_, r) => cursors.Add(r.Cursor);

        c.PointPickHandler = _ => true;
        c.PointPickHandler = null;

        Assert.Equal(new[] { InputCursor.Cross, InputCursor.Default }, cursors);
    }
}
