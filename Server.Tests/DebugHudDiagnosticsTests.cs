// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #311 — expanded 3D-lighting HUD diagnostics (ScreenSpacePost.ApplyDebugHud).
// Each overlay is a DebugHudFlags bit drawn on the colour byte buffer (no font,
// camera-free). These lock the non-degenerate behaviour: the toggle draws where
// it should and leaves control pixels alone.

using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DebugHudDiagnosticsTests
{
    private const int W = 220, H = 165;
    private const uint Mid = 0xFF808080u; // solid mid-grey canvas

    private static uint[] Canvas() { var b = new uint[W * H]; for (int i = 0; i < b.Length; i++) b[i] = Mid; return b; }
    private static long Diffs(uint[] a, uint[] b) { long n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }

    // Ref delegate — LightingFxData is a struct, so a by-value Action would mutate
    // a copy and the tuned fields would never reach ApplyDebugHud.
    private delegate void FxTune(ref LightingFxData fx);

    private static uint[] WithHud(int bits, FxTune? tune = null)
    {
        var fx = LightingFxData.CreateDefault();
        fx.DebugHudFlags = bits;
        tune?.Invoke(ref fx);
        var buf = Canvas();
        ScreenSpacePost.ApplyDebugHud(buf, W, H, in fx);
        return buf;
    }

    [Fact]
    public void FlagsZero_Is_NoOp()
    {
        var buf = WithHud(0);
        var clean = Canvas();
        Assert.Equal(0, Diffs(buf, clean));
    }

    // ── Slice 2 (#313) — composition guides ───────────────────────────
    [Fact]
    public void CompositionGuides_Draw_Thirds_Lines()
    {
        var buf = WithHud(0x10);

        // The x = W/3 column should carry a drawn (alpha-blended) line.
        int xThird = W / 3;
        int drawnInColumn = 0;
        for (int y = 0; y < H; y++) if (buf[y * W + xThird] != Mid) drawnInColumn++;
        Assert.True(drawnInColumn > H / 2, $"thirds line missing at x={xThird} ({drawnInColumn}/{H})");

        // A pixel off every guide line / cross / frame stays untouched.
        Assert.Equal(Mid, buf[(H / 3 + 7) * W + (W / 3 + 7)]);
    }

    // ── Slice 3 (#314) — clipping zebra ───────────────────────────────
    [Fact]
    public void Zebra_Stripes_Clipped_Highlights_And_Shadows_Only()
    {
        // Left half blown white, right half crushed black.
        var fx = LightingFxData.CreateDefault();
        fx.DebugHudFlags = 0x20;
        var buf = new uint[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            buf[y * W + x] = x < W / 2 ? 0xFFFFFFFFu : 0xFF000000u;
        ScreenSpacePost.ApplyDebugHud(buf, W, H, in fx);

        long over = 0, under = 0, mid = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0xFFFFCC00u) over++;   // yellow on blown highlight
            else if (buf[i] == 0xFF3399FFu) under++; // blue on crushed shadow
        }
        Assert.True(over > 100, $"expected highlight stripes (got {over})");
        Assert.True(under > 100, $"expected shadow stripes (got {under})");

        // A properly-exposed mid-grey frame gets NO zebra.
        var midBuf = WithHud(0x20);
        for (int i = 0; i < midBuf.Length; i++) if (midBuf[i] != Mid) mid++;
        Assert.Equal(0, mid);
    }

    // ── Slice 1 (#312) — light gauge + shaft-readiness lamp ───────────
    private static long YellowLampPixels(uint[] buf)
    { long n = 0; foreach (var c in buf) if (c == 0xFFFFCC00u) n++; return n; }

    [Fact]
    public void LightGauge_Lamp_Yellow_Only_When_Shafts_Will_Render()
    {
        // Raking key light (cos(phi) ~= 0.4) + fog + volume + shadow steps -> ready.
        var ready = WithHud(0x8, (ref LightingFxData fx) =>
        {
            fx.Light1.Phi = System.Math.Acos(0.4);
            fx.Light1.Intensity = 1.4;
            fx.FogDensity = 0.5;
            fx.VolumeSteps = 32;
            fx.ShadowSteps = 24;
            fx.ShadowLightMask = 0x1;
        });
        Assert.True(YellowLampPixels(ready) > 0, "shaft-ready rig should light the lamp yellow");

        // Same rig but ShadowSteps=0 -> uniform glow, no shafts -> lamp not lit.
        var notReady = WithHud(0x8, (ref LightingFxData fx) =>
        {
            fx.Light1.Phi = System.Math.Acos(0.4);
            fx.Light1.Intensity = 1.4;
            fx.FogDensity = 0.5;
            fx.VolumeSteps = 32;
            fx.ShadowSteps = 0;
        });
        Assert.Equal(0, YellowLampPixels(notReady));

        // The gauge still draws (bottom-right pixels changed) even when not ready.
        long changed = 0; var clean = Canvas();
        for (int i = 0; i < notReady.Length; i++) if (notReady[i] != clean[i]) changed++;
        Assert.True(changed > 20, $"gauge should draw regardless of readiness (got {changed})");
    }

    // ── Slice 4 (#315) — lookdev reference balls ──────────────────────
    private static double Lum(uint c)
        => ((c >> 16) & 0xFF) * 0.299 + ((c >> 8) & 0xFF) * 0.587 + (c & 0xFF) * 0.114;

    [Fact]
    public void ReferenceBalls_GreyBall_Lit_Side_Follows_Key_Light()
    {
        // Single key light pointing +X (dir = (1,0,0)) -> the grey ball's +X
        // (right) hemisphere is lit, the -X (left) is shadow.
        var buf = WithHud(0x40, (ref LightingFxData fx) =>
        {
            fx.Light1.Theta = 0.0;
            fx.Light1.Phi = System.Math.PI / 2;   // horizon, straight to +X
            fx.Light1.Intensity = 3.0;
            fx.Light2.Intensity = 0.0;
            fx.Light3.Intensity = 0.0;
            fx.AmbientStrength = 0.05;
        });

        int r = 20, cx = 8 + r, cyG = H / 2 - r - 4;
        double litRight = Lum(buf[cyG * W + (cx + r / 2)]);
        double darkLeft = Lum(buf[cyG * W + (cx - r / 2)]);
        Assert.True(litRight > darkLeft + 20,
            $"grey ball lit side should be brighter (right={litRight:0}, left={darkLeft:0})");

        // Balls actually draw a meaningful number of pixels.
        long changed = 0; var clean = Canvas();
        for (int i = 0; i < buf.Length; i++) if (buf[i] != clean[i]) changed++;
        Assert.True(changed > 400, $"two balls should cover many pixels (got {changed})");
    }

    // ── Slice 5 (#316) — false-color + histogram ──────────────────────
    private static uint[] Fill(uint c) { var b = new uint[W * H]; for (int i = 0; i < b.Length; i++) b[i] = c; return b; }

    [Fact]
    public void FalseColor_Maps_Luma_To_Exposure_Zones()
    {
        var fx = LightingFxData.CreateDefault();
        fx.DebugHudFlags = 0x80;

        // Dark (luma ~20) -> a blue zone; bright (luma ~240) -> yellow zone.
        var dark = Fill(0xFF141414u);
        ScreenSpacePost.ApplyDebugHud(dark, W, H, in fx);
        Assert.Equal(0xFF3355CCu, dark[0]);   // zone hi=32

        var bright = Fill(0xFFF0F0F0u);
        ScreenSpacePost.ApplyDebugHud(bright, W, H, in fx);
        Assert.Equal(0xFFFFCC00u, bright[0]); // zone hi=250

        // Every pixel recoloured (full-frame view mode).
        Assert.All(dark, c => Assert.Equal(0xFF3355CCu, c));
    }

    [Fact]
    public void Histogram_Draws_Panel_And_Flags_White_Clip()
    {
        var fx = LightingFxData.CreateDefault();
        fx.DebugHudFlags = 0x100;
        var buf = Fill(0xFFFFFFFFu);   // fully blown -> energy piled at bin 255
        ScreenSpacePost.ApplyDebugHud(buf, W, H, in fx);

        long backdrop = 0, clipBar = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != 0xFFFFFFFFu) backdrop++;      // panel darkened / drew over white
            if (buf[i] == 0xFFFFCC00u) clipBar++;       // yellow clip column
        }
        Assert.True(backdrop > 200, $"histogram panel should draw (got {backdrop})");
        Assert.True(clipBar > 0, "white-clip should raise the rightmost (yellow) bar");
    }
}
