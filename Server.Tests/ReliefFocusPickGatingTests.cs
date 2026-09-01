// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 click-to-focus (#400) — the controller side of the gesture. Alt+double-click
// routes to the ReliefFocusPickHandler instead of recentering; a consumed pick
// returns before RaiseViewChanged, so no recenter fires. Without Alt, or when the
// handler declines, the normal double-click recenter still runs (ViewChanged fires).

using Xunit;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Server.Tests;

public sealed class ReliefFocusPickGatingTests
{
    private const int W = 800, H = 600;

    private static (FractalInputController c, FractalViewState s) Make()
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, Zoom = 1.0 };
        return (new FractalInputController(s), s);
    }

    private static PointerInput Click(InputModifiers mods) =>
        new(W / 2, H / 2, W, H, PointerButton.Left, mods);

    [Fact]
    public void AltDoubleClick_WithConsumingHandler_PicksFocus_AndSuppressesRecenter()
    {
        var (c, _) = Make();
        int picks = 0, viewChanges = 0;
        c.ReliefFocusPickHandler = _ => { picks++; return true; };   // consumes
        c.ViewChanged += (_, _) => viewChanges++;

        c.OnPointerDoubleClick(Click(InputModifiers.Alt));

        Assert.Equal(1, picks);
        Assert.Equal(0, viewChanges);   // recenter suppressed
    }

    [Fact]
    public void AltDoubleClick_WithDecliningHandler_FallsThroughToRecenter()
    {
        var (c, _) = Make();
        int picks = 0, viewChanges = 0;
        c.ReliefFocusPickHandler = _ => { picks++; return false; };  // declines (e.g. sky)
        c.ViewChanged += (_, _) => viewChanges++;

        c.OnPointerDoubleClick(Click(InputModifiers.Alt));

        Assert.Equal(1, picks);
        Assert.Equal(1, viewChanges);   // normal recenter ran
    }

    [Fact]
    public void DoubleClick_WithoutAlt_IgnoresHandler_AndRecenters()
    {
        var (c, _) = Make();
        int picks = 0, viewChanges = 0;
        c.ReliefFocusPickHandler = _ => { picks++; return true; };
        c.ViewChanged += (_, _) => viewChanges++;

        c.OnPointerDoubleClick(Click(InputModifiers.None));

        Assert.Equal(0, picks);         // handler not consulted without Alt
        Assert.Equal(1, viewChanges);   // normal recenter ran
    }

    [Fact]
    public void AltDoubleClick_WithNoHandler_Recenters()
    {
        var (c, _) = Make();
        int viewChanges = 0;
        c.ViewChanged += (_, _) => viewChanges++;

        c.OnPointerDoubleClick(Click(InputModifiers.Alt));

        Assert.Equal(1, viewChanges);   // harmless off the relief path
    }
}
