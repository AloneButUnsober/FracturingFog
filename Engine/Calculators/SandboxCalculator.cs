// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SandboxCalculator.cs
//
// Escape-time renderer driven by SandboxExpression — a restricted expression
// DSL that has no access to the BCL (no File.IO, no reflection, no P/Invoke).
// Mirrors UserEquationCalculator's pixel loop but swaps the Roslyn script
// delegate for an interpreter walking a parsed AST.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class SandboxCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Phase 11 — surface normals via numerical Jacobian. Same pattern as
    // UserEquationCalculator (parallel-perturbation trajectory + Hubbard-
    // Douady at escape). Empty/zero for in-set pixels; 3D Phong themes
    // pick them up via the five-parameter ColorMap.Map overload.
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    /// <summary>#96/#382 — global interior alpha (0..255). Scales the alpha of
    /// the in-set colour so the interior can composite over the chosen
    /// <c>Interior2DBackground</c>. 255 = opaque (bit-identical to before). Set
    /// from <c>FractalParameters.InteriorAlpha</c> by the render host / poster
    /// builder, mirroring the Mandelbrot canonical path.</summary>
    public int InteriorAlpha { get; set; } = 255;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Most recent compile error, or empty string when last compile succeeded.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True if last compile produced a usable expression.</summary>
    public bool IsCompiled => _compiled != null;

    private SandboxExpression? _compiled;
    private string _compiledSource = string.Empty;

    public SandboxCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
    }

    public void Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            _compiled = null;
            LastError = "Source is empty";
            return;
        }

        try
        {
            _compiled = SandboxExpression.Parse(source);
            _compiledSource = source;
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _compiled = null;
        }
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (_compiled == null)
        {
            if (!string.IsNullOrWhiteSpace(FractalParameters.SandboxSource)
                && FractalParameters.SandboxSource != _compiledSource)
            {
                Compile(FractalParameters.SandboxSource!);
            }
        }

        var expr = _compiled;
        if (expr == null)
        {
            Array.Clear(ColorBuffer);
            uint bg = ColorMap.InSetColor;
            for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;
            return;
        }

        ColorMap.MaxIterations = MaxIterations;
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        // #541 — escape radius from FractalParameters (0 = legacy |z|² = 1024).
        double bailout2 = FractalParameters.EscapeRadius > 0.0
            ? FractalParameters.EscapeRadius * FractalParameters.EscapeRadius
            : 1024.0;

        // P5: gate orbit sampling once. Non-orbit themes pay nothing.
        var orbitMap = ColorMap as IOrbitAwareColorMap;

        // #382: pre-scale the in-set colour's alpha by the global InteriorAlpha
        // knob once (multiplies any alpha the theme's InSetColor already carries),
        // then write it at every in-set pixel. InteriorAlpha == 255 leaves the
        // colour bit-identical.
        uint inSet = ColorMap.InSetColor;
        if (InteriorAlpha < 255)
        {
            uint a = (inSet >> 24) & 0xFFu;
            uint na = (a * (uint)InteriorAlpha) / 255u;
            inSet = (inSet & 0x00FFFFFFu) | (na << 24);
        }

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, () => expr.NewEnv(),
            (y, _, env) =>
            {
                if (ct.IsCancellationRequested) return env;
                double cy = centerY + (y - height * 0.5) * scale;
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    double cx = centerX + (x - width * 0.5) * scale;
                    var c = new Complex(cx, cy);
                    const double h = 1e-6;
                    var cP = new Complex(cx + h, cy);
                    var z = Complex.Zero;
                    var zP = Complex.Zero;
                    Complex prevZ = Complex.Zero, prevZP = Complex.Zero;   // #543 z_{n-1}
                    OrbitAccumulator acc = default;
                    if (orbitMap != null) orbitMap.InitOrbit(out acc);
                    int iter;
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        // Sample BEFORE update; skip iter==0 (z_0 = 0 has no arg).
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        try
                        {
                            // #543 — pass z_{n-1} to the `prev` slot; advance prev
                            // to the current z before overwriting (CalcGen order).
                            var zn = expr.EvalStep(z, c, iter, env, prevZ);
                            var zpn = expr.EvalStep(zP, cP, iter, env, prevZP);
                            prevZ = z; prevZP = zP;
                            z = zn; zP = zpn;
                        }
                        catch { iter = maxIt; break; }
                    }
                    int idx = rowBase + x;
                    if (iter >= maxIt)
                    {
                        ColorBuffer[idx] = inSet;   // #382: alpha pre-scaled above
                        NormalXBuffer[idx] = 0f;
                        NormalYBuffer[idx] = 0f;
                    }
                    else
                    {
                        double mag = Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
                        float smooth = (float)(iter + 1.0 - Math.Log2(Math.Max(1e-10, Math.Log2(Math.Max(mag, 1.0 + 1e-10)))));

                        // Hubbard-Douady normal from numerical dz/dc.
                        double dzdcR = (zP.Real - z.Real) / h;
                        double dzdcI = (zP.Imaginary - z.Imaginary) / h;
                        double u = z.Real * dzdcR + z.Imaginary * dzdcI;
                        double v = -(z.Real * dzdcI - z.Imaginary * dzdcR);
                        double m = Math.Sqrt(u * u + v * v);
                        float nx, ny;
                        if (m > 1e-12) { nx = (float)(u / m); ny = (float)(v / m); }
                        else { nx = 0f; ny = 0f; }
                        NormalXBuffer[idx] = nx;
                        NormalYBuffer[idx] = ny;

                        ColorBuffer[idx] = orbitMap != null
                            ? (uint)orbitMap.MapWithOrbit(smooth, 0f, maxIt, nx, ny, in acc)
                            : (uint)ColorMap.Map(smooth, 0f, maxIt, nx, ny);
                    }
                }
                return env;
            },
            _ => { });
    }
}
