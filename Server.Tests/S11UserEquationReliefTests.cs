// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S11 add-on (#726 slice 1) — Relief 3D for User Equation / DSL fractals.
// UserEquationCalculator (the interpreted DSL path) was the last mainstream 2D
// family with no relief path: it computed a smooth value per pixel but discarded
// it and did not implement IHeightFieldSource, so FractalRenderHost's display-res
// relief fallback (calc as IHeightFieldSource) resolved null and skipped relief.
// Slice 1 persists the smooth field into SmoothBuffer + implements the interface;
// no host change is needed (the fallback picks it up generically). These lock:
// (a) the calc is now a height-field source with real structure, (b) that field
// extrudes a surface under the raymarch, (c) the smooth field matches the escape
// contract (in-set = 0) and is deterministic.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S11UserEquationReliefTests
{
    private const int W = 160, H = 120;

    private static UserEquationCalculator Render(string source = "z*z + c")
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            ColorMap = new MonoBandMap(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = source,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    // (a) The DSL calc now exposes a real height field — the gap #726 closes.
    [Fact]
    public void Exposes_Structured_HeightField_Source()
    {
        var calc = Render();
        Assert.IsAssignableFrom<IHeightFieldSource>(calc);

        var sb = calc.SmoothBuffer;
        Assert.Equal(W * H, sb.Length);
        Assert.True(sb.Max() > 1f, "smooth height never rises (no escaped structure)");

        // A real terrain has many distinct heights (a smooth gradient), not a step.
        int distinct = sb.Where(h => h > 0f).Select(h => (int)(h * 4)).Distinct().Count();
        Assert.True(distinct > 8, $"height field too flat (distinct={distinct})");

        // The big in-set lobe around c=-0.5 must read 0 (no relief), and escaped
        // pixels must read > 0 — the IHeightFieldSource contract.
        Assert.Contains(sb, h => h == 0f);
        Assert.Contains(sb, h => h > 0f);
    }

    // (b) That field extrudes a surface: the raymarch reports a non-zero hit
    // fraction (the "relief renders" acceptance in #726).
    [Fact]
    public void HeightField_Raymarch_Produces_Surface()
    {
        var calc = Render();
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
        };
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render((uint[])calc.ColorBuffer.Clone(),
            (float[])calc.SmoothBuffer.Clone(), W, H, p, dst, out double hitFraction);

        Assert.True(hitFraction > 0.0, "raymarch found no surface — relief did not extrude");
    }

    // #726 slice 2 — hi-res field twin. CreateReliefFieldCalc now builds a fresh
    // UserEquationCalculator; parameterised the way SyncAltStateFromMandel does (view
    // + source via FractalParameters), it recompiles the same DSL equation and yields
    // a non-degenerate smooth field at the hi-res floor — so small windows get sharper
    // DSL terrain, not the display-res field.
    [Fact]
    public void HiRes_Twin_Produces_NonDegenerate_Field()
    {
        Assert.True(FractalRenderHost.SupportsHiResReliefField(FractalType.UserEquation));

        var twin = FractalRenderHost.CreateReliefFieldCalc(FractalType.UserEquation, 96, 72);
        var u = Assert.IsType<UserEquationCalculator>(twin);
        u.CenterX = -0.5; u.CenterY = 0.0; u.Zoom = 1.0; u.MaxIterations = 200;
        u.ColorMap = new MonoBandMap();
        u.FractalParameters = new FractalParameters
        {
            UserEquationSource = "z*z + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        };
        u.Calculate(default);

        var field = ((IHeightFieldSource)u).SmoothBuffer;
        Assert.Equal(96 * 72, field.Length);
        int exterior = 0, interior = 0;
        foreach (float v in field) { if (v > 0f) exterior++; else interior++; }
        Assert.True(exterior > 0, "hi-res twin field has no raised (escaped) pixels");
        Assert.True(interior > 0, "hi-res twin field has no base plane (view missed the set)");
    }

    // #726 (orbit-trap slice) — with an orbit-trap theme the DSL calc fills a
    // TrapBuffer (ITrapFieldSource), and the Trap height source builds a DIFFERENT,
    // non-degenerate relief field from it (literal 3D orbit-trap topography), not the
    // smooth count.
    [Fact]
    public void OrbitTrap_Theme_Fills_TrapBuffer_And_Drives_Trap_Relief()
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            ColorMap = new OrbitTrapPointMap(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "z*z + c",
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);

        Assert.IsAssignableFrom<ITrapFieldSource>(calc);
        var trap = calc.TrapBuffer;
        Assert.Equal(W * H, trap.Length);
        Assert.Contains(trap, t => t > 0f);   // orbit-trap distances captured

        int n = W * H;
        var trapField = ReliefHeightField.Build(calc.SmoothBuffer, trap, n, ReliefHeightSource.Trap, 0.0);
        // Trap source must NOT be the smooth field (it is a distinct topography) and
        // must be non-degenerate (some raised ridges).
        Assert.NotSame(calc.SmoothBuffer, trapField);
        Assert.NotEqual(calc.SmoothBuffer, trapField);
        Assert.Contains(trapField, h => h > 0f);
    }

    // A non-orbit theme leaves TrapBuffer all-zero, so the Trap source falls back to
    // the smooth field (same reference) — byte-identical, no spurious relief.
    [Fact]
    public void NonOrbit_Theme_Leaves_TrapBuffer_Empty_TrapFallsBackToSmooth()
    {
        var calc = Render();   // MonoBandMap — not orbit-aware
        Assert.All(calc.TrapBuffer, t => Assert.Equal(0f, t));
        var built = ReliefHeightField.Build(calc.SmoothBuffer, calc.TrapBuffer,
            W * H, ReliefHeightSource.Trap, 0.0);
        Assert.Same(calc.SmoothBuffer, built);   // empty trap → smooth reference
    }

    // (c) Contract + determinism: the smooth field is stable run-to-run, and
    // reading it never perturbs the flat colour render (byte-identical gate — the
    // buffer is populated alongside, not instead of, the colour path).
    [Fact]
    public void SmoothField_Is_Deterministic_And_Color_Stable()
    {
        var a = Render();
        var b = Render();
        Assert.Equal(a.SmoothBuffer, b.SmoothBuffer);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);   // colour path unchanged by the field
    }
}
