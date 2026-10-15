// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.FFMath;

namespace FracturingFog.Rendering
{
    /// <summary>
    /// #1270 (SM-9) — the full multi-limb view centre of a
    /// <see cref="MandelbrotCalculator"/> (the double head plus the DD / QD / OD
    /// tail limbs). Captured before a sub-pixel jitter and restored after it, and
    /// used as the TAA fingerprint so a pan that only moves the tail limbs (any
    /// pan past ~1e10 zoom) still invalidates the accumulated samples.
    /// </summary>
    public readonly record struct CentreLimbs(
        double X0, double X1, double X2, double X3, double X4, double X5, double X6, double X7,
        double Y0, double Y1, double Y2, double Y3, double Y4, double Y5, double Y6, double Y7)
    {
        public static CentreLimbs Capture(MandelbrotCalculator c) => new(
            c.CenterX, c.CenterXLo, c.CenterX2, c.CenterX3, c.CenterX4, c.CenterX5, c.CenterX6, c.CenterX7,
            c.CenterY, c.CenterYLo, c.CenterY2, c.CenterY3, c.CenterY4, c.CenterY5, c.CenterY6, c.CenterY7);

        public void Restore(MandelbrotCalculator c)
        {
            c.CenterX = X0; c.CenterXLo = X1; c.CenterX2 = X2; c.CenterX3 = X3;
            c.CenterX4 = X4; c.CenterX5 = X5; c.CenterX6 = X6; c.CenterX7 = X7;
            c.CenterY = Y0; c.CenterYLo = Y1; c.CenterY2 = Y2; c.CenterY3 = Y3;
            c.CenterY4 = Y4; c.CenterY5 = Y5; c.CenterY6 = Y6; c.CenterY7 = Y7;
        }

        public OD X => new(X0, X1, X2, X3, X4, X5, X6, X7);
        public OD Y => new(Y0, Y1, Y2, Y3, Y4, Y5, Y6, Y7);
    }

    /// <summary>
    /// #1270 (SM-9) — sub-pixel anti-aliasing jitter that survives deep zoom.
    /// The MSAA / TAA passes used to add <c>j·scale</c> to the double
    /// <see cref="MandelbrotCalculator.CenterX"/> only; past ~1e10 that offset is
    /// below the double's precision and rounded away, so every jittered sample
    /// was the centre sample again. Here the offset goes through the OD centre
    /// (<see cref="OD.FromCenterOffset"/>, which places it at its true magnitude
    /// across all eight limbs), and the result is written back limb by limb, so
    /// the DD / QD / OD paths — and the reference orbit, rebuilt at the jittered
    /// centre — see the shift at any zoom OD can address.
    /// </summary>
    public static class CentreJitter
    {
        /// <summary>Shift the calculator's centre by (jx, jy) pixels of size
        /// <paramref name="scale"/>, carrying the offset through every limb.
        /// The caller restores the centre with <see cref="CentreLimbs.Restore"/>.</summary>
        public static void Apply(MandelbrotCalculator calc, in CentreLimbs orig, double jx, double jy, double scale)
        {
            OD x = OD.FromCenterOffset(orig.X, jx, scale);
            OD y = OD.FromCenterOffset(orig.Y, jy, scale);
            calc.CenterX = x.X0; calc.CenterXLo = x.X1; calc.CenterX2 = x.X2; calc.CenterX3 = x.X3;
            calc.CenterX4 = x.X4; calc.CenterX5 = x.X5; calc.CenterX6 = x.X6; calc.CenterX7 = x.X7;
            calc.CenterY = y.X0; calc.CenterYLo = y.X1; calc.CenterY2 = y.X2; calc.CenterY3 = y.X3;
            calc.CenterY4 = y.X4; calc.CenterY5 = y.X5; calc.CenterY6 = y.X6; calc.CenterY7 = y.X7;
        }

        /// <summary>True when a jitter of about one pixel still changes the
        /// centre: the pixel is above the OD centre's precision floor (~2⁻³⁷¹
        /// relative, eight 53-bit limbs, with headroom). Past it every jittered
        /// sample is the centre sample again, so TAA continuations are skipped
        /// (the SM-8c guard, moved from the double floor to the OD floor).</summary>
        public static bool IsResolvable(MandelbrotCalculator calc, double scale)
        {
            double mag = System.Math.Max(System.Math.Abs(calc.CenterX), System.Math.Abs(calc.CenterY));
            return scale > mag * 1e-105;
        }
    }
}
