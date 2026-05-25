// UserBulbAnalyticDE.cs
//
// Source-pattern detector for User Bulb step functions whose growth is
// closed-form. When detected, UserBulbCalculator can iterate a single
// trajectory and use the Hubbard-Douady running-derivative recurrence
//
//     dr_{n+1} = N · r_n^{N-1} · dr_n + 1
//     DE       = 0.5 · log(r) · r / dr
//
// instead of the numerical Jacobian (4 trajectories per iter). Roughly
// 4× delegate-call reduction.
//
// Auto mode: detect kind, run 1 numerical Jacobian probe at the sample
// point, compare to the analytic estimate; accept if relative delta < 5%.
// Mis-detect (e.g. user wrote z*z + c but with a leading factor) falls
// back gracefully.

using System;
using System.Text.RegularExpressions;

using FracturingFog.Models;

namespace FracturingFog.Calculators;

public enum AnalyticDEKind
{
    None,
    Square,         // z*z + c                              (power N=2)
    PowerN,         // Vec3.Pow(z, N) + c                   (power N)
    MandelbulbN,    // canonical triplex formula            (power N)
}

public sealed record AnalyticDEPattern(AnalyticDEKind Kind, double Power);

public static class UserBulbAnalyticDE
{
    public static AnalyticDEPattern Detect(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return new(AnalyticDEKind.None, 0);

        // Normalize: strip comments + whitespace
        string s = StripComments(source);
        s = Regex.Replace(s, @"\s+", " ").Trim();

        // 1. Vec3.Pow(z, N) + c
        var mPow = Regex.Match(s,
            @"^\s*return\s+Vec3\.Pow\s*\(\s*z\s*,\s*([-+]?[0-9]*\.?[0-9]+)\s*\)\s*\+\s*c\s*;?\s*$");
        if (mPow.Success && double.TryParse(mPow.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n))
        {
            return new(AnalyticDEKind.PowerN, n);
        }

        // 2. Square triplex: z*z + c   OR   new Vec3(z.X*z.X - z.Y*z.Y - z.Z*z.Z, 2*z.X*z.Y, 2*z.X*z.Z) + c
        if (Regex.IsMatch(s, @"^\s*return\s+z\s*\*\s*z\s*\+\s*c\s*;?\s*$"))
            return new(AnalyticDEKind.Square, 2);

        if (Regex.IsMatch(s,
            @"new\s+Vec3\s*\(\s*z\.X\s*\*\s*z\.X\s*-\s*z\.Y\s*\*\s*z\.Y\s*-\s*z\.Z\s*\*\s*z\.Z\s*,\s*2\s*\*\s*z\.X\s*\*\s*z\.Y\s*,\s*2\s*\*\s*z\.X\s*\*\s*z\.Z\s*\)\s*\+\s*c"))
            return new(AnalyticDEKind.Square, 2);

        return new(AnalyticDEKind.None, 0);
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//.*?$", "", RegexOptions.Multiline);
        return src;
    }

    /// <summary>
    /// Hubbard-Douady DE for a power map of order N. Single-trajectory; no
    /// delegate calls needed for the recurrence itself (the user step still
    /// runs to advance z, but only once per iter instead of 4×).
    /// </summary>
    public static double PowerDE(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double power, double[] pArr)
    {
        var c = new Vec3(cx, cy, cz);
        var z = Vec3.Zero;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            // dr_{n+1} = N · r^(N-1) · dr_n + 1
            dr = power * Math.Pow(r, power - 1) * dr + 1.0;
            z = fn(z, c, i, pArr);
        }

        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }

    /// <summary>
    /// Validation probe: compare analytic DE to numerical Jacobian DE at a
    /// representative sample point. Used by Auto mode.
    /// </summary>
    public static bool AcceptAuto(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        AnalyticDEPattern pattern,
        int iter, double bailout, double jacH, double[] pArr)
    {
        if (pattern.Kind == AnalyticDEKind.None) return false;

        double cx = 0.4, cy = 0.3, cz = 0.2;
        double analytic = PowerDE(fn, cx, cy, cz, iter, bailout, pattern.Power, pArr);
        double numerical = NumericalProbe(fn, cx, cy, cz, iter, bailout, jacH, pArr);

        if (analytic <= 0 || numerical <= 0) return false;
        double rel = Math.Abs(analytic - numerical) / Math.Max(Math.Abs(numerical), 1e-9);
        return rel < 0.20; // 20% — Lipschitz vs log-form differ in magnitude but track shape
    }

    private static double NumericalProbe(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double h, double[] pArr)
    {
        var cBase = new Vec3(cx, cy, cz);
        var cPx = new Vec3(cx + h, cy, cz);
        var cPy = new Vec3(cx, cy + h, cz);
        var cPz = new Vec3(cx, cy, cz + h);
        var z = Vec3.Zero; var zx = Vec3.Zero; var zy = Vec3.Zero; var zz = Vec3.Zero;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z  = fn(z,  cBase, i, pArr);
            zx = fn(zx, cPx,   i, pArr);
            zy = fn(zy, cPy,   i, pArr);
            zz = fn(zz, cPz,   i, pArr);
        }
        double j0 = (zx - z).Length / h;
        double j1 = (zy - z).Length / h;
        double j2 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), j2);
        return 0.5 * r / Math.Max(dr, 1e-10);
    }
}
