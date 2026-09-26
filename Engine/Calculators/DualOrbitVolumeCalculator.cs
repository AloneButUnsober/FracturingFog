// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitVolumeCalculator.cs (#972, epic #850 / #863)
//
// Dual-orbit escape-geometry VOLUME. The user's original experiment — two fixed
// seeds (the critical z0 = 0 and c0 = (x, y, 0)), a shared parameter s whose
// s.y is fixed while s.x is swept — run for EVERY c0. World coordinates:
//
//     X = c.x,   Z = c.y,   Y (up) = s.x − DualOrbitVolumeSXCenter,
//     s = s.x + i·DualOrbitVolumeSY.
//
// The solid is { (c, s.x) : the c-orbit of u → u² + s stays bounded } — a 3D slice
// of the 4D (z0, s) space of quadratic dynamics. Every horizontal layer is the
// filled Julia set K_s; the c = 0 column is the Mandelbrot line at Im s = s.y.
// The critical orbit (seed 0) does not depend on c, so it is constant per layer:
// it colours the layer (connected vs Cantor Julia set / critical escape level)
// while the c-orbit shapes the surface — the dual-orbit reading in 3D.
//
// Why not the quaternion or component-wise maps (verified numerically, §3.6):
// Hamilton q² + C is rotation-covariant, so every quaternion object is a surface
// of revolution; the component-wise map is separable, so its volume is an
// axis-aligned product. The complex map couples c.x and c.y and is the one with
// genuine 3D fractal structure.
//
// Distance estimate (analytic, Hubbard–Douady form). With u_n(c, s):
//     a = ∂u/∂c :  a_0 = 1,  a ← 2u·a
//     b = ∂u/∂s :  b_0 = 0,  b ← 2u·b + 1
// Moving along X or Z changes u by a·dX or i·a·dZ (holomorphic in c); moving
// along Y changes s.x only, so u by b·dY. The gradient of the escape potential
// G = lim log|u_n| / 2^n is therefore
//     |∇G| = sqrt(|a|² + (Re(ū·b) / |u|)²) / (|u|·2^n)
// and DE = ½·G / |∇G| = ½·|u|·ln|u| / sqrt(|a|² + (Re(ū·b)/|u|)²).
//
// Symmetry: u → u² + s is even in u, so the c-orbits of ±c coincide after one
// step and the volume is invariant under (X, Z) → (−X, −Z).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class DualOrbitVolumeCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>P2 — low-res interactive preview. See Mandelbulb for contract.</summary>
    public bool LowResPreview { get; set; } = false;

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 96;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // Iterations for the per-layer critical orbit (colour only).
    private const int CriticalIter = 256;
    // Under-relaxed march: the DE of thin Julia sheets overestimates slightly;
    // full steps tunnel through them (speckle).
    private const double StepFactor = 0.85;

    public DualOrbitVolumeCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = 256;
        int fullW = Width;
        int fullH = Height;
        bool lowRes = LowResPreview;
        double lrScale = lowRes ? Math.Clamp(FractalParameters.LowResPreviewScale, 0.25, 1.0) : 1.0;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(fullW, fullH, lrScale);
        int width = dims.Width;
        int height = dims.Height;
        uint[] renderBuffer = lowRes ? new uint[width * height] : ColorBuffer;

        var fp = FractalParameters;
        double sxCenter = fp.DualOrbitVolumeSXCenter;
        double sy = fp.DualOrbitVolumeSY;
        int deIter = Math.Max(4, fp.DualOrbitVolumeIterations);
        double bail = Math.Max(4.0, fp.DualOrbitVolumeBailout);
        double bailout2 = bail * bail;
        int maxSteps = Math.Max(16, fp.DualOrbitVolumeMaxSteps);
        double eps = Math.Max(1e-5, fp.DualOrbitVolumeEpsilon);
        var colorSource = fp.DualOrbitVolumeColor;
        double halfH = Math.Max(0.05, fp.DualOrbitVolumeHalfHeight);

        double setRadius = 2.2;
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = fp.DualOrbitVolumeCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = fp.DualOrbitVolumeCameraTheta;
        double camPhi = fp.DualOrbitVolumeCameraPhi;

        double camPX = camDist * Math.Sin(camPhi) * Math.Cos(camTheta);
        double camPY = camDist * Math.Cos(camPhi);
        double camPZ = camDist * Math.Sin(camPhi) * Math.Sin(camTheta);

        double[] fwd = Normalize3(-camPX, -camPY, -camPZ);
        double[] worldUp = { 0, 1, 0 };
        double[] right = Normalize3(
            fwd[1] * worldUp[2] - fwd[2] * worldUp[1],
            fwd[2] * worldUp[0] - fwd[0] * worldUp[2],
            fwd[0] * worldUp[1] - fwd[1] * worldUp[0]);
        double[] up = {
            right[1] * fwd[2] - right[2] * fwd[1],
            right[2] * fwd[0] - right[0] * fwd[2],
            right[0] * fwd[1] - right[1] * fwd[0],
        };

        double eyeOffset = fp.Lighting.StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camPX += right[0] * eyeOffset;
            camPY += right[1] * eyeOffset;
            camPZ += right[2] * eyeOffset;
        }

        double aspect = (double)width / height;
        double fovBase = Math.Tan(0.5 * Math.PI / 3.0);
        double zoomLensFactor = rawCamDist >= camDistFloor
            ? 1.0
            : Math.Max(0.05, rawCamDist / camDistFloor);
        double fovScale = fovBase * zoomLensFactor;

        double panU = CenterX;
        double panV = -CenterY;

        var fx = fp.Lighting;
        VolumePaletteBaker.Bake(ref fx, ColorMap);
        var deStruct = new De(sxCenter, sy, bailout2, deIter, halfH);

        double sceneRadius = camDist + setRadius * 2.0 + 4.0;

        float[]? depthBuf = null;
        float[]? normalBuf = null;
        if (fx.SsaoSamples > 0)
        {
            depthBuf = new float[width * height];
            normalBuf = new float[3 * width * height];
            ScreenSpacePost.ClearGBuffer(depthBuf, normalBuf);
        }
        float[]? hdrBuf = null;
        bool wantPost = fx.ToneMap != ToneMapOperator.None || fx.BloomStrength > 0;
        if (wantPost)
        {
            hdrBuf = new float[3 * width * height];
            ScreenSpacePost.ClearHdrBuffer(hdrBuf);
        }

        bool thinLensDof = ThinLensDof.IsActive(in fx);
        int dofN = ThinLensDof.SampleCount(in fx);
        double dofFocus = ThinLensDof.FocusDistance(in fx, camDist);
        double dofAperture = fx.DofAperture;

        // Surface base colour at a hit point, per the selected source.
        uint SurfaceColor(double px, double py, double pz, double[] nrm, int hitStep, double tTotal)
        {
            float v;
            switch (colorSource)
            {
                case DualOrbitVolumeColor.CriticalLayer:
                {
                    double smoothZ = CriticalSmooth(py + sxCenter, sy, bail);
                    // Bounded critical orbit (s in M, connected layer) → palette end.
                    v = smoothZ < 0 ? 255f : (float)Math.Min(250.0, smoothZ * 6.0);
                    break;
                }
                case DualOrbitVolumeColor.ExternalAngle:
                {
                    double t = DualOrbitEscapeCalculator.ExternalAngleTurns(px, pz, py + sxCenter, sy, 400, 1e6);
                    v = double.IsNaN(t) ? 255f : (float)(t * 255.0);
                    break;
                }
                default:
                    v = (float)hitStep * (192f / Math.Max(1, maxSteps)) + (float)(tTotal * 0.5);
                    break;
            }
            return (uint)ColorMap.Map(v, 0f, 256, (float)nrm[0], (float)nrm[1]);
        }

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale + panV;
            int rowBase = y * width;
            float[] hdrScratch = thinLensDof ? new float[3] : null!;

            uint ShadeRay(double ox, double oy, double oz, double dx, double dy, double dz,
                          out float hr, out float hg, out float hb)
            {
                double sx = ox, sy2 = oy, sz = oz, tT = 0; bool sHit = false; int sStep = 0;
                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = VolumeDE(sx, sy2, sz, sxCenter, sy, bailout2, deIter, halfH);
                    if (dist < eps) { sHit = true; sStep = step; break; }
                    if (tT > sceneRadius) break;
                    dist *= StepFactor; sx += dx * dist; sy2 += dy * dist; sz += dz * dist; tT += dist;
                }
                if (!sHit)
                {
                    uint sky = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(dx, dy, dz, in fx)
                        : ColorMap.InSetColor;
                    hr = ((sky >> 16) & 0xFF) / 255f; hg = ((sky >> 8) & 0xFF) / 255f; hb = (sky & 0xFF) / 255f;
                    return sky;
                }
                var snrm = Normal(sx, sy2, sz, eps * 2, sxCenter, sy, bailout2, deIter, halfH);
                uint sbase = SurfaceColor(sx, sy2, sz, snrm, sStep, tT);
                var sinputs = new ShadingInputs(sx, sy2, sz, snrm[0], snrm[1], snrm[2], dx, dy, dz, tT, 0.0, sStep, eps);
                ScreenSpacePost.ClearHdrBuffer(hdrScratch);
                uint col = ShadingPipeline.Shade<De>(
                    in sinputs, sbase, in fx, in deStruct, true, 0, null, null, hdrScratch);
                if (!float.IsNaN(hdrScratch[0])) { hr = hdrScratch[0]; hg = hdrScratch[1]; hb = hdrScratch[2]; }
                else { hr = ((col >> 16) & 0xFF) / 255f; hg = ((col >> 8) & 0xFF) / 255f; hb = (col & 0xFF) / 255f; }
                return col;
            }

            for (int x = 0; x < width; x++)
            {
                double u = (2.0 * (x + 0.5) / width - 1.0) * fovScale * aspect + panU;
                double rdx = right[0] * u + up[0] * v + fwd[0];
                double rdy = right[1] * u + up[1] * v + fwd[1];
                double rdz = right[2] * u + up[2] * v + fwd[2];
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn[0]; rdy = dn[1]; rdz = dn[2];

                if (thinLensDof)
                {
                    ThinLensDof.AccumulatePixel(
                        x, y, rowBase + x, dofN, 13,
                        camPX, camPY, camPZ, rdx, rdy, rdz,
                        right[0], right[1], right[2], up[0], up[1], up[2],
                        dofFocus, dofAperture, renderBuffer, hdrBuf, ShadeRay);
                    continue;
                }

                double px = camPX, py = camPY, pz = camPZ;
                double tTotal = 0;
                bool hit = false;
                int hitStep = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = VolumeDE(px, py, pz, sxCenter, sy, bailout2, deIter, halfH);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    dist *= StepFactor;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit)
                {
                    renderBuffer[idx] = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx)
                        : ColorMap.InSetColor;
                    continue;
                }

                var nrm = Normal(px, py, pz, eps * 2, sxCenter, sy, bailout2, deIter, halfH);
                uint baseColor = SurfaceColor(px, py, pz, nrm, hitStep, tTotal);

                var inputs = new ShadingInputs(
                    px, py, pz, nrm[0], nrm[1], nrm[2],
                    rdx, rdy, rdz, tTotal, 0.0, hitStep, eps);
                renderBuffer[idx] = ShadingPipeline.Shade<De>(
                    in inputs, baseColor, in fx, in deStruct, true,
                    idx, depthBuf, normalBuf, hdrBuf);
            }
        });

        ScreenSpacePost.BeginGpuFrame(renderBuffer, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null && !thinLensDof)
            ScreenSpacePost.ApplySsao(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        if (hdrBuf is not null && depthBuf is not null && !thinLensDof)
            ScreenSpacePost.ApplyHdrDof(hdrBuf, depthBuf, width, height, in fx);
        if (hdrBuf is not null)
            ScreenSpacePost.ApplyToneMapBloom(renderBuffer, hdrBuf, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null && !thinLensDof)
            ScreenSpacePost.ApplyEdgeInk(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        ScreenSpacePost.EndGpuFrame(in fx);

        if (lowRes)
            FracturingFog.Rendering.LowResPreview.UpscaleNearest(
                renderBuffer, width, height, ColorBuffer, fullW, fullH);

        ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
    }

    /// <summary>Concrete DE struct for the shared shading pipeline.</summary>
    public readonly struct De
        : FracturingFog.Rendering.Lighting.IDistanceEstimator,
          FracturingFog.Rendering.Lighting.IOrbitTrapEstimator
    {
        private readonly double _sxCenter, _sy, _bailout2, _halfHeight;
        private readonly int _iter;
        public De(double sxCenter, double sy, double bailout2, int iter, double halfHeight)
        { _sxCenter = sxCenter; _sy = sy; _bailout2 = bailout2; _iter = iter; _halfHeight = halfHeight; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => VolumeDE(x, y, z, _sxCenter, _sy, _bailout2, _iter, _halfHeight);

        // Origin orbit trap (roadmap S9, #391): closest the c-orbit passes to the
        // origin, normalized over the bailout radius.
        public double OrbitTrap(double x, double y, double z)
            => VolumeTrap(x, y, z, _sxCenter, _sy, _bailout2, _iter);
    }

    /// <summary>Distance estimate to the volume at world point (x, y, z):
    /// c = x + i·z, s = (y + sxCenter) + i·sy, c-orbit u0 = c. 0 inside (orbit
    /// bounded within <paramref name="iter"/>), clipped to |y| ≤ halfHeight. See
    /// the header for the derivation.</summary>
    public static double VolumeDE(double x, double y, double z,
        double sxCenter, double sy, double bailout2, int iter, double halfHeight)
    {
        // CSG intersection with the slab |y| ≤ halfHeight: beyond the Mandelbrot
        // line's extent the layers are Cantor dust reaching to infinity.
        double slab = Math.Abs(y) - halfHeight;
        double de = FractalDE(x, y, z, sxCenter, sy, bailout2, iter);
        return Math.Max(de, slab);
    }

    private static double FractalDE(double x, double y, double z,
        double sxCenter, double sy, double bailout2, int iter)
    {
        double sr = y + sxCenter, si = sy;
        double ur = x, ui = z;          // u0 = c
        double ar = 1.0, ai = 0.0;      // a = ∂u/∂c
        double br = 0.0, bi = 0.0;      // b = ∂u/∂s
        for (int i = 0; i < iter; i++)
        {
            // Derivatives use u before it is updated.
            double nar = 2.0 * (ur * ar - ui * ai), nai = 2.0 * (ur * ai + ui * ar);
            double nbr = 2.0 * (ur * br - ui * bi) + 1.0, nbi = 2.0 * (ur * bi + ui * br);
            ar = nar; ai = nai; br = nbr; bi = nbi;
            double nur = ur * ur - ui * ui + sr;
            ui = 2.0 * ur * ui + si;
            ur = nur;
            if (ur * ur + ui * ui > bailout2) break;
        }
        double r2 = ur * ur + ui * ui;
        if (r2 < 4.0) return 0.0;
        double r = Math.Sqrt(r2);
        double reUb = (ur * br + ui * bi) / r;   // Re(ū·b) / |u|
        double g2 = ar * ar + ai * ai + reUb * reUb;
        if (g2 < 1e-30) return 0.0;
        return 0.5 * r * Math.Log(r) / Math.Sqrt(g2);
    }

    private static double VolumeTrap(double x, double y, double z,
        double sxCenter, double sy, double bailout2, int iter)
    {
        double sr = y + sxCenter, si = sy;
        double ur = x, ui = z;
        double minR2 = ur * ur + ui * ui;
        for (int i = 0; i < iter; i++)
        {
            double nur = ur * ur - ui * ui + sr;
            ui = 2.0 * ur * ui + si;
            ur = nur;
            double r2 = ur * ur + ui * ui;
            if (r2 < minR2) minR2 = r2;
            if (r2 > bailout2) break;
        }
        double bail = Math.Sqrt(Math.Max(bailout2, 1e-9));
        return Math.Clamp(Math.Sqrt(minR2) / bail, 0.0, 1.0);
    }

    /// <summary>Smooth escape count of the critical orbit (seed 0) at s; −1 when it
    /// stays bounded (s in the Mandelbrot set — the layer's Julia set is connected).</summary>
    public static double CriticalSmooth(double sr, double si, double bailout)
    {
        double b2 = bailout * bailout, logB = Math.Log(bailout);
        double ur = 0.0, ui = 0.0;
        for (int n = 0; n < CriticalIter; n++)
        {
            double r2 = ur * ur + ui * ui;
            if (r2 > b2)
                return n - Math.Log(Math.Log(r2) * 0.5 / logB) / Math.Log(2.0);
            double nur = ur * ur - ui * ui + sr;
            ui = 2.0 * ur * ui + si;
            ur = nur;
        }
        return -1.0;
    }

    private static double[] Normal(double px, double py, double pz, double h,
        double sxCenter, double sy, double bailout2, int iter, double halfHeight)
    {
        double n0 = VolumeDE(px + h, py, pz, sxCenter, sy, bailout2, iter, halfHeight)
                  - VolumeDE(px - h, py, pz, sxCenter, sy, bailout2, iter, halfHeight);
        double n1 = VolumeDE(px, py + h, pz, sxCenter, sy, bailout2, iter, halfHeight)
                  - VolumeDE(px, py - h, pz, sxCenter, sy, bailout2, iter, halfHeight);
        double n2 = VolumeDE(px, py, pz + h, sxCenter, sy, bailout2, iter, halfHeight)
                  - VolumeDE(px, py, pz - h, sxCenter, sy, bailout2, iter, halfHeight);
        return Normalize3(n0, n1, n2);
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
