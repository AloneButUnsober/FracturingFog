// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorGenRuntime.cs
//
// #27 Phase 4 — compiled runtime for the ColorGen DSL *interpreter*
// (CgInterpreter / InterpretedColorMap). These are the exact helpers the
// generated-C# theme path inlines from ColorMap.template.cs (there they are the
// private nested `Cg3` struct + `CgScalar` static). Ported here VERBATIM —
// renamed `CgRgb` / `CgMath` to avoid clashing with the template's own copy —
// so the interpreter produces bit-identical colours to the compiled path. The
// parity harness (ColorGenInterpreterParityTests) pins the two together.
//
// DO NOT let this drift from ColorMap.template.cs: any change to a colour
// formula must be mirrored in both, and the parity test will catch a mismatch.

using System;

namespace FracturingFog.Models;

/// <summary>RGB triple in [0,1] working space — the Vec3 value of the ColorGen
/// DSL. Mirrors the generated template's private <c>Cg3</c> record struct.</summary>
public readonly record struct CgRgb(double R, double G, double B)
{
    // ── Elementwise + - * / on CgRgb↔CgRgb (CgRgb↔Scalar variants follow). ──
    public static CgRgb Add(CgRgb a, CgRgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    public static CgRgb AddVS(CgRgb a, double s) => new(a.R + s, a.G + s, a.B + s);
    public static CgRgb AddSV(double s, CgRgb a) => new(s + a.R, s + a.G, s + a.B);
    public static CgRgb Sub(CgRgb a, CgRgb b) => new(a.R - b.R, a.G - b.G, a.B - b.B);
    public static CgRgb SubVS(CgRgb a, double s) => new(a.R - s, a.G - s, a.B - s);
    public static CgRgb SubSV(double s, CgRgb a) => new(s - a.R, s - a.G, s - a.B);
    public static CgRgb Mul(CgRgb a, CgRgb b) => new(a.R * b.R, a.G * b.G, a.B * b.B);
    public static CgRgb MulVS(CgRgb a, double s) => new(a.R * s, a.G * s, a.B * s);
    public static CgRgb MulSV(double s, CgRgb a) => new(s * a.R, s * a.G, s * a.B);
    public static CgRgb Div(CgRgb a, CgRgb b) => new(a.R / b.R, a.G / b.G, a.B / b.B);
    public static CgRgb DivVS(CgRgb a, double s) => new(a.R / s, a.G / s, a.B / s);
    public static CgRgb DivSV(double s, CgRgb a) => new(s / a.R, s / a.G, s / a.B);
    public static CgRgb Neg(CgRgb a) => new(-a.R, -a.G, -a.B);

    public static CgRgb Mod(CgRgb a, CgRgb b)
        => new(CgMath.Mod(a.R, b.R), CgMath.Mod(a.G, b.G), CgMath.Mod(a.B, b.B));
    public static CgRgb ModVS(CgRgb a, double s)
        => new(CgMath.Mod(a.R, s), CgMath.Mod(a.G, s), CgMath.Mod(a.B, s));
    public static CgRgb ModSV(double s, CgRgb a)
        => new(CgMath.Mod(s, a.R), CgMath.Mod(s, a.G), CgMath.Mod(s, a.B));

    public static CgRgb Pow(CgRgb a, CgRgb b)
        => new(Math.Pow(a.R, b.R), Math.Pow(a.G, b.G), Math.Pow(a.B, b.B));
    public static CgRgb PowVS(CgRgb a, double s)
        => new(Math.Pow(a.R, s), Math.Pow(a.G, s), Math.Pow(a.B, s));
    public static CgRgb PowSV(double s, CgRgb a)
        => new(Math.Pow(s, a.R), Math.Pow(s, a.G), Math.Pow(s, a.B));

    public static CgRgb Mix(CgRgb a, CgRgb b, double t)
        => new(a.R + (b.R - a.R) * t,
               a.G + (b.G - a.G) * t,
               a.B + (b.B - a.B) * t);

    public static CgRgb FromHsv(double h, double s, double v)
    {
        // Cyclic hue in [0,1). HSV→RGB with the standard 6-segment lookup.
        h = h - Math.Floor(h);
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        double hh = h * 6.0;
        int i = (int)Math.Floor(hh);
        double f = hh - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - f * s);
        double tt = v * (1.0 - (1.0 - f) * s);
        return (i % 6) switch
        {
            0 => new CgRgb(v, tt, p),
            1 => new CgRgb(q, v, p),
            2 => new CgRgb(p, v, tt),
            3 => new CgRgb(p, q, v),
            4 => new CgRgb(tt, p, v),
            _ => new CgRgb(v, p, q),
        };
    }

    public static CgRgb FromHsl(double h, double s, double l)
    {
        h = h - Math.Floor(h);
        s = Math.Clamp(s, 0.0, 1.0);
        l = Math.Clamp(l, 0.0, 1.0);
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hh = h * 6.0;
        double x = c * (1.0 - Math.Abs((hh % 2.0) - 1.0));
        double m = l - c * 0.5;
        double r1, g1, b1;
        int seg = (int)Math.Floor(hh);
        switch (seg % 6)
        {
            case 0: r1 = c; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = c; b1 = 0; break;
            case 2: r1 = 0; g1 = c; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0; b1 = c; break;
            default:r1 = c; g1 = 0; b1 = x; break;
        }
        return new CgRgb(r1 + m, g1 + m, b1 + m);
    }

    /// <summary>Cyclic linear palette: stops are evenly spaced on [0,1) with t
    /// wrapping (fract). With N stops the segment for t lands in
    /// [k/N, (k+1)/N) and lerps between stops[k] and stops[(k+1)%N].</summary>
    public static CgRgb Palette(double t, params CgRgb[] stops)
    {
        int n = stops.Length;
        if (n == 0) return new CgRgb(0, 0, 0);
        if (n == 1) return stops[0];
        double frac = t - Math.Floor(t);
        double pos = frac * n;
        int k = (int)Math.Floor(pos) % n;
        int k2 = (k + 1) % n;
        double f = pos - Math.Floor(pos);
        return Mix(stops[k], stops[k2], f);
    }

    /// <summary>Inigo Quilez cosine palette: a + b·cos(2π·(c·t + d)). a,b,c,d are
    /// per-channel coefficient vectors. Output unclamped (the packer clamps).</summary>
    public static CgRgb Cosine(double t, CgRgb a, CgRgb b, CgRgb c, CgRgb d)
    {
        const double tau = 6.283185307179586;
        return new CgRgb(
            a.R + b.R * Math.Cos(tau * (c.R * t + d.R)),
            a.G + b.G * Math.Cos(tau * (c.G * t + d.G)),
            a.B + b.B * Math.Cos(tau * (c.B * t + d.B)));
    }

    // ── OkLab (Björn Ottosson) — perceptually uniform colour (F9). ──────
    private static double SrgbToLinear(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }

    public static CgRgb FromOkLab(double L, double a, double b)
    {
        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;
        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        double r =  4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        return new CgRgb(LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(bb));
    }

    public static CgRgb FromOkLch(double L, double c, double h)
        => FromOkLab(L, c * Math.Cos(h), c * Math.Sin(h));

    private static CgRgb ToOkLab(CgRgb c)
    {
        double r = SrgbToLinear(c.R), g = SrgbToLinear(c.G), b = SrgbToLinear(c.B);
        double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
        double l_ = Math.Cbrt(l), m_ = Math.Cbrt(m), s_ = Math.Cbrt(s);
        return new CgRgb(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    public static CgRgb MixOkLab(CgRgb a, CgRgb b, double t)
    {
        CgRgb la = ToOkLab(a), lb = ToOkLab(b);
        return FromOkLab(la.R + (lb.R - la.R) * t,
                         la.G + (lb.G - la.G) * t,
                         la.B + (lb.B - la.B) * t);
    }

    public static CgRgb Brightness(CgRgb a, double s) => new(a.R + s, a.G + s, a.B + s);

    public static CgRgb Contrast(CgRgb a, double s)
    {
        // s in [-1,1]: -1 = grey, 0 = identity, 1 = double contrast.
        double f = 1.0 + s;
        return new(0.5 + (a.R - 0.5) * f,
                   0.5 + (a.G - 0.5) * f,
                   0.5 + (a.B - 0.5) * f);
    }

    public static CgRgb Gamma(CgRgb a, double g)
    {
        double e = g <= 0.0 ? 1.0 : 1.0 / g;
        return new(Math.Pow(Math.Max(0, a.R), e),
                   Math.Pow(Math.Max(0, a.G), e),
                   Math.Pow(Math.Max(0, a.B), e));
    }
}

/// <summary>Scalar helpers mirroring the generated template's <c>CgScalar</c>.</summary>
public static class CgMath
{
    public static double Fract(double x) => x - Math.Floor(x);

    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    public static double Mod(double x, double y)
    {
        // GLSL-style mod: x - y * floor(x/y). Differs from C# % on negatives.
        if (y == 0.0) return 0.0;
        return x - y * Math.Floor(x / y);
    }

    public static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = (x - edge0) / (edge1 - edge0);
        if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
        return t * t * (3.0 - 2.0 * t);
    }

    public static double Hash(double x)
        => Fract(Math.Sin(x * 12.9898) * 43758.5453);

    public static double Hash2(double x, double y)
        => Fract(Math.Sin(x * 12.9898 + y * 78.233) * 43758.5453);

    /// <summary>ARGB packer — matches the template's PackArgb exactly.</summary>
    public static int PackArgb(CgRgb c)
    {
        int r = (int)(Math.Clamp(c.R, 0.0, 1.0) * 255.0 + 0.5);
        int g = (int)(Math.Clamp(c.G, 0.0, 1.0) * 255.0 + 0.5);
        int b = (int)(Math.Clamp(c.B, 0.0, 1.0) * 255.0 + 0.5);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }
}
