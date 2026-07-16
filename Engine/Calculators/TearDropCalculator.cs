// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/TearDropCalculator.cs
//
// Concrete escape-time calculator for the "Tear Drop" fractal:
//
//      z_{n+1} = (z^3 / c^3) · (-i)  +  c
//
// Promotes the Sandbox equation "z^3 / (c^3)*-i + c" out of the interpreted
// DSL into a hand-rolled inner loop with full distance/normal output and
// perturbation theory at deep zoom.
//
// Precision dispatch:
//   • SP  (double)             — zoom ≲ 1e12
//   • DD-PT  (DD ref / double δ) — zoom ≤ 1e25
//   • QD-PT  (QD ref / double δ) — zoom ≤ ~5e58
//   • DD/QD-FULL                 — DisableAcceleration=true, or PT-glitched pixels
//
// Closed-form derivative carried in every path:
//      d_{n+1} = -3i · z² · d_n / c³  +  3i · z³ / c⁴  +  1
// supplies dz/dc for distance estimation and Milnor-style normal output.
//
// Perturbation theory exploits the c-dependent denominator algebraically:
//      δ_{n+1} = -i · (τ_z3 · C³ − Z³ · τ_c3) / (c³ · C³)  +  dc
// where
//      τ_z3 = Δ · (3 Z² + Δ · (3 Z + Δ))           — recomputed each step
//      τ_c3 = dc · (3 C² + dc · (3 C + dc))        — constant per pixel
// All δ / dc / τ math runs in double. Reference orbit Z_n is stored in DD
// (or QD when zoom > 1e25). A pixel is flagged "glitched" and re-rendered
// in full DD/QD when |Δ| approaches |Z| or |Z| collapses near zero; both
// failure modes blow up the linear PT approximation.

using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class TearDropCalculator : IFractalCalculator
{
    public bool SupportsZoomPan => true;

    // ── Public surface ────────────────────────────────────────────────────────

    public int Width { get; private set; }
    public int Height { get; private set; }

    public double CenterX { get; set; } = 0.0;
    public double CenterXLo { get; set; } = 0.0;
    public double CenterX2 { get; set; } = 0.0;
    public double CenterX3 { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double CenterYLo { get; set; } = 0.0;
    public double CenterY2 { get; set; } = 0.0;
    public double CenterY3 { get; set; } = 0.0;

    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 512;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public FractalParameters FractalParameters { get; set; } = new();

    public bool IsHighPrecisionActive { get; private set; }

    /// <summary>When true skips PT and runs full DD/QD per-pixel everywhere.</summary>
    public bool DisableAcceleration { get; set; } = false;

    /// <summary>Enables 4-wide AVX2+FMA SIMD on PT paths when hardware permits.</summary>
    public bool DisableSimd { get; set; } = false;

    /// <summary>AVX2+FMA available — required by the SIMD PT inner loop.</summary>
    public static bool SimdSupported => Avx2.IsSupported && Fma.IsSupported;

    public const double QDZoomThreshold = 1e25;

    // R = 32 → cubic growth overshoots small bailouts in one step; larger R
    // smooths the colour gradient when smooth-iter takes log·log of |z|.
    private const double BailoutRadius2 = 1024.0;
    private const double LogBailout = 3.4657359027997265; // 0.5·log(1024) = log(32)

    // PT validity: |Δ| ≥ GlitchFactor · |Z| triggers full-precision rerender.
    private const double GlitchFactor = 1e-3;
    // Also fall back when |Z| collapses; small Z makes 1/C³ swamp the linear term.
    private const double GlitchMinZ = 1e-6;

    private static readonly double InvLog3 = 1.0 / Math.Log(3.0);

    // ── Buffers ──────────────────────────────────────────────────────────────

    public int[] IterationBuffer { get; private set; } = Array.Empty<int>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDiBuffer { get; private set; } = Array.Empty<float>();

    // ── Reference orbit cache (Hi limbs for PT delta math) ───────────────────

    private double[] _refZr = Array.Empty<double>();
    private double[] _refZi = Array.Empty<double>();
    private int _refLen;
    private bool _refEscaped;

    // ── Construction ─────────────────────────────────────────────────────────

    public TearDropCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentException("Dimensions must be positive.");
        Width = width;
        Height = height;
        int n = width * height;
        IterationBuffer = new int[n];
        SmoothBuffer = new float[n];
        DistanceBuffer = new float[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
        ColorBuffer = new uint[n];
        FinalZrBuffer = new float[n];
        FinalZiBuffer = new float[n];
        FinalDrBuffer = new float[n];
        FinalDiBuffer = new float[n];
    }

    // ── Top-level dispatch ───────────────────────────────────────────────────

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = MaxIterations;
        bool needsHp = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = needsHp;
        bool useQd = Zoom > QDZoomThreshold;

        if (!needsHp)
        {
            CalculateSP(ct);
            return;
        }

        if (DisableAcceleration)
        {
            if (useQd) CalculateQDFull(ct);
            else       CalculateDDFull(ct);
            return;
        }

        if (useQd) CalculatePT_QDRef(ct);
        else       CalculatePT_DDRef(ct);
    }

    // ── SP path with DE / normals ────────────────────────────────────────────

    private void CalculateSP(CancellationToken ct)
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        var map = ColorMap;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                if (!PrecomputeInverseCubes(cx, cy,
                        out double invC3r, out double invC3i,
                        out double invC4r, out double invC4i))
                {
                    EmitImmediateEscape(idx, map, maxIt);
                    continue;
                }

                double zr = 0.0, zi = 0.0;
                double dr = 0.0, di = 0.0;
                int iter;
                for (iter = 0; iter < maxIt; iter++)
                {
                    double mag2 = zr * zr + zi * zi;
                    if (mag2 >= BailoutRadius2) break;

                    StepDouble(ref zr, ref zi, ref dr, ref di,
                               cx, cy, invC3r, invC3i, invC4r, invC4i);
                }

                FinaliseDouble(idx, iter, maxIt, zr, zi, dr, di, map);
            }
        });
    }

    // ── DD-FULL (no PT) — used when DisableAcceleration ──────────────────────

    private void CalculateDDFull(CancellationToken ct)
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        DD centerXDd = new(CenterX, CenterXLo);
        DD centerYDd = new(CenterY, CenterYLo);
        int width = Width;
        int height = Height;
        var map = ColorMap;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            DD cy = DD.FromCenterOffset(centerYDd, y - height * 0.5, scale);
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                DD cx = DD.FromCenterOffset(centerXDd, x - width * 0.5, scale);
                int idx = rowBase + x;

                IterateFullDD(cx, cy, maxIt, idx, map);
            }
        });
    }

    // ── QD-FULL (no PT) — used when DisableAcceleration ──────────────────────

    private void CalculateQDFull(CancellationToken ct)
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        QD centerXQd = new(CenterX, CenterXLo, CenterX2, CenterX3);
        QD centerYQd = new(CenterY, CenterYLo, CenterY2, CenterY3);
        int width = Width;
        int height = Height;
        var map = ColorMap;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            QD cy = QD.FromCenterOffset(centerYQd, y - height * 0.5, scale);
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                QD cx = QD.FromCenterOffset(centerXQd, x - width * 0.5, scale);
                int idx = rowBase + x;

                IterateFullQD(cx, cy, maxIt, idx, map);
            }
        });
    }

    // ── PT with DD reference orbit ───────────────────────────────────────────

    private void CalculatePT_DDRef(CancellationToken ct)
    {
        DD centerXDd = new(CenterX, CenterXLo);
        DD centerYDd = new(CenterY, CenterYLo);
        int maxIt = MaxIterations;

        ComputeReferenceOrbitDD(centerXDd, centerYDd, maxIt);
        if (_refLen <= 0)
        {
            // Reference itself blew up at iter 0 — c near origin. Fall back.
            CalculateDDFull(ct);
            return;
        }

        // C, C², C³, C⁴ as doubles (Hi limbs are exact representation of ref).
        double Cr = centerXDd.Hi, Ci = centerYDd.Hi;
        ComplexSquare(Cr, Ci, out double C2r, out double C2i);
        ComplexMul(C2r, C2i, Cr, Ci, out double C3r, out double C3i);
        ComplexMul(C3r, C3i, Cr, Ci, out double C4r, out double C4i);
        double C3mag2 = C3r * C3r + C3i * C3i;

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int width = Width;
        int height = Height;
        var map = ColorMap;
        bool useSimd = SimdSupported && !DisableSimd;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double dcy = (y - height * 0.5) * scale;
            int rowBase = y * width;
            int x = 0;
            if (useSimd)
            {
                for (; x + 4 <= width; x += 4)
                {
                    IteratePT_SIMD4(
                        rowBase + x,
                        (x + 0 - width * 0.5) * scale,
                        (x + 1 - width * 0.5) * scale,
                        (x + 2 - width * 0.5) * scale,
                        (x + 3 - width * 0.5) * scale,
                        dcy,
                        Cr, Ci, C2r, C2i, C3r, C3i, C4r, C4i,
                        maxIt, map, scale,
                        centerXDdHi: centerXDd, centerYDdHi: centerYDd,
                        useQdFallback: false);
                }
            }
            for (; x < width; x++)
            {
                double dcx = (x - width * 0.5) * scale;
                int idx = rowBase + x;
                IteratePT(idx, dcx, dcy,
                          Cr, Ci, C2r, C2i, C3r, C3i, C4r, C4i, C3mag2,
                          maxIt, map,
                          centerXDdHi: centerXDd, centerYDdHi: centerYDd,
                          useQdFallback: false);
            }
        });
    }

    // ── PT with QD reference orbit ───────────────────────────────────────────

    private void CalculatePT_QDRef(CancellationToken ct)
    {
        QD centerXQd = new(CenterX, CenterXLo, CenterX2, CenterX3);
        QD centerYQd = new(CenterY, CenterYLo, CenterY2, CenterY3);
        int maxIt = MaxIterations;

        ComputeReferenceOrbitQD(centerXQd, centerYQd, maxIt);
        if (_refLen <= 0)
        {
            CalculateQDFull(ct);
            return;
        }

        double Cr = centerXQd.X0, Ci = centerYQd.X0;
        ComplexSquare(Cr, Ci, out double C2r, out double C2i);
        ComplexMul(C2r, C2i, Cr, Ci, out double C3r, out double C3i);
        ComplexMul(C3r, C3i, Cr, Ci, out double C4r, out double C4i);
        double C3mag2 = C3r * C3r + C3i * C3i;

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int width = Width;
        int height = Height;
        var map = ColorMap;

        DD cxDd = new(centerXQd.X0, centerXQd.X1);
        DD cyDd = new(centerYQd.X0, centerYQd.X1);
        bool useSimd = SimdSupported && !DisableSimd;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double dcy = (y - height * 0.5) * scale;
            int rowBase = y * width;
            int x = 0;
            if (useSimd)
            {
                for (; x + 4 <= width; x += 4)
                {
                    IteratePT_SIMD4(
                        rowBase + x,
                        (x + 0 - width * 0.5) * scale,
                        (x + 1 - width * 0.5) * scale,
                        (x + 2 - width * 0.5) * scale,
                        (x + 3 - width * 0.5) * scale,
                        dcy,
                        Cr, Ci, C2r, C2i, C3r, C3i, C4r, C4i,
                        maxIt, map, scale,
                        centerXDdHi: cxDd, centerYDdHi: cyDd,
                        useQdFallback: true,
                        qdCenterX: centerXQd, qdCenterY: centerYQd);
                }
            }
            for (; x < width; x++)
            {
                double dcx = (x - width * 0.5) * scale;
                int idx = rowBase + x;
                IteratePT(idx, dcx, dcy,
                          Cr, Ci, C2r, C2i, C3r, C3i, C4r, C4i, C3mag2,
                          maxIt, map,
                          centerXDdHi: cxDd, centerYDdHi: cyDd,
                          useQdFallback: true,
                          qdCenterX: centerXQd, qdCenterY: centerYQd);
            }
        });
    }

    // ── Reference orbit (DD) ─────────────────────────────────────────────────

    private void ComputeReferenceOrbitDD(DD cx, DD cy, int maxIter)
    {
        if (_refZr.Length < maxIter + 1)
        {
            _refZr = new double[maxIter + 1];
            _refZi = new double[maxIter + 1];
        }

        DD cx2 = cx.Square();
        DD cy2 = cy.Square();
        DD c3r = cx * (cx2 - cy2 * 3.0);
        DD c3i = cy * (cx2 * 3.0 - cy2);
        DD c3mag2 = c3r.Square() + c3i.Square();
        if (c3mag2.Hi < 1e-300)
        {
            _refLen = 0;
            _refEscaped = false;
            return;
        }

        DD zr = DD.Zero, zi = DD.Zero;
        _refZr[0] = 0.0; _refZi[0] = 0.0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            DD zr2 = zr.Square();
            DD zi2 = zi.Square();
            DD mag2 = zr2 + zi2;
            if (mag2 >= BailoutRadius2) break;

            DD z3r = zr * (zr2 - zi2 * 3.0);
            DD z3i = zi * (zr2 * 3.0 - zi2);

            DD numR = z3r * c3r + z3i * c3i;
            DD numI = z3i * c3r - z3r * c3i;
            DD uR = numR / c3mag2;
            DD uI = numI / c3mag2;

            DD newZr = uI + cx;
            DD newZi = -uR + cy;
            zr = newZr; zi = newZi;

            _refZr[n + 1] = zr.Hi;
            _refZi[n + 1] = zi.Hi;
        }
        _refLen = n + 1;             // entries 0..n valid
        _refEscaped = n < maxIter;
    }

    // ── Reference orbit (QD) ─────────────────────────────────────────────────

    private void ComputeReferenceOrbitQD(QD cx, QD cy, int maxIter)
    {
        if (_refZr.Length < maxIter + 1)
        {
            _refZr = new double[maxIter + 1];
            _refZi = new double[maxIter + 1];
        }

        QD cx2 = cx.Square();
        QD cy2 = cy.Square();
        QD c3r = cx * (cx2 - cy2 * 3.0);
        QD c3i = cy * (cx2 * 3.0 - cy2);
        QD c3mag2 = c3r.Square() + c3i.Square();
        if (c3mag2.X0 < 1e-300)
        {
            _refLen = 0;
            _refEscaped = false;
            return;
        }

        QD zr = QD.Zero, zi = QD.Zero;
        _refZr[0] = 0.0; _refZi[0] = 0.0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            QD zr2 = zr.Square();
            QD zi2 = zi.Square();
            QD mag2 = zr2 + zi2;
            if (mag2 >= BailoutRadius2) break;

            QD z3r = zr * (zr2 - zi2 * 3.0);
            QD z3i = zi * (zr2 * 3.0 - zi2);

            QD numR = z3r * c3r + z3i * c3i;
            QD numI = z3i * c3r - z3r * c3i;
            QD uR = numR / c3mag2;
            QD uI = numI / c3mag2;

            QD newZr = uI + cx;
            QD newZi = -uR + cy;
            zr = newZr; zi = newZi;

            _refZr[n + 1] = zr.X0;
            _refZi[n + 1] = zi.X0;
        }
        _refLen = n + 1;
        _refEscaped = n < maxIter;
    }

    // ── PT pixel loop (shared by DD-ref and QD-ref) ──────────────────────────

    private void IteratePT(
        int idx,
        double dcr, double dci,
        double Cr, double Ci,
        double C2r, double C2i,
        double C3r, double C3i,
        double C4r, double C4i,
        double C3mag2,
        int maxIt,
        IColorMap map,
        DD centerXDdHi, DD centerYDdHi,
        bool useQdFallback,
        QD qdCenterX = default, QD qdCenterY = default)
    {
        // Per-pixel c (double approximation) and c-cubed prelude.
        double cr = Cr + dcr;
        double ci = Ci + dci;

        // τ_c3 = dc · (3 C² + dc · (3 C + dc))
        // Builds c³ − C³ directly without forming both quantities.
        ComplexMul(dcr, dci, dcr, dci, out double dc2r, out double dc2i);

        // tmp = 3C + dc
        double tmp_r = 3.0 * Cr + dcr;
        double tmp_i = 3.0 * Ci + dci;
        // tmp = dc · (3C + dc)
        ComplexMul(dcr, dci, tmp_r, tmp_i, out tmp_r, out tmp_i);
        // tmp = 3C² + tmp
        tmp_r += 3.0 * C2r;
        tmp_i += 3.0 * C2i;
        // τ_c3 = dc · tmp
        ComplexMul(dcr, dci, tmp_r, tmp_i, out double tau_c3r, out double tau_c3i);

        // c³ = C³ + τ_c3   (double; reference C³ is double-Hi accurate)
        double c3r_pix = C3r + tau_c3r;
        double c3i_pix = C3i + tau_c3i;
        double c3mag2_pix = c3r_pix * c3r_pix + c3i_pix * c3i_pix;
        if (c3mag2_pix < 1e-300)
        {
            EmitImmediateEscape(idx, map, maxIt);
            return;
        }

        // 1/c³ as complex = conj(c³) / |c³|²   — for d-update (uses c per pixel).
        double invC3pr = c3r_pix / c3mag2_pix;
        double invC3pi = -c3i_pix / c3mag2_pix;
        // c⁴ = c³ · c   and 1/c⁴
        ComplexMul(c3r_pix, c3i_pix, cr, ci, out double c4r_pix, out double c4i_pix);
        double c4mag2_pix = c4r_pix * c4r_pix + c4i_pix * c4i_pix;
        if (c4mag2_pix < 1e-300)
        {
            EmitImmediateEscape(idx, map, maxIt);
            return;
        }
        double invC4pr = c4r_pix / c4mag2_pix;
        double invC4pi = -c4i_pix / c4mag2_pix;

        // Denominator for the (z³/c³ − Z³/C³) division: (C³+τ_c3)·C³ = c³·C³.
        ComplexMul(c3r_pix, c3i_pix, C3r, C3i, out double denomR, out double denomI);
        double denomMag2 = denomR * denomR + denomI * denomI;
        if (denomMag2 < 1e-300)
        {
            EmitImmediateEscape(idx, map, maxIt);
            return;
        }

        // Δ_0 = 0, d_0 = 0.
        double dR = 0.0, dI = 0.0;
        double deltaR = 0.0, deltaI = 0.0;

        int iter;
        bool glitched = false;
        for (iter = 0; iter < maxIt; iter++)
        {
            // Stop if reference orbit ran out (shouldn't normally — ref orbit
            // is built for the same maxIt cap).
            if (iter >= _refLen) { glitched = true; break; }

            double Zr = _refZr[iter];
            double Zi = _refZi[iter];

            // z = Z + Δ
            double zr = Zr + deltaR;
            double zi = Zi + deltaI;

            double mag2 = zr * zr + zi * zi;
            if (mag2 >= BailoutRadius2) break;

            // Glitch check: PT linearisation invalid when |Δ| ≳ |Z|, or |Z|
            // shrinks below the noise floor where Z·Δ products lose precision.
            double zRefMag2 = Zr * Zr + Zi * Zi;
            double dlMag2 = deltaR * deltaR + deltaI * deltaI;
            if (iter > 0 &&
                (dlMag2 > GlitchFactor * GlitchFactor * Math.Max(zRefMag2, GlitchMinZ * GlitchMinZ)))
            {
                glitched = true;
                break;
            }

            // ── Δ update ──
            // Z² = (Zr²−Zi², 2 Zr Zi)
            ComplexSquare(Zr, Zi, out double Z2r, out double Z2i);
            // Z³ = Z² · Z
            ComplexMul(Z2r, Z2i, Zr, Zi, out double Z3r, out double Z3i);

            // τ_z3 = Δ · (3Z² + Δ · (3Z + Δ))
            double t_r = 3.0 * Zr + deltaR;
            double t_i = 3.0 * Zi + deltaI;
            ComplexMul(deltaR, deltaI, t_r, t_i, out t_r, out t_i);
            t_r += 3.0 * Z2r;
            t_i += 3.0 * Z2i;
            ComplexMul(deltaR, deltaI, t_r, t_i, out double tau_z3r, out double tau_z3i);

            // num = τ_z3 · C³ − Z³ · τ_c3
            ComplexMul(tau_z3r, tau_z3i, C3r, C3i, out double numA_r, out double numA_i);
            ComplexMul(Z3r, Z3i, tau_c3r, tau_c3i, out double numB_r, out double numB_i);
            double numR = numA_r - numB_r;
            double numI = numA_i - numB_i;

            // diff = num / denom  (denom precomputed per pixel)
            // diff = num · conj(denom) / |denom|²
            double diffR = (numR * denomR + numI * denomI) / denomMag2;
            double diffI = (numI * denomR - numR * denomI) / denomMag2;

            // Δ_{n+1} = -i · diff + dc = (diff.i + dc.r, -diff.r + dc.i)
            double newDeltaR = diffI + dcr;
            double newDeltaI = -diffR + dci;

            // ── d update (no perturbation form — track per-pixel d directly) ──
            // z², z³ for this pixel (z = Z + Δ).
            ComplexSquare(zr, zi, out double z2r, out double z2i);
            ComplexMul(z2r, z2i, zr, zi, out double z3r, out double z3i);
            // A = z² · d / c³ = (z²·d) · invC3
            ComplexMul(z2r, z2i, dR, dI, out double z2dR, out double z2dI);
            ComplexMul(z2dR, z2dI, invC3pr, invC3pi, out double A_r, out double A_i);
            // B = z³ / c⁴
            ComplexMul(z3r, z3i, invC4pr, invC4pi, out double B_r, out double B_i);
            // d_{n+1} = -3i·A + 3i·B + 1
            //        = (3(A.i − B.i) + 1, 3(B.r − A.r))
            double newDr = 3.0 * (A_i - B_i) + 1.0;
            double newDi = 3.0 * (B_r - A_r);

            deltaR = newDeltaR;
            deltaI = newDeltaI;
            dR = newDr; dI = newDi;
        }

        if (glitched)
        {
            // Re-render this pixel at full DD/QD per-pixel. Compute its actual
            // c from the high-precision centre + offset.
            if (useQdFallback)
            {
                QD cx_qd = QD.FromCenterOffset(qdCenterX, dcr / ((3.5 / Math.Max(Width, Height)) / Zoom) * 0.0 + 0.0, 0.0);
                // ↑ unused alternate path. Simpler: compute pixel coord directly.
                double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
                // dc was generated as (x − W/2)·scale, so x − W/2 = dcr/scale.
                double pxOffR = dcr / scale;
                double pxOffI = dci / scale;
                QD cx = QD.FromCenterOffset(qdCenterX, pxOffR, scale);
                QD cy = QD.FromCenterOffset(qdCenterY, pxOffI, scale);
                IterateFullQD(cx, cy, maxIt, idx, map);
            }
            else
            {
                double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
                double pxOffR = dcr / scale;
                double pxOffI = dci / scale;
                DD cx = DD.FromCenterOffset(centerXDdHi, pxOffR, scale);
                DD cy = DD.FromCenterOffset(centerYDdHi, pxOffI, scale);
                IterateFullDD(cx, cy, maxIt, idx, map);
            }
            return;
        }

        // Finalise from final z (= Z_iter + Δ_iter) and d.
        if (iter < maxIt && iter < _refLen)
        {
            double zr = _refZr[iter] + deltaR;
            double zi = _refZi[iter] + deltaI;
            FinaliseDouble(idx, iter, maxIt, zr, zi, dR, dI, map);
        }
        else if (iter >= maxIt)
        {
            FinaliseInSet(idx, map);
        }
        else
        {
            FinaliseInSet(idx, map);
        }
    }

    // ── PT pixel loop, 4-wide AVX2+FMA SIMD ──────────────────────────────────
    //
    // Processes 4 horizontally-adjacent pixels in one row. dcy and the
    // reference orbit Zn are broadcast (identical across lanes); dcx and all
    // per-pixel state (Δ, d, c³, denom, invC3, invC4, τ_c3) are lane-distinct.
    //
    // An active-lane mask freezes per-lane state once a lane escapes or
    // glitches; glitched lanes are recorded for a post-loop scalar full-DD/QD
    // rerun. Tail of the row (width % 4) is handled by the scalar IteratePT.

    private void IteratePT_SIMD4(
        int baseIdx,
        double dcx0, double dcx1, double dcx2, double dcx3,
        double dcyScalar,
        double Cr, double Ci,
        double C2r, double C2i,
        double C3r, double C3i,
        double C4r, double C4i,
        int maxIt,
        IColorMap map,
        double scale,
        DD centerXDdHi, DD centerYDdHi,
        bool useQdFallback,
        QD qdCenterX = default, QD qdCenterY = default)
    {
        // ─ Broadcasts ─
        var vZero    = Vector256<double>.Zero;
        var vOne     = Vector256.Create(1.0);
        var vThree   = Vector256.Create(3.0);
        var vCr      = Vector256.Create(Cr);
        var vCi      = Vector256.Create(Ci);
        var vC2r     = Vector256.Create(C2r);
        var vC2i     = Vector256.Create(C2i);
        var vC3r     = Vector256.Create(C3r);
        var vC3i     = Vector256.Create(C3i);
        var vBailout = Vector256.Create(BailoutRadius2);
        var vGFsq    = Vector256.Create(GlitchFactor * GlitchFactor);
        var vGMZsq   = Vector256.Create(GlitchMinZ * GlitchMinZ);
        var vTiny    = Vector256.Create(1e-300);

        // dcr lanes 0..3, dci broadcast across lanes (same row).
        var vDcr = Vector256.Create(dcx0, dcx1, dcx2, dcx3);
        var vDci = Vector256.Create(dcyScalar);

        // ─ Per-pixel τ_c3 = dc · (3 C² + dc · (3 C + dc)) ─
        // tmp = 3C + dc
        var tmpR = Avx.Add(Avx.Multiply(vThree, vCr), vDcr);
        var tmpI = Avx.Add(Avx.Multiply(vThree, vCi), vDci);
        // tmp = dc · (3C + dc)
        VMul(vDcr, vDci, tmpR, tmpI, out tmpR, out tmpI);
        // tmp = 3 C² + tmp
        tmpR = Fma.MultiplyAdd(vThree, vC2r, tmpR);
        tmpI = Fma.MultiplyAdd(vThree, vC2i, tmpI);
        // τ_c3
        VMul(vDcr, vDci, tmpR, tmpI, out var vTauC3r, out var vTauC3i);

        // c³_pix = C³ + τ_c3
        var vC3PixR = Avx.Add(vC3r, vTauC3r);
        var vC3PixI = Avx.Add(vC3i, vTauC3i);
        var vC3PixMag2 = Fma.MultiplyAdd(vC3PixR, vC3PixR, Avx.Multiply(vC3PixI, vC3PixI));

        // c_pix = C + dc
        var vCrPix = Avx.Add(vCr, vDcr);
        var vCiPix = Avx.Add(vCi, vDci);

        // c⁴_pix = c³_pix · c_pix
        VMul(vC3PixR, vC3PixI, vCrPix, vCiPix, out var vC4PixR, out var vC4PixI);
        var vC4PixMag2 = Fma.MultiplyAdd(vC4PixR, vC4PixR, Avx.Multiply(vC4PixI, vC4PixI));

        // denom = c³_pix · C³
        VMul(vC3PixR, vC3PixI, vC3r, vC3i, out var vDenomR, out var vDenomI);
        var vDenomMag2 = Fma.MultiplyAdd(vDenomR, vDenomR, Avx.Multiply(vDenomI, vDenomI));

        // Invalid-pixel mask: any of c³_pix, c⁴_pix, denom too tiny.
        // Mask high bit set ⇒ lane is invalid.
        var vInvalid = Avx.Or(
            Avx.Or(
                Avx.CompareLessThan(vC3PixMag2, vTiny),
                Avx.CompareLessThan(vC4PixMag2, vTiny)),
            Avx.CompareLessThan(vDenomMag2, vTiny));

        // invC3_pix = conj(c³_pix) / |c³_pix|²
        var vInvC3PixR = Avx.Divide(vC3PixR, vC3PixMag2);
        var vInvC3PixI = Avx.Divide(Avx.Subtract(vZero, vC3PixI), vC3PixMag2);
        // invC4_pix = conj(c⁴_pix) / |c⁴_pix|²
        var vInvC4PixR = Avx.Divide(vC4PixR, vC4PixMag2);
        var vInvC4PixI = Avx.Divide(Avx.Subtract(vZero, vC4PixI), vC4PixMag2);

        // Δ_0 = 0, d_0 = 0.
        var vDr = vZero;
        var vDi = vZero;
        var vDeltaR = vZero;
        var vDeltaI = vZero;

        // Lane status. activeMask: -1 (all bits set) ⇒ lane still iterating.
        // escapedIter[k] = iter index at which lane k escaped; maxIt if never.
        // glitched[k] = true if lane k tripped glitch test.
        var vActive = Avx.CompareEqual(vInvalid, vInvalid); // start: all -1
        // Mask out invalid lanes immediately.
        vActive = Avx.AndNot(vInvalid, vActive);

        Span<int> escapedIter = stackalloc int[4] { maxIt, maxIt, maxIt, maxIt };
        Span<double> finalZr = stackalloc double[4];
        Span<double> finalZi = stackalloc double[4];
        Span<double> finalDr = stackalloc double[4];
        Span<double> finalDi = stackalloc double[4];
        Span<bool> glitched = stackalloc bool[4];

        // Lanes flagged invalid up-front: emit immediate escape after loop.
        Span<bool> immediateEscape = stackalloc bool[4];
        int invalidMask = Avx.MoveMask(vInvalid);
        for (int k = 0; k < 4; k++) immediateEscape[k] = ((invalidMask >> k) & 1) != 0;

        int activeBits = Avx.MoveMask(vActive) & 0xF;

        int iter;
        for (iter = 0; iter < maxIt && activeBits != 0; iter++)
        {
            if (iter >= _refLen)
            {
                // Reference orbit shorter than maxIt — flag remaining active
                // lanes as glitched and rerun via full DD/QD.
                for (int k = 0; k < 4; k++)
                    if (((activeBits >> k) & 1) != 0) glitched[k] = true;
                break;
            }

            double Zr = _refZr[iter];
            double Zi = _refZi[iter];
            var vZr = Vector256.Create(Zr);
            var vZi = Vector256.Create(Zi);

            // z = Z + Δ
            var vzr = Avx.Add(vZr, vDeltaR);
            var vzi = Avx.Add(vZi, vDeltaI);
            var vMag2 = Fma.MultiplyAdd(vzr, vzr, Avx.Multiply(vzi, vzi));

            // Escape mask = (mag2 ≥ bailout) AND active.
            var vEscape = Avx.And(
                Avx.CompareGreaterThanOrEqual(vMag2, vBailout),
                vActive);

            // Glitch mask = (|Δ|² > GF²·max(|Z|², GMZ²)) AND active AND iter>0.
            Vector256<double> vGlitch = vZero;
            if (iter > 0)
            {
                var vZrefMag2 = Vector256.Create(Zr * Zr + Zi * Zi);
                var vDlMag2 = Fma.MultiplyAdd(vDeltaR, vDeltaR, Avx.Multiply(vDeltaI, vDeltaI));
                var vRhs = Avx.Multiply(vGFsq, Avx.Max(vZrefMag2, vGMZsq));
                vGlitch = Avx.And(Avx.CompareGreaterThan(vDlMag2, vRhs), vActive);
            }

            int escMask = Avx.MoveMask(vEscape) & 0xF;
            int glMask = Avx.MoveMask(vGlitch) & 0xF;

            // Snapshot newly-terminating lanes into finals.
            int terminating = escMask | glMask;
            if (terminating != 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (((terminating >> k) & 1) == 0) continue;
                    escapedIter[k] = iter;
                    finalZr[k] = vzr.GetElement(k);
                    finalZi[k] = vzi.GetElement(k);
                    finalDr[k] = vDr.GetElement(k);
                    finalDi[k] = vDi.GetElement(k);
                    if (((glMask >> k) & 1) != 0) glitched[k] = true;
                }
                // Drop terminating lanes from active set.
                vActive = Avx.AndNot(Avx.Or(vEscape, vGlitch), vActive);
                activeBits = Avx.MoveMask(vActive) & 0xF;
                if (activeBits == 0) { iter++; break; }
            }

            // ─ Δ update ─
            // Z² = (Zr²−Zi², 2 Zr Zi)
            double Z2r_s = Zr * Zr - Zi * Zi;
            double Z2i_s = 2.0 * Zr * Zi;
            var vZ2r = Vector256.Create(Z2r_s);
            var vZ2i = Vector256.Create(Z2i_s);
            // Z³ = Z² · Z
            double Z3r_s = Z2r_s * Zr - Z2i_s * Zi;
            double Z3i_s = Z2r_s * Zi + Z2i_s * Zr;
            var vZ3r = Vector256.Create(Z3r_s);
            var vZ3i = Vector256.Create(Z3i_s);

            // τ_z3 = Δ · (3Z² + Δ · (3Z + Δ))
            var tR = Avx.Add(Avx.Multiply(vThree, vZr), vDeltaR);
            var tI = Avx.Add(Avx.Multiply(vThree, vZi), vDeltaI);
            VMul(vDeltaR, vDeltaI, tR, tI, out tR, out tI);
            tR = Fma.MultiplyAdd(vThree, vZ2r, tR);
            tI = Fma.MultiplyAdd(vThree, vZ2i, tI);
            VMul(vDeltaR, vDeltaI, tR, tI, out var vTauZ3r, out var vTauZ3i);

            // num = τ_z3 · C³ − Z³ · τ_c3
            VMul(vTauZ3r, vTauZ3i, vC3r, vC3i, out var vNumAr, out var vNumAi);
            VMul(vZ3r, vZ3i, vTauC3r, vTauC3i, out var vNumBr, out var vNumBi);
            var vNumR = Avx.Subtract(vNumAr, vNumBr);
            var vNumI = Avx.Subtract(vNumAi, vNumBi);

            // diff = num · conj(denom) / |denom|²
            var vNumDotDr = Fma.MultiplyAdd(vNumR, vDenomR, Avx.Multiply(vNumI, vDenomI));
            var vNumCrsDi = Fma.MultiplySubtract(vNumI, vDenomR, Avx.Multiply(vNumR, vDenomI));
            var vDiffR = Avx.Divide(vNumDotDr, vDenomMag2);
            var vDiffI = Avx.Divide(vNumCrsDi, vDenomMag2);

            // Δ_{n+1} = -i · diff + dc = (diff.i + dc.r, -diff.r + dc.i)
            var vNewDeltaR = Avx.Add(vDiffI, vDcr);
            var vNewDeltaI = Avx.Add(Avx.Subtract(vZero, vDiffR), vDci);

            // ─ d update (per-pixel z, c) ─
            // z = Z + Δ (already computed as vzr, vzi)
            // z² = (zr²−zi², 2 zr zi)
            var vz2r = Fma.MultiplySubtract(vzr, vzr, Avx.Multiply(vzi, vzi));
            var vz2i = Avx.Multiply(Avx.Multiply(Vector256.Create(2.0), vzr), vzi);
            // z³ = z² · z
            VMul(vz2r, vz2i, vzr, vzi, out var vz3r, out var vz3i);
            // A = (z² · d) · invC3_pix
            VMul(vz2r, vz2i, vDr, vDi, out var vZ2dR, out var vZ2dI);
            VMul(vZ2dR, vZ2dI, vInvC3PixR, vInvC3PixI, out var vAr, out var vAi);
            // B = z³ · invC4_pix
            VMul(vz3r, vz3i, vInvC4PixR, vInvC4PixI, out var vBr, out var vBi);
            // d_{n+1} = (3(A.i − B.i) + 1, 3(B.r − A.r))
            var vNewDr = Fma.MultiplyAdd(vThree, Avx.Subtract(vAi, vBi), vOne);
            var vNewDi = Avx.Multiply(vThree, Avx.Subtract(vBr, vAr));

            // Blend so escaped/glitched lanes freeze their state.
            vDeltaR = Avx.BlendVariable(vDeltaR, vNewDeltaR, vActive);
            vDeltaI = Avx.BlendVariable(vDeltaI, vNewDeltaI, vActive);
            vDr     = Avx.BlendVariable(vDr,     vNewDr,     vActive);
            vDi     = Avx.BlendVariable(vDi,     vNewDi,     vActive);
        }

        // Any lane still active at maxIt is "in set".
        // Capture remaining lanes (no escape this loop).
        int stillActive = activeBits;
        if (stillActive != 0)
        {
            for (int k = 0; k < 4; k++)
            {
                if (((stillActive >> k) & 1) == 0) continue;
                // Synthesise final z = Z_{maxIt-1?} + Δ if we want, but simpler:
                // mark as in-set by leaving escapedIter[k] = maxIt; finals untouched.
                // Actually we may want final z for color, but FinaliseInSet
                // doesn't use it — leave as-is.
                escapedIter[k] = maxIt;
            }
        }

        // ─ Per-lane finalisation ─
        for (int k = 0; k < 4; k++)
        {
            int idx = baseIdx + k;
            if (immediateEscape[k])
            {
                EmitImmediateEscape(idx, map, maxIt);
                continue;
            }
            if (glitched[k])
            {
                double dcr = k == 0 ? dcx0 : k == 1 ? dcx1 : k == 2 ? dcx2 : dcx3;
                double dci = dcyScalar;
                if (useQdFallback)
                {
                    double pxOffR = dcr / scale;
                    double pxOffI = dci / scale;
                    QD cx = QD.FromCenterOffset(qdCenterX, pxOffR, scale);
                    QD cy = QD.FromCenterOffset(qdCenterY, pxOffI, scale);
                    IterateFullQD(cx, cy, maxIt, idx, map);
                }
                else
                {
                    double pxOffR = dcr / scale;
                    double pxOffI = dci / scale;
                    DD cx = DD.FromCenterOffset(centerXDdHi, pxOffR, scale);
                    DD cy = DD.FromCenterOffset(centerYDdHi, pxOffI, scale);
                    IterateFullDD(cx, cy, maxIt, idx, map);
                }
                continue;
            }

            int it = escapedIter[k];
            if (it >= maxIt)
            {
                IterationBuffer[idx] = maxIt;
                FinaliseInSet(idx, map);
            }
            else
            {
                FinaliseDouble(idx, it, maxIt,
                    finalZr[k], finalZi[k], finalDr[k], finalDi[k], map);
            }
        }
    }

    // 4-lane complex multiply: (ar+ai i)·(br+bi i)
    private static void VMul(
        Vector256<double> ar, Vector256<double> ai,
        Vector256<double> br, Vector256<double> bi,
        out Vector256<double> rr, out Vector256<double> ri)
    {
        // rr = ar*br − ai*bi  =  FMA(ar, br, −ai*bi)  =  FMS(ar, br, ai*bi)
        // ri = ar*bi + ai*br
        rr = Fma.MultiplySubtract(ar, br, Avx.Multiply(ai, bi));
        ri = Fma.MultiplyAdd(ar, bi, Avx.Multiply(ai, br));
    }

    // ── Full DD per-pixel iteration with DE/normals ──────────────────────────

    private void IterateFullDD(DD cx, DD cy, int maxIt, int idx, IColorMap map)
    {
        DD cx2 = cx.Square();
        DD cy2 = cy.Square();
        DD c3r = cx * (cx2 - cy2 * 3.0);
        DD c3i = cy * (cx2 * 3.0 - cy2);
        DD c3mag2 = c3r.Square() + c3i.Square();
        if (c3mag2.Hi < 1e-300) { EmitImmediateEscape(idx, map, maxIt); return; }

        // c⁴ = c³ · c
        DD c4r = c3r * cx - c3i * cy;
        DD c4i = c3r * cy + c3i * cx;
        DD c4mag2 = c4r.Square() + c4i.Square();
        if (c4mag2.Hi < 1e-300) { EmitImmediateEscape(idx, map, maxIt); return; }

        // Pre-compute conj(c³)/|c³|² and conj(c⁴)/|c⁴|² for repeated divisions.
        DD invC3r = c3r / c3mag2;
        DD invC3i = -c3i / c3mag2;
        DD invC4r = c4r / c4mag2;
        DD invC4i = -c4i / c4mag2;

        DD zr = DD.Zero, zi = DD.Zero;
        DD dr = DD.Zero, di = DD.Zero;
        int iter;
        for (iter = 0; iter < maxIt; iter++)
        {
            DD zr2 = zr.Square();
            DD zi2 = zi.Square();
            DD mag2 = zr2 + zi2;
            if (mag2 >= BailoutRadius2) break;

            DD z3r = zr * (zr2 - zi2 * 3.0);
            DD z3i = zi * (zr2 * 3.0 - zi2);

            // u = z³ · invC3
            DD uR = z3r * invC3r - z3i * invC3i;
            DD uI = z3r * invC3i + z3i * invC3r;

            // A = z² · d · invC3
            DD z2dR = zr2 * dr - (zr * zi * 2.0) * di;            // z² · d
            DD z2dI = zr2 * di + (zr * zi * 2.0) * dr;
            // Recompute z² as complex for clarity:
            DD z2r = zr2 - zi2;
            DD z2i = zr * zi * 2.0;
            z2dR = z2r * dr - z2i * di;
            z2dI = z2r * di + z2i * dr;
            DD AR = z2dR * invC3r - z2dI * invC3i;
            DD AI = z2dR * invC3i + z2dI * invC3r;
            // B = z³ · invC4
            DD BR = z3r * invC4r - z3i * invC4i;
            DD BI = z3r * invC4i + z3i * invC4r;
            // d_{n+1} = (3(A.i − B.i) + 1, 3(B.r − A.r))
            DD newDr = (AI - BI) * 3.0 + 1.0;
            DD newDi = (BR - AR) * 3.0;

            // z_{n+1} = (u.i + cx, −u.r + cy)
            DD newZr = uI + cx;
            DD newZi = -uR + cy;
            zr = newZr; zi = newZi;
            dr = newDr; di = newDi;
        }

        FinaliseDouble(idx, iter, maxIt, zr.Hi, zi.Hi, dr.Hi, di.Hi, map);
    }

    // ── Full QD per-pixel iteration with DE/normals ──────────────────────────

    private void IterateFullQD(QD cx, QD cy, int maxIt, int idx, IColorMap map)
    {
        QD cx2 = cx.Square();
        QD cy2 = cy.Square();
        QD c3r = cx * (cx2 - cy2 * 3.0);
        QD c3i = cy * (cx2 * 3.0 - cy2);
        QD c3mag2 = c3r.Square() + c3i.Square();
        if (c3mag2.X0 < 1e-300) { EmitImmediateEscape(idx, map, maxIt); return; }

        QD c4r = c3r * cx - c3i * cy;
        QD c4i = c3r * cy + c3i * cx;
        QD c4mag2 = c4r.Square() + c4i.Square();
        if (c4mag2.X0 < 1e-300) { EmitImmediateEscape(idx, map, maxIt); return; }

        QD invC3r = c3r / c3mag2;
        QD invC3i = -c3i / c3mag2;
        QD invC4r = c4r / c4mag2;
        QD invC4i = -c4i / c4mag2;

        QD zr = QD.Zero, zi = QD.Zero;
        QD dr = QD.Zero, di = QD.Zero;
        int iter;
        for (iter = 0; iter < maxIt; iter++)
        {
            QD zr2 = zr.Square();
            QD zi2 = zi.Square();
            QD mag2 = zr2 + zi2;
            if (mag2 >= BailoutRadius2) break;

            QD z3r = zr * (zr2 - zi2 * 3.0);
            QD z3i = zi * (zr2 * 3.0 - zi2);

            QD uR = z3r * invC3r - z3i * invC3i;
            QD uI = z3r * invC3i + z3i * invC3r;

            QD z2r = zr2 - zi2;
            QD z2i = zr * zi * 2.0;
            QD z2dR = z2r * dr - z2i * di;
            QD z2dI = z2r * di + z2i * dr;
            QD AR = z2dR * invC3r - z2dI * invC3i;
            QD AI = z2dR * invC3i + z2dI * invC3r;
            QD BR = z3r * invC4r - z3i * invC4i;
            QD BI = z3r * invC4i + z3i * invC4r;
            QD newDr = (AI - BI) * 3.0 + 1.0;
            QD newDi = (BR - AR) * 3.0;

            QD newZr = uI + cx;
            QD newZi = -uR + cy;
            zr = newZr; zi = newZi;
            dr = newDr; di = newDi;
        }

        FinaliseDouble(idx, iter, maxIt, zr.X0, zi.X0, dr.X0, di.X0, map);
    }

    // ── Double-precision one-step (used by SP path) ──────────────────────────

    private static void StepDouble(
        ref double zr, ref double zi, ref double dr, ref double di,
        double cx, double cy,
        double invC3r, double invC3i,
        double invC4r, double invC4i)
    {
        double zr2 = zr * zr;
        double zi2 = zi * zi;
        double z3r = zr * (zr2 - 3.0 * zi2);
        double z3i = zi * (3.0 * zr2 - zi2);

        // u = z³ · invC3
        ComplexMul(z3r, z3i, invC3r, invC3i, out double uR, out double uI);
        // z² complex
        double z2r = zr2 - zi2;
        double z2i = 2.0 * zr * zi;
        // A = (z²·d) · invC3
        ComplexMul(z2r, z2i, dr, di, out double z2dR, out double z2dI);
        ComplexMul(z2dR, z2dI, invC3r, invC3i, out double A_r, out double A_i);
        // B = z³ · invC4
        ComplexMul(z3r, z3i, invC4r, invC4i, out double B_r, out double B_i);
        // d_{n+1} = (3(A.i − B.i) + 1, 3(B.r − A.r))
        double newDr = 3.0 * (A_i - B_i) + 1.0;
        double newDi = 3.0 * (B_r - A_r);

        // z_{n+1} = (u.i + cx, −u.r + cy)
        double newZr = uI + cx;
        double newZi = -uR + cy;

        zr = newZr; zi = newZi;
        dr = newDr; di = newDi;
    }

    // ── Per-pixel cube + invC3/invC4 setup (SP only) ─────────────────────────

    private static bool PrecomputeInverseCubes(
        double cx, double cy,
        out double invC3r, out double invC3i,
        out double invC4r, out double invC4i)
    {
        double cx2 = cx * cx, cy2 = cy * cy;
        double c3r = cx * (cx2 - 3.0 * cy2);
        double c3i = cy * (3.0 * cx2 - cy2);
        double c3m2 = c3r * c3r + c3i * c3i;
        if (c3m2 < 1e-300) { invC3r = invC3i = invC4r = invC4i = 0; return false; }
        invC3r = c3r / c3m2;
        invC3i = -c3i / c3m2;

        // c⁴ = c³ · c
        ComplexMul(c3r, c3i, cx, cy, out double c4r, out double c4i);
        double c4m2 = c4r * c4r + c4i * c4i;
        if (c4m2 < 1e-300) { invC4r = invC4i = 0; return false; }
        invC4r = c4r / c4m2;
        invC4i = -c4i / c4m2;
        return true;
    }

    // ── Buffer / colour write helpers ────────────────────────────────────────

    private void FinaliseDouble(
        int idx, int iter, int maxIt,
        double zr, double zi, double dr, double di,
        IColorMap map)
    {
        IterationBuffer[idx] = iter;
        if (iter >= maxIt)
        {
            FinaliseInSet(idx, map);
            return;
        }

        double mag = Math.Sqrt(zr * zr + zi * zi);
        float smooth;
        if (mag <= 1.0) smooth = iter;
        else
        {
            double logMag = Math.Log(mag);
            if (logMag <= 0.0) smooth = iter;
            else
            {
                double inner = logMag / LogBailout;
                smooth = inner <= 1.0
                    ? iter
                    : (float)(iter + 1.0 - Math.Log(inner) * InvLog3);
            }
        }
        SmoothBuffer[idx] = smooth;

        double dMag = Math.Sqrt(dr * dr + di * di);
        // Distance estimate: |z|·log|z| / |d| (Milnor formula, generic for any
        // analytic escape-time iteration with closed-form derivative).
        float dist = dMag > 1e-30
            ? (float)(mag * Math.Log(Math.Max(mag, 1.0 + 1e-12)) / dMag)
            : 0f;
        DistanceBuffer[idx] = dist;

        // Normal vector u + i·v where u = Re(z·conj(d)), v = Im(z·conj(d)).
        double u = zr * dr + zi * di;
        double v = zi * dr - zr * di;
        double m = Math.Sqrt(u * u + v * v);
        float nx, ny;
        if (m > 1e-30) { nx = (float)(u / m); ny = (float)(v / m); }
        else            { nx = 0; ny = 0; }
        NormalXBuffer[idx] = nx;
        NormalYBuffer[idx] = ny;

        float fzr = (float)zr, fzi = (float)zi;
        float fdr = (float)dr, fdi = (float)di;
        FinalZrBuffer[idx] = fzr;
        FinalZiBuffer[idx] = fzi;
        FinalDrBuffer[idx] = fdr;
        FinalDiBuffer[idx] = fdi;

        ColorBuffer[idx] = (uint)map.Map(
            smooth, dist, maxIt, nx, ny, fzr, fzi, fdr, fdi);
    }

    private void FinaliseInSet(int idx, IColorMap map)
    {
        SmoothBuffer[idx] = 0f;
        DistanceBuffer[idx] = 0f;
        NormalXBuffer[idx] = 0f;
        NormalYBuffer[idx] = 0f;
        FinalZrBuffer[idx] = 0f;
        FinalZiBuffer[idx] = 0f;
        FinalDrBuffer[idx] = 0f;
        FinalDiBuffer[idx] = 0f;
        ColorBuffer[idx] = map.InSetColor;
    }

    private void EmitImmediateEscape(int idx, IColorMap map, int maxIt)
    {
        IterationBuffer[idx] = 0;
        SmoothBuffer[idx] = 0f;
        DistanceBuffer[idx] = 0f;
        NormalXBuffer[idx] = 0f;
        NormalYBuffer[idx] = 0f;
        FinalZrBuffer[idx] = 0f;
        FinalZiBuffer[idx] = 0f;
        FinalDrBuffer[idx] = 0f;
        FinalDiBuffer[idx] = 0f;
        ColorBuffer[idx] = (uint)map.Map(0f, 0f, maxIt);
    }

    // ── Tiny complex-number helpers ──────────────────────────────────────────

    private static void ComplexMul(
        double ar, double ai, double br, double bi,
        out double rr, out double ri)
    {
        rr = ar * br - ai * bi;
        ri = ar * bi + ai * br;
    }

    private static void ComplexSquare(double ar, double ai, out double rr, out double ri)
    {
        rr = ar * ar - ai * ai;
        ri = 2.0 * ar * ai;
    }
}
