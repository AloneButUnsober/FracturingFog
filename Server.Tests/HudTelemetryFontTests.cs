// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #318 — numeric telemetry + 5x7 bitmap font for the byte-buffer lighting HUD.
// Locks the font primitive (DrawText / MeasureText) and the telemetry panel
// (ApplyDebugHud bit 0x200): it draws, self-hides FPS/SS rows when the host
// hasn't stamped them, and skips small frames like every other HUD widget.

using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class HudTelemetryFontTests
{
    private const uint Bg = 0xFF000000u;
    private const uint Ink = 0xFFDDDDDDu;

    private static uint[] NewBuf(int w, int h)
    {
        var b = new uint[w * h];
        for (int i = 0; i < b.Length; i++) b[i] = Bg;
        return b;
    }

    // ── Font primitive ────────────────────────────────────────────────────

    [Fact]
    public void MeasureText_Is_Monospaced_Six_Px_Per_Char()
    {
        Assert.Equal(0, ScreenSpacePost.MeasureText(""));
        Assert.Equal(6, ScreenSpacePost.MeasureText("A"));
        Assert.Equal(18, ScreenSpacePost.MeasureText("ABC"));
        Assert.Equal(36, ScreenSpacePost.MeasureText("ABC", 2)); // scale doubles it
    }

    [Fact]
    public void DrawText_Hyphen_Lights_Exactly_The_Middle_Row()
    {
        // '-' glyph is row 3 (of 0..6) all five columns, every other row blank.
        int w = 32, h = 16;
        var buf = NewBuf(w, h);
        int x = 4, y = 2;
        int end = ScreenSpacePost.DrawText(buf, w, h, x, y, "-", Ink);
        Assert.Equal(x + 6, end); // one glyph advance

        for (int col = 0; col < 5; col++)
            Assert.Equal(Ink, buf[(y + 3) * w + (x + col)]);      // lit middle row
        // Rows above/below the bar are untouched background.
        for (int col = 0; col < 5; col++)
        {
            Assert.Equal(Bg, buf[(y + 2) * w + (x + col)]);
            Assert.Equal(Bg, buf[(y + 4) * w + (x + col)]);
        }
    }

    [Fact]
    public void DrawText_Is_Exact_Color_Not_Alpha_Blended()
    {
        // Alpha-1 fill so tests + downstream readers see the requested color.
        int w = 16, h = 16;
        var buf = NewBuf(w, h);
        ScreenSpacePost.DrawText(buf, w, h, 1, 1, "8", 0xFF00CCFFu);
        bool any = false;
        foreach (var p in buf) if (p == 0xFF00CCFFu) { any = true; break; }
        Assert.True(any, "a lit glyph pixel should be the exact requested color");
    }

    [Fact]
    public void DrawText_Lowercase_Folds_To_Uppercase_Cell()
    {
        int w = 24, h = 12;
        var a = NewBuf(w, h);
        var b = NewBuf(w, h);
        ScreenSpacePost.DrawText(a, w, h, 2, 2, "ABC", Ink);
        ScreenSpacePost.DrawText(b, w, h, 2, 2, "abc", Ink);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DrawText_Unknown_Glyph_Is_Blank_But_Still_Advances()
    {
        int w = 24, h = 12;
        var buf = NewBuf(w, h);
        // '~' is not in the font — nothing drawn, but the cursor still advances.
        int end = ScreenSpacePost.DrawText(buf, w, h, 2, 2, "~", Ink);
        Assert.Equal(8, end);
        foreach (var p in buf) Assert.Equal(Bg, p); // untouched
    }

    // ── Telemetry panel (HUD bit 0x200) ───────────────────────────────────

    private static LightingFxData TelemetryFx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.DebugHudFlags = 0x200;
        return fx;
    }

    [Fact]
    public void Telemetry_Panel_Draws_When_Enabled()
    {
        int w = 256, h = 256;
        var off = NewBuf(w, h);
        var on = NewBuf(w, h);
        var fxOff = LightingFxData.CreateDefault(); // no flags
        var fxOn = TelemetryFx();

        ScreenSpacePost.HudFrameMs = 0;      // FPS row hidden
        ScreenSpacePost.HudSupersample = 0;  // SS row hidden

        ScreenSpacePost.ApplyDebugHud(off, w, h, in fxOff);
        ScreenSpacePost.ApplyDebugHud(on, w, h, in fxOn);

        Assert.Equal(off, NewBuf(w, h));           // disabled = untouched
        Assert.NotEqual(on, off);                  // enabled = drew something
    }

    [Fact]
    public void Telemetry_Fps_Row_Appears_Only_When_Frame_Time_Stamped()
    {
        int w = 256, h = 256;

        ScreenSpacePost.HudSupersample = 0;
        var fx = TelemetryFx();

        ScreenSpacePost.HudFrameMs = 0;
        var noFps = NewBuf(w, h);
        ScreenSpacePost.ApplyDebugHud(noFps, w, h, in fx);
        int litNoFps = CountInk(noFps);

        ScreenSpacePost.HudFrameMs = 16.7; // ~60 FPS
        var withFps = NewBuf(w, h);
        ScreenSpacePost.ApplyDebugHud(withFps, w, h, in fx);
        int litWithFps = CountInk(withFps);

        Assert.True(litWithFps > litNoFps,
            $"the FPS row should add lit pixels (no-fps={litNoFps}, fps={litWithFps})");

        ScreenSpacePost.HudFrameMs = 0; // reset shared static for other tests
    }

    [Fact]
    public void Telemetry_Skips_Small_Frames_Like_Other_Widgets()
    {
        int w = 64, h = 64; // < 128 → HUD self-skips
        var buf = NewBuf(w, h);
        var fx = TelemetryFx();
        ScreenSpacePost.HudFrameMs = 16.7;
        ScreenSpacePost.ApplyDebugHud(buf, w, h, in fx);
        Assert.Equal(buf, NewBuf(w, h)); // untouched
        ScreenSpacePost.HudFrameMs = 0;
    }

    private static int CountInk(uint[] buf)
    {
        int n = 0;
        foreach (var p in buf) if (p == Ink) n++;
        return n;
    }
}
