// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScreenSpacePost.HudFont.cs
//
// #318 — numeric telemetry for the byte-buffer lighting HUD. The Phase-19
// HUD (ScreenSpacePost.ApplyDebugHud) is a font-free pixel pusher: compass,
// bars, gauge, balls, histogram — all primitives, no text. This partial adds
//
//   • a compact 5×7 bitmap font (digits + A–Z + a little punctuation),
//   • a DrawText primitive that stamps it into the packed-BGRA color buffer,
//   • a telemetry panel (HUD bit 0x200) with the readouts a lookdev artist
//     wants baked INTO the frame (survives PNG / video export, unlike the
//     screen-only Skia perf HUD): resolution, active-light count, fog optical
//     depth, supersample level, active AOV view, and — when the host has
//     stamped it — frame-time / FPS.
//
// The font lives here (not a shared asset) so saved scenes / exports stay
// self-contained and the HUD has zero external dependencies. 5×7 is the
// classic LCD cell: legible at 1× on a >=128px frame, and scalable by an
// integer factor for larger stills.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FracturingFog.Rendering.Lighting;

public static partial class ScreenSpacePost
{
    // ── Host-stamped transient telemetry ──────────────────────────────────
    // Frame-time and supersample level are host/global concerns (a rolling
    // average across the whole app, and the active AA factor), not per-scene
    // lighting parameters — so they ride a static channel the host updates
    // once per frame rather than bloating LightingFxData (which serializes)
    // or threading a struct through all ~25 ApplyDebugHud call sites. Both
    // self-hide at 0 / <=1, so a host that never stamps them simply omits the
    // FPS / SS rows.

    /// <summary>Last measured frame time in ms for the telemetry panel. 0 =
    /// unknown → the FPS row is hidden. Set by the render host each frame.</summary>
    public static double HudFrameMs;

    /// <summary>Active supersample / AA factor for the telemetry panel. 0 or 1
    /// = none → the SS row is hidden. Set by the render host each frame.</summary>
    public static int HudSupersample;

    // ── Telemetry panel (HUD bit 0x200) ───────────────────────────────────

    /// <summary>Numeric readout block (top-left, under the scene clock). Lines
    /// the lighting/lookdev artist wants baked into the frame: render resolution,
    /// active-light count, fog optical depth, supersample level, the active AOV
    /// view, and frame-time/FPS when the host has stamped <see cref="HudFrameMs"/>.
    /// Amber (#FFCC00) flags the fog row when fog is live; cyan flags a non-Beauty
    /// AOV so the reader knows the frame is a diagnostic view, not a beauty pass.</summary>
    private static void DrawTelemetry(uint[] buf, int w, int h, in LightingFxData fx)
    {
        int lights = (fx.Light1.Intensity > 0 ? 1 : 0)
                   + (fx.Light2.Intensity > 0 ? 1 : 0)
                   + (fx.Light3.Intensity > 0 ? 1 : 0);

        // (text, color) rows. Grey = neutral; amber = live fog; cyan = AOV view.
        var rows = new List<(string s, uint c)>(6)
        {
            ($"RES {w}X{h}", 0xFFDDDDDDu),
            ($"LIGHTS {lights}", 0xFFDDDDDDu),
        };

        if (fx.FogDensity > 0.0)
            rows.Add(($"FOG OD {F2(fx.FogDensity)}", 0xFFFFCC00u)); // amber = active
        else
            rows.Add(("FOG OFF", 0xFF888888u));

        int ss = HudSupersample;
        if (ss > 1) rows.Add(($"SS {ss}X", 0xFFDDDDDDu));

        double frameMs = HudFrameMs;
        if (frameMs > 0.0)
        {
            int fps = (int)Math.Round(1000.0 / frameMs);
            rows.Add(($"FPS {fps} ({F1(frameMs)}MS)", 0xFFDDDDDDu));
        }

        if (fx.DebugAov != AovView.Beauty)
            rows.Add(($"AOV {AovLabel(fx.DebugAov)}", 0xFF00CCFFu)); // cyan = diagnostic

        // Panel geometry. 1× glyphs: 5×7 cell + 1px advance = 6px per char,
        // rows 7px tall + 2px lead = 9px. Sit below the 40px scene clock.
        const int scale = 1, pad = 4, lineH = 9;
        int maxChars = 0;
        foreach (var (s, _) in rows) if (s.Length > maxChars) maxChars = s.Length;
        int panelW = pad * 2 + maxChars * 6 * scale;
        int panelH = pad * 2 + rows.Count * lineH * scale - (lineH - 7) * scale;
        int x0 = 8, y0 = 54;

        FillRectAlpha(buf, w, h, x0, y0, x0 + panelW, y0 + panelH, 0xFF000000u, 0.5);
        int tx = x0 + pad, ty = y0 + pad;
        foreach (var (s, c) in rows)
        {
            DrawText(buf, w, h, tx, ty, s, c, scale);
            ty += lineH * scale;
        }
    }

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string AovLabel(AovView v) => v switch
    {
        AovView.Normals          => "NORMALS",
        AovView.Depth            => "DEPTH",
        AovView.StepCount        => "STEPS",
        AovView.AmbientOcclusion => "AO",
        AovView.Diffuse          => "DIFFUSE",
        AovView.Specular         => "SPECULAR",
        AovView.Shadow           => "SHADOW",
        _                        => "BEAUTY",
    };

    // ── Bitmap text primitive ─────────────────────────────────────────────

    /// <summary>Stamp a string into the packed-BGRA buffer at (x,y) using the
    /// 5×7 bitmap font, one <paramref name="scale"/>×<paramref name="scale"/>
    /// block per lit glyph pixel. Monospaced: each glyph advances 6·scale px.
    /// Unknown glyphs render blank. Alpha-1 fill so drawn pixels are the exact
    /// requested color (tests can assert on them). Returns the end X.</summary>
    public static int DrawText(
        uint[] buf, int w, int h, int x, int y, string text, uint color, int scale = 1)
    {
        if (string.IsNullOrEmpty(text) || scale < 1) return x;
        int cx = x;
        foreach (char ch in text)
        {
            byte[] glyph = Glyph(ch);
            for (int row = 0; row < 7; row++)
            {
                int bits = glyph[row];
                for (int col = 0; col < 5; col++)
                {
                    if ((bits & (0b10000 >> col)) == 0) continue;
                    int px = cx + col * scale;
                    int py = y + row * scale;
                    FillRectAlpha(buf, w, h, px, py, px + scale, py + scale, color, 1.0);
                }
            }
            cx += 6 * scale; // 5 wide + 1 advance
        }
        return cx;
    }

    /// <summary>Pixel width a string will occupy at the given scale (monospaced).</summary>
    public static int MeasureText(string text, int scale = 1)
        => string.IsNullOrEmpty(text) ? 0 : text.Length * 6 * scale;

    private static byte[] Glyph(char ch)
    {
        // Fold lowercase to the uppercase cell (single-case font).
        if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
        return Font5x7.TryGetValue(ch, out var g) ? g : Blank;
    }

    private static readonly byte[] Blank = { 0, 0, 0, 0, 0, 0, 0 };

    // 5×7 bitmap font. Each glyph = 7 rows top→bottom; the low 5 bits of each
    // byte are the columns left→right (0b10000 = leftmost). Classic LCD cell.
    private static readonly Dictionary<char, byte[]> Font5x7 = new()
    {
        ['0'] = new byte[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
        ['1'] = new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['2'] = new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 },
        ['3'] = new byte[] { 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110 },
        ['4'] = new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
        ['5'] = new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 },
        ['6'] = new byte[] { 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 },
        ['7'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
        ['8'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
        ['9'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100 },

        ['A'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['B'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
        ['C'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 },
        ['D'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110 },
        ['E'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
        ['F'] = new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['G'] = new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111 },
        ['H'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
        ['I'] = new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
        ['J'] = new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100 },
        ['K'] = new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
        ['L'] = new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
        ['M'] = new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
        ['N'] = new byte[] { 0b10001, 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001 },
        ['O'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['P'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
        ['Q'] = new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
        ['R'] = new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
        ['S'] = new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
        ['T'] = new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['U'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
        ['V'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
        ['W'] = new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001 },
        ['X'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
        ['Y'] = new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
        ['Z'] = new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },

        [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
        ['.'] = new byte[] { 0, 0, 0, 0, 0, 0b00110, 0b00110 },
        [':'] = new byte[] { 0, 0b00110, 0b00110, 0, 0b00110, 0b00110, 0 },
        ['/'] = new byte[] { 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000 },
        ['-'] = new byte[] { 0, 0, 0, 0b11111, 0, 0, 0 },
        ['%'] = new byte[] { 0b11000, 0b11001, 0b00010, 0b00100, 0b01000, 0b10011, 0b00011 },
        ['('] = new byte[] { 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010 },
        [')'] = new byte[] { 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000 },
    };
}
