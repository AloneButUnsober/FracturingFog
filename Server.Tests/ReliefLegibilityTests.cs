// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.9 (PaletteBuilder-Design.md, #392) — relief = luminance is form.
// A luminance-monotonic ramp with enough range reads as raised 3D relief; a ramp
// whose lightness reverses or barely moves flattens it. LockLuminance repairs it,
// keeping hue + chroma. Deterministic → the colour parity twin.

using System;
using System.Collections.Generic;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefLegibilityTests
{
    private static (byte r, byte g, byte b) Unpack(uint p) =>
        ((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF));

    private static float L(byte r, byte g, byte b) => PerceptualRamp.RgbToOkLab(r, g, b).L;
    private static float Hue(byte r, byte g, byte b)
    {
        var (LL, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
        return PerceptualRamp.OkLabToOklch(LL, a, bb).H;
    }

    private static List<(byte, byte, byte)> Unpack(uint[] ramp)
    {
        var outp = new List<(byte, byte, byte)>();
        foreach (var p in ramp) { var c = Unpack(p); outp.Add((c.r, c.g, c.b)); }
        return outp;
    }

    [Fact]
    public void Monotonic_Ramp_Reads_As_Relief()
    {
        // Dark → light sweep (viridis-like lightness climb).
        var mono = new List<(byte, byte, byte)>
        {
            ((byte)30, (byte)10, (byte)60), ((byte)60, (byte)90, (byte)140),
            ((byte)90, (byte)170, (byte)150), ((byte)240, (byte)240, (byte)90),
        };
        var rep = ReliefLegibility.Analyze(mono);
        Assert.True(rep.ReadsAsRelief);
        Assert.False(rep.Flat);
        Assert.False(rep.NonMonotonic);
        Assert.Equal(0, rep.Reversals);
        Assert.True(rep.LuminanceSpread > 0.3f);
    }

    [Fact]
    public void Lightness_Reversal_Flattens_Relief()
    {
        // Bright → dark → bright: a rising slope reads as up-then-down.
        var reversal = new List<(byte, byte, byte)>
        {
            ((byte)230, (byte)230, (byte)230), ((byte)20, (byte)20, (byte)20), ((byte)235, (byte)235, (byte)235),
        };
        var rep = ReliefLegibility.Analyze(reversal);
        Assert.False(rep.ReadsAsRelief);
        Assert.True(rep.NonMonotonic);
        Assert.True(rep.Reversals >= 1);
    }

    [Fact]
    public void Low_Contrast_Ramp_Is_Flat()
    {
        // Three colours at nearly the same lightness → no shading gradient.
        var flat = new List<(byte, byte, byte)>
        {
            ((byte)120, (byte)118, (byte)122), ((byte)118, (byte)122, (byte)120), ((byte)122, (byte)120, (byte)118),
        };
        var rep = ReliefLegibility.Analyze(flat);
        Assert.True(rep.Flat);
        Assert.False(rep.ReadsAsRelief);
        Assert.True(rep.LuminanceSpread < 0.2f);
    }

    [Fact]
    public void LockLuminance_Makes_A_Reversed_Ramp_Read_As_Relief()
    {
        var reversal = new List<(byte, byte, byte)>
        {
            ((byte)235, (byte)235, (byte)235), ((byte)20, (byte)20, (byte)20), ((byte)235, (byte)235, (byte)235),
        };
        Assert.True(ReliefLegibility.Analyze(reversal).NonMonotonic);

        var locked = ReliefLegibility.LockLuminance(reversal, 12);
        var rep = ReliefLegibility.Analyze(Unpack(locked));
        Assert.True(rep.ReadsAsRelief, $"locked ramp should read as relief (reversals {rep.Reversals}, spread {rep.LuminanceSpread})");
        Assert.Equal(0, rep.Reversals);
    }

    [Fact]
    public void LockLuminance_Preserves_Hue_While_Locking_Lightness()
    {
        // Reddish stops with a lightness reversal (mid → dark → light red).
        var redReversal = new List<(byte, byte, byte)>
        {
            ((byte)200, (byte)70, (byte)70), ((byte)60, (byte)15, (byte)15), ((byte)245, (byte)150, (byte)150),
        };
        float srcHue = Hue(200, 70, 70);

        var locked = ReliefLegibility.LockLuminance(redReversal, 9);
        var stops = Unpack(locked);

        // Lightness now ascends monotonically.
        float prev = -1f;
        foreach (var (r, g, b) in stops)
        {
            float l = L(r, g, b);
            Assert.True(l >= prev - 0.01f, $"locked lightness not monotonic: {l} < {prev}");
            prev = l;
        }
        // Hue stays reddish across the ramp (chroma-bearing stops), within a wide band.
        foreach (var (r, g, b) in stops)
        {
            if (PerceptualRamp.OkLabToOklch(PerceptualRamp.RgbToOkLab(r, g, b).L,
                    PerceptualRamp.RgbToOkLab(r, g, b).a, PerceptualRamp.RgbToOkLab(r, g, b).b).C < 0.02f)
                continue;   // near-neutral stop at the dark/bright extreme — hue undefined
            float dh = MathF.Abs(((Hue(r, g, b) - srcHue) % 360f + 360f) % 360f);
            if (dh > 180f) dh = 360f - dh;
            Assert.True(dh < 45f, $"hue drifted {dh} from red");
        }
    }

    [Fact]
    public void LockLuminance_Respects_Descending_Direction()
    {
        // Overall light → dark ramp with a reversal in the middle.
        var lightToDark = new List<(byte, byte, byte)>
        {
            ((byte)240, (byte)240, (byte)240), ((byte)200, (byte)200, (byte)200), ((byte)250, (byte)250, (byte)250), ((byte)25, (byte)25, (byte)25),
        };
        var locked = Unpack(ReliefLegibility.LockLuminance(lightToDark, 10));
        // Monotonic (no reversals) and descending overall.
        var rep = ReliefLegibility.Analyze(locked);
        Assert.Equal(0, rep.Reversals);
        Assert.True(L(locked[0].Item1, locked[0].Item2, locked[0].Item3) >
                    L(locked[^1].Item1, locked[^1].Item2, locked[^1].Item3),
            "descending source should stay light → dark");
    }
}
