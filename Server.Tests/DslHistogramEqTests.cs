// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #845 (slice 3 of #144) — histogram equalization for the escape-time DSL
// calculators (UserEquationCalculator, SandboxCalculator). These reuse the
// shared CDF (HistogramEqualizer.BuildCdf) but recolor through
// DslHistogramEqualizer, which touches ONLY plain smooth-escaped pixels
// (IterationBuffer < MaxIterations) and leaves in-set / #544 converged / #615
// OOB-surround / interior-orbit pixels exactly as Calculate wrote them. HE is
// inert under orbit-trap themes. These tests lock that contract through the
// public ISupportsHistogramEq surface, classifying pixels via the public
// IterationBuffer:
//
//   • full-strength HE recolors escaped pixels but never the non-escaped ones;
//   • strength 0 is the plain linear mapping — an idempotent recolor that
//     restores after a strong pass, and never disturbs non-escaped pixels;
//   • under an orbit-trap theme HE is inert (BuildHistogramCdf → false, apply
//     is a byte-identical no-op).

using System.Linq;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslHistogramEqTests
{
    private const int W = 96, H = 72;

    public enum Kind { UserEquation, Sandbox }

    private static (IFractalCalculator calc, ISupportsHistogramEq he, int[] iters) Make(
        Kind kind, IColorMap map)
    {
        IFractalCalculator c;
        int[] iterView;
        if (kind == Kind.UserEquation)
        {
            var u = new UserEquationCalculator(W, H)
            {
                CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
                ColorMap = map,
                FractalParameters = new FractalParameters
                {
                    UserEquationActiveTab = 1,
                    UserEquationDslSource = "z^2 + c",
                    UserCodeOrigin = UserCodeOrigin.Interactive,
                },
            };
            u.Calculate(default);
            Assert.True(u.IsCompiled, u.LastError);
            c = u; iterView = u.IterationBuffer;
        }
        else
        {
            var s = new SandboxCalculator(W, H)
            {
                CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
                ColorMap = map,
                FractalParameters = new FractalParameters { SandboxSource = "z^2 + c" },
            };
            s.Calculate(default);
            Assert.True(s.IsCompiled, s.LastError);
            c = s; iterView = s.IterationBuffer;
        }
        return (c, (ISupportsHistogramEq)c, iterView);
    }

    private static bool Escaped(int iter, int maxIter) => iter < maxIter;

    // ── full-strength HE recolors escaped pixels, leaves the rest alone ─────────
    [Theory]
    [InlineData(Kind.UserEquation)]
    [InlineData(Kind.Sandbox)]
    public void Full_Strength_Changes_Escaped_Not_Interior(Kind kind)
    {
        var (calc, he, iters) = Make(kind, new MonoBandMap());
        var baseline = (uint[])calc.ColorBuffer.Clone();

        he.ApplyHistogramEqualization(1.0);

        int escaped = 0, nonEscaped = 0, changed = 0;
        for (int i = 0; i < baseline.Length; i++)
        {
            if (Escaped(iters[i], calc.MaxIterations))
            {
                escaped++;
                if (calc.ColorBuffer[i] != baseline[i]) changed++;
            }
            else
            {
                nonEscaped++;
                Assert.Equal(baseline[i], calc.ColorBuffer[i]);   // never touched
            }
        }

        Assert.True(escaped > 0, "frame should contain escaped pixels");
        Assert.True(nonEscaped > 0, "frame should contain in-set pixels");
        Assert.True(changed > 0, "full-strength HE should recolor some escaped pixels");
    }

    // ── strength 0 is the plain linear map: idempotent + interior untouched ─────
    [Theory]
    [InlineData(Kind.UserEquation)]
    [InlineData(Kind.Sandbox)]
    public void Strength0_Is_Idempotent_Linear_Map(Kind kind)
    {
        var (calc, he, iters) = Make(kind, new MonoBandMap());
        var baseline = (uint[])calc.ColorBuffer.Clone();

        // Perturb with a strong pass, then return to 0 — the linear map must be
        // restored, and re-applying 0 must be a fixed point.
        he.ApplyHistogramEqualization(0.9);
        he.ApplyHistogramEqualization(0.0);
        var r0 = (uint[])calc.ColorBuffer.Clone();
        he.ApplyHistogramEqualization(0.0);
        var r0b = (uint[])calc.ColorBuffer.Clone();

        Assert.Equal(r0, r0b);   // strength 0 is a fixed point

        // Non-escaped pixels are identical to the original render throughout.
        for (int i = 0; i < baseline.Length; i++)
            if (!Escaped(iters[i], calc.MaxIterations))
                Assert.Equal(baseline[i], r0[i]);
    }

    // ── orbit-trap theme: HE is inert (no CDF, byte-identical apply) ────────────
    [Theory]
    [InlineData(Kind.UserEquation)]
    [InlineData(Kind.Sandbox)]
    public void OrbitTrap_Theme_Is_Inert(Kind kind)
    {
        var (calc, he, _) = Make(kind, new OrbitTrapPointMap());
        var baseline = (uint[])calc.ColorBuffer.Clone();

        Assert.False(he.BuildHistogramCdf(out _, out _, out _));

        he.ApplyHistogramEqualization(1.0);
        Assert.Equal(baseline, calc.ColorBuffer);   // untouched under orbit-trap
    }
}
