// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CoquaternionMandelbrotCalculator.cs
//
// Coquaternion (split-quaternion) Mandelbrot distance-estimation raymarcher
// (#853, epic #850). Iteration t := t² + c with t, c in the 4D split algebra
// spanned by (1, i, j, k) under the relations
//
//     i² = −1,  j² = +1,  k² = +1,
//     ij = k = −ji,  jk = −i = −kj,  ki = j = −ik.
//
// This is the "swapped product table" sibling of the quaternion (Hamilton:
// i²=j²=k²=−1) and bicomplex (tessarine: i²=j²=−1, k²=+1, commutative) sets —
// see Docs/Technical/Theoretical-Fractal-RnD.md §3.2 and the Resources
// bibliography (Rochon 2000). Unlike bicomplex it is NON-commutative
// (ij = −ji); unlike quaternion two of the three imaginary units square to
// +1, giving an indefinite metric and a distinct 3D silhouette.
//
// Because squaring is t·t, every off-diagonal cross term cancels against its
// anticommuting partner (t2·t3·ij + t3·t2·ji = 0, etc.), so the squaring map
// reduces to the clean diagonal form — identical to the quaternion square
// except for the mixed real-part signs from j² = k² = +1:
//   t²_R = t1² − t2² + t3² + t4²
//   t²_i = 2·t1·t2
//   t²_j = 2·t1·t3
//   t²_k = 2·t1·t4
//
// The derivative of a non-commutative square is d(t²) = t·dt + dt·t (order
// matters); the antisymmetric cross terms again cancel in the symmetrisation,
// so the Hubbard–Douady derivative recurrence is
//   d.R := 2·(t1·d1 − t2·d2 + t3·d3 + t4·d4) + 1
//   d.i := 2·(t1·d2 + t2·d1)
//   d.j := 2·(t1·d3 + t3·d1)
//   d.k := 2·(t1·d4 + t4·d1)
//
// NOTE: the pure SPLIT-COMPLEX (2D, j²=+1) Mandelbrot degenerates to a filled
// square — the idempotents e± = (1 ± j)/2 split the iteration into two
// independent real quadratic recurrences — so it is not worth rendering. The
// coquaternion is the genuinely-structured member of the split-algebra family.
//
// First cut is CPU-only (no GPU kernel yet); slice is fixed to the k-axis
// (c = (x, y, z, CoquaternionSliceW)). Bailout uses the Euclidean |t|² for a
// well-behaved boundedness test.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class CoquaternionMandelbrotCalculator : IFractalCalculator
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

    public CoquaternionMandelbrotCalculator(int width, int height) => Resize(width, height);

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

        double sliceW = FractalParameters.CoquaternionSliceW;
        int deIter = Math.Max(2, FractalParameters.CoquaternionIterations);
        double bailout2 = Math.Max(4.0, FractalParameters.CoquaternionBailout);
        int maxSteps = Math.Max(16, FractalParameters.CoquaternionMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.CoquaternionEpsilon);

        double setRadius = 2.0;
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.CoquaternionCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.CoquaternionCameraTheta;
        double camPhi = FractalParameters.CoquaternionCameraPhi;

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

        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
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

        double[] light = Normalize3(
            Math.Sin(FractalParameters.CoquaternionLightPhi) * Math.Cos(FractalParameters.CoquaternionLightTheta),
            Math.Cos(FractalParameters.CoquaternionLightPhi),
            Math.Sin(FractalParameters.CoquaternionLightPhi) * Math.Sin(FractalParameters.CoquaternionLightTheta));

        var fx = FractalParameters.Lighting;
        VolumePaletteBaker.Bake(ref fx, ColorMap);
        var deStruct = new De(sliceW, bailout2, deIter);

        double sceneRadius = camDist + setRadius * 2.0 + 4.0;

        // Phase 4 — G-buffer for SSAO post-pass.
        float[]? depthBuf = null;
        float[]? normalBuf = null;
        if (fx.SsaoSamples > 0)
        {
            depthBuf = new float[width * height];
            normalBuf = new float[3 * width * height];
            ScreenSpacePost.ClearGBuffer(depthBuf, normalBuf);
        }
        // Phase 7 — HDR buffer for tonemap/bloom.
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

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale + panV;
            int rowBase = y * width;
            float[] hdrScratch = thinLensDof ? new float[3] : null!;

            uint ShadeRay(double ox, double oy, double oz, double dx, double dy, double dz,
                          out float hr, out float hg, out float hb)
            {
                double sx = ox, sy = oy, sz = oz, tT = 0; bool sHit = false; int sStep = 0;
                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = CoquaternionDE(sx, sy, sz, sliceW, bailout2, deIter);
                    if (dist < eps) { sHit = true; sStep = step; break; }
                    if (tT > sceneRadius) break;
                    sx += dx * dist; sy += dy * dist; sz += dz * dist; tT += dist;
                }
                if (!sHit)
                {
                    uint sky = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(dx, dy, dz, in fx)
                        : ColorMap.InSetColor;
                    hr = ((sky >> 16) & 0xFF) / 255f; hg = ((sky >> 8) & 0xFF) / 255f; hb = (sky & 0xFF) / 255f;
                    return sky;
                }
                double hn = eps * 2;
                double sn0 = CoquaternionDE(sx + hn, sy, sz, sliceW, bailout2, deIter)
                           - CoquaternionDE(sx - hn, sy, sz, sliceW, bailout2, deIter);
                double sn1 = CoquaternionDE(sx, sy + hn, sz, sliceW, bailout2, deIter)
                           - CoquaternionDE(sx, sy - hn, sz, sliceW, bailout2, deIter);
                double sn2 = CoquaternionDE(sx, sy, sz + hn, sliceW, bailout2, deIter)
                           - CoquaternionDE(sx, sy, sz - hn, sliceW, bailout2, deIter);
                var snrm = Normalize3(sn0, sn1, sn2);
                float ssmooth = (float)sStep * (192f / Math.Max(1, maxSteps)) + (float)(tT * 0.5);
                uint sbase = (uint)ColorMap.Map(ssmooth, 0f, 256, (float)snrm[0], (float)snrm[1]);
                var sinputs = new ShadingInputs(sx, sy, sz, snrm[0], snrm[1], snrm[2], dx, dy, dz, tT, 0.0, sStep, eps);
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
                    double dist = CoquaternionDE(px, py, pz, sliceW, bailout2, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
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

                double h = eps * 2;
                double n0 = CoquaternionDE(px + h, py, pz, sliceW, bailout2, deIter)
                          - CoquaternionDE(px - h, py, pz, sliceW, bailout2, deIter);
                double n1 = CoquaternionDE(px, py + h, pz, sliceW, bailout2, deIter)
                          - CoquaternionDE(px, py - h, pz, sliceW, bailout2, deIter);
                double n2 = CoquaternionDE(px, py, pz + h, sliceW, bailout2, deIter)
                          - CoquaternionDE(px, py, pz - h, sliceW, bailout2, deIter);
                var nrm = Normalize3(n0, n1, n2);

                float smooth = (float)hitStep * (192f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 0.5);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);

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

    /// <summary>P3 — concrete DE struct for the shared shading pipeline.</summary>
    public readonly struct De
        : FracturingFog.Rendering.Lighting.IDistanceEstimator,
          FracturingFog.Rendering.Lighting.IOrbitTrapEstimator
    {
        private readonly double _sliceW, _bailout2;
        private readonly int _iter;
        public De(double sliceW, double bailout2, int iter)
        { _sliceW = sliceW; _bailout2 = bailout2; _iter = iter; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => CoquaternionDE(x, y, z, _sliceW, _bailout2, _iter);

        // Origin orbit trap (roadmap S9, #391): closest the orbit passes to the
        // origin, normalized over the bailout radius.
        public double OrbitTrap(double x, double y, double z)
            => CoquaternionTrap(x, y, z, _sliceW, _bailout2, _iter);
    }

    private static double CoquaternionTrap(
        double sx, double sy, double sz, double sliceW, double bailout2, int iter)
    {
        // Pixel walks (1, i, j); the slice constant rides on k.
        double c1 = sx, c2 = sy, c3 = sz, c4 = sliceW;
        double t1 = 0.0, t2 = 0.0, t3 = 0.0, t4 = 0.0;
        double minR2 = double.MaxValue;
        for (int i = 0; i < iter; i++)
        {
            double nt1 = t1 * t1 - t2 * t2 + t3 * t3 + t4 * t4;
            double nt2 = 2.0 * t1 * t2;
            double nt3 = 2.0 * t1 * t3;
            double nt4 = 2.0 * t1 * t4;
            t1 = nt1 + c1; t2 = nt2 + c2; t3 = nt3 + c3; t4 = nt4 + c4;
            double r2 = t1 * t1 + t2 * t2 + t3 * t3 + t4 * t4;
            if (r2 < minR2) minR2 = r2;
            if (r2 > bailout2) break;
        }
        double bail = Math.Sqrt(Math.Max(bailout2, 1e-9));
        return Math.Clamp(Math.Sqrt(minR2) / bail, 0.0, 1.0);
    }

    private static double CoquaternionDE(
        double sx, double sy, double sz, double sliceW, double bailout2, int iter)
    {
        double c1 = sx, c2 = sy, c3 = sz, c4 = sliceW;

        // t starts at zero (Mandelbrot membership test).
        double t1 = 0.0, t2 = 0.0, t3 = 0.0, t4 = 0.0;
        // dt/dc starts at zero because t_0 = 0.
        double d1 = 0.0, d2 = 0.0, d3 = 0.0, d4 = 0.0;

        for (int i = 0; i < iter; i++)
        {
            // dt := t·dt + dt·t + 1. The antisymmetric cross terms cancel in
            // the symmetrisation, so only the diagonal survives (see header).
            double nd1 = t1 * d1 - t2 * d2 + t3 * d3 + t4 * d4;
            double nd2 = t1 * d2 + t2 * d1;
            double nd3 = t1 * d3 + t3 * d1;
            double nd4 = t1 * d4 + t4 * d1;
            d1 = 2.0 * nd1 + 1.0;
            d2 = 2.0 * nd2;
            d3 = 2.0 * nd3;
            d4 = 2.0 * nd4;

            // t := t² + c. Coquaternion squaring (i²=−1, j²=k²=+1); cross terms
            // cancel under anticommutativity, leaving the clean diagonal form.
            double nt1 = t1 * t1 - t2 * t2 + t3 * t3 + t4 * t4;
            double nt2 = 2.0 * t1 * t2;
            double nt3 = 2.0 * t1 * t3;
            double nt4 = 2.0 * t1 * t4;
            t1 = nt1 + c1;
            t2 = nt2 + c2;
            t3 = nt3 + c3;
            t4 = nt4 + c4;

            double r2 = t1 * t1 + t2 * t2 + t3 * t3 + t4 * t4;
            if (r2 > bailout2) break;
        }

        double t2sum = t1 * t1 + t2 * t2 + t3 * t3 + t4 * t4;
        double d2sum = d1 * d1 + d2 * d2 + d3 * d3 + d4 * d4;
        if (d2sum < 1e-30) return 0.0;
        if (t2sum < 1.0) return 0.0;
        double tMag = Math.Sqrt(t2sum);
        double dMag = Math.Sqrt(d2sum);
        return 0.5 * tMag * Math.Log(tMag) / dMag;
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
