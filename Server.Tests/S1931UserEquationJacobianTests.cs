// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1931 — the User Equation numeric Jacobian (the path for non-holomorphic maps) steps c by a
// pixel-scaled h. Independent check: for conj(z)^2 + c the derivative with respect to Re(c) has
// an exact forward-mode recurrence, dz' = 2 conj(z) conj(dz) + 1 (dz0 = 0), run here on the same
// double orbit; the calculator's dz/dc (FinalDr / FinalDi) must match it on escaped pixels near
// the tricorn boundary at 1e3, 1e8 and 1e11 (the old absolute h = 1e-6 is shown to fail at 1e11),
// and shallow frames, where 1e-6 was already sub-pixel, are unchanged.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1931UserEquationJacobianTests
{
    private const int W = 48, H = 32, MaxIt = 1500;
    private const double Bail2 = 1024;   // the User Equation default bailout (radius 32)
    private static readonly double Cx = 1 + 0.37e-13, Cy = Math.Sqrt(3);   // on the tricorn boundary (S1909)

    private static UserEquationCalculator Render(double zoom, double cx = 1 + 0.37e-13, double cy = 1.7320508075688772)
    {
        var c = new UserEquationCalculator(W, H)
        {
            Zoom = zoom, MaxIterations = MaxIt, ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = false,
            CenterX = cx, CenterY = cy, FractalParameters = new FractalParameters { UserEquationSource = "conj(z)^2 + c" },
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    // The exact dz/dRe(c) of conj(z)^2 + c at c, with the escape test the calculator uses (|z|^2 >= 1024).
    private static (Complex Dz, bool Escaped) Exact(Complex c)
    {
        Complex z = Complex.Zero, dz = Complex.Zero;
        for (int i = 0; i < MaxIt; i++)
        {
            if (z.Real * z.Real + z.Imaginary * z.Imaginary >= Bail2) return (dz, true);
            var zc = Complex.Conjugate(z);
            dz = 2 * zc * Complex.Conjugate(dz) + 1;
            z = zc * zc + c;
        }
        return (dz, false);
    }

    // The finite difference with a given h on the same double orbit (the calculator's method).
    private static Complex Fd(Complex c, double h)
    {
        Complex z = Complex.Zero, zp = Complex.Zero, cp = new(c.Real + h, c.Imaginary);
        for (int i = 0; i < MaxIt; i++)
        {
            if (z.Real * z.Real + z.Imaginary * z.Imaginary >= Bail2) break;
            var a = Complex.Conjugate(z); var b = Complex.Conjugate(zp);
            z = a * a + c; zp = b * b + cp;
        }
        return (zp - z) / h;
    }

    private static Complex PixelC(int x, int y, double zoom, double cx, double cy)
    {
        double scale = 3.5 / Math.Max(W, H) / zoom;
        return new Complex(cx + (x - W * 0.5) * scale, cy + (y - H * 0.5) * scale);
    }

    // Tolerances: 1e-3 to 1e8, 5e-2 at 1e11, where a double c leaves the sub-pixel step only ~3 digits
    // (h = 1e-13 |c|): measured 1.6-2.8% median there, against NaN / 1e19 with the old 1e-6 step.
    [Theory]
    [InlineData(1e3, 1e-3)]
    [InlineData(1e8, 1e-3)]
    [InlineData(1e11, 5e-2)]
    public void Numeric_Dz_Dc_Matches_The_Exact_Derivative(double zoom, double tolerance)
    {
        var calc = Render(zoom);
        var errs = new System.Collections.Generic.List<double>();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (dz, escaped) = Exact(PixelC(x, y, zoom, Cx, Cy));
                int i = y * W + x;
                if (!escaped || calc.IterationBuffer[i] >= MaxIt) continue;
                var got = new Complex(calc.FinalDrBuffer[i], calc.FinalDiBuffer[i]);
                errs.Add((got - dz).Magnitude / Math.Max(1e-300, dz.Magnitude));
            }
        Assert.True(errs.Count > W * H / 10, $"precondition: escaped pixels ({errs.Count})");
        errs.Sort();
        double median = errs[errs.Count / 2];
        Assert.True(median < tolerance, $"zoom {zoom}: median relative error {median}");
    }

    [Fact]
    public void The_Old_Absolute_Step_Fails_At_1e11_And_The_New_One_Is_Sub_Pixel()
    {
        const double zoom = 1e11;
        double scale = 3.5 / Math.Max(W, H) / zoom;
        double h = UserEquationCalculator.JacobianStep(scale, Cx, Cy);
        Assert.True(h < scale, $"step {h} vs pixel {scale}");
        int bad = 0, n = 0;
        for (int y = 0; y < H; y += 3)
            for (int x = 0; x < W; x += 3)
            {
                var c = PixelC(x, y, zoom, Cx, Cy);
                var (dz, escaped) = Exact(c);
                if (!escaped) continue;
                n++;
                if (!((Fd(c, 1e-6) - dz).Magnitude <= 0.1 * dz.Magnitude)) bad++;   // NaN counts as wrong
            }
        Assert.True(n > 10);
        Assert.True(bad > n / 2, $"precondition: the absolute 1e-6 step should be wrong at 1e11 ({bad} of {n})");
    }

    [Fact]
    public void Shallow_Frames_Keep_The_Old_Step()
    {
        // At the default framing the pixel is ~0.07, so pitch / 1000 > 1e-6: the step stays 1e-6.
        Assert.Equal(1e-6, UserEquationCalculator.JacobianStep(3.5 / 960 / 0.875, -0.5, 0.0));
        Assert.Equal(1e-6, UserEquationCalculator.JacobianStep(1e-3, 0.3, 0.2));
        Assert.Equal(2e-13, UserEquationCalculator.JacobianStep(1e-20, 2, 0.5));
    }
}
