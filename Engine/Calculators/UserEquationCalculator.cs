// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserEquationCalculator.cs
//
// Renders an escape-time fractal whose per-iteration step function is supplied
// at runtime by the user as a C# expression / statement block, compiled via
// Roslyn scripting. Runs scalar (no SIMD) — delegate-call overhead per pixel
// means it is slower than the typed kernels, but interactive at 800×600 with
// modest iteration counts.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class UserEquationCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // ── Phase 11 — surface normals via numerical Jacobian ──────────────────
    //
    // Mandelbrot's typed kernel computes ∂z/∂c analytically (dz' = 2z·dz + 1
    // for z² + c). User-supplied equations have no closed-form derivative
    // available, so we run a parallel-perturbation trajectory per pixel:
    // base (z, c) + perturbed (zP, c + h). At escape, dz/dc ≈ (zP − z) / h.
    // Cost: 2× delegate calls per iteration vs the analytic path.
    //
    // Analytic functions are conformal so a single Re-axis perturbation is
    // enough — Cauchy-Riemann gives dz/dIm(c) = i · dz/dRe(c). Hubbard-Douady
    // escape-potential gradient then yields (nx, ny) routed to the
    // five-parameter ColorMap.Map overload. 2D themes ignore them; 3D Phong
    // themes light the user equation's escape surface for free.

    /// <summary>X component of the escape-potential gradient at escape, in
    /// [-1, 1]. 0 for in-set pixels. Consumed by 3D Phong themes via the
    /// five-parameter ColorMap.Map overload.</summary>
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Y component of the escape-potential gradient. See
    /// <see cref="NormalXBuffer"/>.</summary>
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    // ── High-precision centre limbs ─────────────────────────────────────────
    //
    // The input controller anchors box-zoom, double-click recenter, and
    // wheel zoom in DD/QD precision so the cursor pixel stays under the
    // cursor across the operation. Plain `CenterX` (Hi only) drops the
    // Lo / L2 / L3 limbs on the way to render — at zoom > ~1e15, one Hi
    // ULP is ~100 pixels, so the rendered centre snaps to a coarse grid
    // and clicked-pixel anchoring stops working (the user sees the box
    // zoom land in a "different nearby location"). These extra limbs let
    // Calculate() sum the per-pixel coord as DD or QD and cast the Hi
    // limb back to double for the per-pixel iteration body — the
    // iteration itself stays plain double (delegate-bound Complex), but
    // the per-pixel anchor maps correctly into the complex plane.
    public double CenterXLo { get; set; }
    public double CenterX2 { get; set; }
    public double CenterX3 { get; set; }
    public double CenterYLo { get; set; }
    public double CenterY2 { get; set; }
    public double CenterY3 { get; set; }

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Most recent compile error, or empty string when last compile succeeded.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True if last compile produced a usable delegate.</summary>
    public bool IsCompiled => _compiled != null;

    private Func<Complex, Complex, int, Complex>? _compiled;
    private string _compiledSource = string.Empty;
    // Keeps the assembly backing _compiled alive. Collectible so a superseded
    // compile can be GC-unloaded once no delegate references it (the render
    // loop snapshots `fn = _compiled` into a local, so an in-flight render
    // pins the old context until it finishes — no mid-call unload).
    private AssemblyLoadContext? _lastContext;

    public UserEquationCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
    }

    /// <summary>
    /// Compiles the user source. The source is the BODY of:
    ///   Complex Step(Complex z, Complex c, int n) { ... }
    /// It must return a Complex value. Available APIs: full System.Numerics.Complex
    /// methods plus standard Math wrappers (Re-exported as Sin/Cos/Exp/Log/Pow/Abs).
    /// </summary>
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
            // Full CSharpCompilation, NOT the CSharpScript scripting API.
            // Scripting auto-references the return-type + globals assemblies
            // via Assembly.Location, which is "" under single-file self-
            // contained publish (Linux default) — it throws "Can't create a
            // metadata reference to an assembly without location" no matter
            // what references we hand it, because that broken ref is one the
            // engine adds itself. Compiling a real class to an in-memory
            // assembly (like CalculatorGenHotLoad) sidesteps it entirely and
            // takes references from RoslynRefs' TPA/in-bundle resolver.
            string code = WrapUserSource(source);
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var compilation = CSharpCompilation.Create(
                assemblyName: $"UserEq_{Environment.TickCount}_{Guid.NewGuid():N}",
                syntaxTrees: new[] { syntaxTree },
                references: FracturingFog.Calculators.RoslynRefs.GatherAllTpaRefs(),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var diag in emit.Diagnostics)
                    if (diag.Severity == DiagnosticSeverity.Error)
                        sb.AppendLine(diag.ToString());
                LastError = sb.ToString();
                _compiled = null;
                return;
            }

            ms.Seek(0, SeekOrigin.Begin);
            var ctx = new AssemblyLoadContext($"UserEq_{Environment.TickCount}", isCollectible: true);
            var asm = ctx.LoadFromStream(ms);
            var type = asm.GetType("FracturingFog.UserEq.Generated.__UserEq");
            var del = type?.GetMethod("Get")?.Invoke(null, null)
                          as Func<Complex, Complex, int, Complex>;
            if (del == null)
            {
                LastError = "Compile succeeded but Step delegate was not found.";
                _compiled = null;
                return;
            }
            _compiled = del;
            _lastContext = ctx;   // pin the backing assembly for the live delegate
            _compiledSource = source;
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _compiled = null;
        }
    }

    private static string WrapUserSource(string body)
    {
        // Emit a real compilation unit: a static Step method holding the user
        // body, plus a Get() factory returning it as the delegate the render
        // loop calls. `using static System.Math` re-exports Sin/Cos/Exp/... as
        // bare calls (matches the old scripting AddImports("System.Math")); the
        // System.Numerics import brings Complex + its static members.
        string wrappedBody = body.Contains("return") ? body : $"return {body};";
        return $@"
using System;
using System.Numerics;
using static System.Math;

namespace FracturingFog.UserEq.Generated
{{
    public static class __UserEq
    {{
        public static Complex Step(Complex z, Complex c, int n)
        {{
            {wrappedBody}
        }}

        public static Func<Complex, Complex, int, Complex> Get()
            => (Func<Complex, Complex, int, Complex>)Step;
    }}
}}
";
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (_compiled == null)
        {
            // Try compiling from FractalParameters.UserEquationSource lazily.
            if (!string.IsNullOrWhiteSpace(FractalParameters.UserEquationSource)
                && FractalParameters.UserEquationSource != _compiledSource)
            {
                Compile(FractalParameters.UserEquationSource);
            }
        }

        var fn = _compiled;
        if (fn == null)
        {
            // No compiled equation — fill with theme InSetColor so the screen
            // is at least not stale.
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
        const double bailout2 = 1024.0; // generous bailout for arbitrary maps

        // Precision-tier selection. When the low limbs carry real data
        // (input controller is anchoring in DD or QD) the per-pixel coord
        // must be summed in matching precision or the rendered image
        // disagrees with where the user clicked. Iteration body stays
        // plain double — only the (cx, cy) starting point benefits.
        bool useQD = CenterX2 != 0.0 || CenterX3 != 0.0
                  || CenterY2 != 0.0 || CenterY3 != 0.0;
        bool useDD = !useQD && (CenterXLo != 0.0 || CenterYLo != 0.0);

        QD cxQd = useQD ? new QD(centerX, CenterXLo, CenterX2, CenterX3) : default;
        QD cyQd = useQD ? new QD(centerY, CenterYLo, CenterY2, CenterY3) : default;
        DD cxDd = useDD ? new DD(centerX, CenterXLo) : default;
        DD cyDd = useDD ? new DD(centerY, CenterYLo) : default;

        double rot = FractalParameters.UserEquationRotationDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(rot);
        double sinA = Math.Sin(rot);
        bool skipJacobian = FractalParameters.UserEquationSkipJacobian;

        // P5: gate orbit sampling once per render. Non-orbit themes pay nothing.
        var orbitMap = ColorMap as IOrbitAwareColorMap;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double dy = (y - height * 0.5) * scale;
            double dyCos = dy * cosA;
            double dySin = dy * sinA;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double dx = (x - width * 0.5) * scale;
                double cx, cy;
                if (useQD)
                {
                    // Sum centre + per-pixel offset in QD, take Hi limb.
                    // dx and dy are plain doubles (offset within ~1 pixel
                    // of scale); QD's implicit double promotion handles
                    // the addition.
                    var cxFull = cxQd + (dx * cosA - dySin);
                    var cyFull = cyQd + (dx * sinA + dyCos);
                    cx = cxFull.X0;
                    cy = cyFull.X0;
                }
                else if (useDD)
                {
                    var cxFull = cxDd + (dx * cosA - dySin);
                    var cyFull = cyDd + (dx * sinA + dyCos);
                    cx = cxFull.Hi;
                    cy = cyFull.Hi;
                }
                else
                {
                    cx = centerX + dx * cosA - dySin;
                    cy = centerY + dx * sinA + dyCos;
                }
                var c = new Complex(cx, cy);
                const double h = 1e-6;
                var cP = new Complex(cx + h, cy);
                var z = Complex.Zero;
                var zP = Complex.Zero;
                OrbitAccumulator acc = default;
                if (orbitMap != null) orbitMap.InitOrbit(out acc);
                int iter;
                if (skipJacobian)
                {
                    // Skip parallel-perturbation trajectory — halves delegate
                    // call cost. 3D Phong themes degrade to flat lighting
                    // because surface normals come out zero, but 2D themes
                    // are unaffected.
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        try { z = fn(z, c, iter); }
                        catch { iter = maxIt; break; }
                    }
                }
                else
                {
                    for (iter = 0; iter < maxIt; iter++)
                    {
                        double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                        if (r2 >= bailout2) break;
                        if (orbitMap != null && iter > 0)
                            orbitMap.Sample(ref acc, z.Real, z.Imaginary, cx, cy, iter);
                        try { z = fn(z, c, iter); zP = fn(zP, cP, iter); }
                        catch { iter = maxIt; break; }
                    }
                }
                int idx = rowBase + x;
                if (iter >= maxIt)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    NormalXBuffer[idx] = 0f;
                    NormalYBuffer[idx] = 0f;
                }
                else
                {
                    double mag = Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
                    float smooth = (float)(iter + 1.0 - Math.Log2(Math.Max(1e-10, Math.Log2(Math.Max(mag, 1.0 + 1e-10)))));

                    float nx, ny;
                    if (skipJacobian)
                    {
                        nx = 0f;
                        ny = 0f;
                    }
                    else
                    {
                        // Hubbard-Douady normal: u = Re(conj(z) · dz/dc),
                        // v = -Im(conj(z) · dz/dc). dz/dc ≈ (zP − z) / h
                        // (Cauchy-Riemann gives the Im column for free on analytic fn).
                        double dzdcR = (zP.Real - z.Real) / h;
                        double dzdcI = (zP.Imaginary - z.Imaginary) / h;
                        double u = z.Real * dzdcR + z.Imaginary * dzdcI;          // Re(z̄ · dzdc)
                        double v = -(z.Real * dzdcI - z.Imaginary * dzdcR);       // -Im(z̄ · dzdc)
                        double m = Math.Sqrt(u * u + v * v);
                        if (m > 1e-12) { nx = (float)(u / m); ny = (float)(v / m); }
                        else { nx = 0f; ny = 0f; }
                    }
                    NormalXBuffer[idx] = nx;
                    NormalYBuffer[idx] = ny;

                    ColorBuffer[idx] = orbitMap != null
                        ? (uint)orbitMap.MapWithOrbit(smooth, 0f, maxIt, nx, ny, in acc)
                        : (uint)ColorMap.Map(smooth, 0f, maxIt, nx, ny);
                }
            }
        });
    }
}
