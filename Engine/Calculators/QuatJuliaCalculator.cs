// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// QuatJuliaCalculator.cs
//
// Quaternion Julia distance-estimation raymarcher (Hart, Sandin & Kauffman
// 1989). Iteration q := q² + c with q, c ∈ ℍ (Hamilton quaternions); the
// renderer marches a 3D slice — pixel (x, y, z) ↦ q = (x, y, z, sliceW) —
// of the full 4D set. DE uses the Hubbard–Douady estimator
//   DE = 0.5 · |q| · ln|q| / |dq|
// with the orbital derivative dq tracked as a quaternion through the
// Hamilton product (dq' = 2·q·dq for the squaring map). Camera / lighting
// plumbing mirrors MandelboxCalculator so the User Bulb-style orbit camera,
// theta/phi rotation and minimap suppression all match.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class QuatJuliaCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // P7b — lazily-constructed GPU calculator. See MandelbulbCalculator for contract.
    private QJuliaGpuCalculator? _gpu;

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

    public QuatJuliaCalculator(int width, int height) => Resize(width, height);

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

        double cx = FractalParameters.QJuliaCX;
        double cy = FractalParameters.QJuliaCY;
        double cz = FractalParameters.QJuliaCZ;
        double cw = FractalParameters.QJuliaCW;
        double sliceW = FractalParameters.QJuliaSliceW;
        int deIter = Math.Max(2, FractalParameters.QJuliaIterations);
        double bailout2 = Math.Max(4.0, FractalParameters.QJuliaBailout);
        int maxSteps = Math.Max(16, FractalParameters.QJuliaMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.QJuliaEpsilon);

        // Quaternion Julia at typical c lives inside a ball of radius ≈ 1.6.
        // Match the Mandelbox/KIFS camera-floor pattern so high zoom narrows
        // FOV instead of plunging the camera into the set.
        double setRadius = 2.0;
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.QJuliaCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.QJuliaCameraTheta;
        double camPhi = FractalParameters.QJuliaCameraPhi;

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

        // Phase 20b — true per-eye camera offset along the right basis.
        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camPX += right[0] * eyeOffset;
            camPY += right[1] * eyeOffset;
            camPZ += right[2] * eyeOffset;
        }

        double aspect = (double)width / height;
        double fovBase = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV
        double zoomLensFactor = rawCamDist >= camDistFloor
            ? 1.0
            : Math.Max(0.05, rawCamDist / camDistFloor);
        double fovScale = fovBase * zoomLensFactor;

        double panU = CenterX;
        double panV = -CenterY;

        double[] light = Normalize3(
            Math.Sin(FractalParameters.QJuliaLightPhi) * Math.Cos(FractalParameters.QJuliaLightTheta),
            Math.Cos(FractalParameters.QJuliaLightPhi),
            Math.Sin(FractalParameters.QJuliaLightPhi) * Math.Sin(FractalParameters.QJuliaLightTheta));

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        // Vol-color slice D (#180) — bake the active theme gradient for the
        // volumetric palette remap (no-op unless VolumePaletteStrength > 0).
        VolumePaletteBaker.Bake(ref fx, ColorMap);
        var deStruct = new De(sliceW, cx, cy, cz, cw, bailout2, deIter);

        // Hoisted for shared use by GPU dispatch + CPU path.
        double sceneRadius = camDist + setRadius * 2.0 + 4.0;

        // P7b — opt-in GPU raymarch path (cheap-palette shading). See
        // MandelbulbCalculator for the FX-drop trade-off + P7c lift plan.
        if (fx.UseGpuRender && !lowRes)
        {
            var rp = new GpuRaymarchParams
            {
                Width = width, Height = height,
                CamX = camPX, CamY = camPY, CamZ = camPZ,
                TargetX = 0, TargetY = 0, TargetZ = 0,
                FwdX = fwd[0], FwdY = fwd[1], FwdZ = fwd[2],
                RightX = right[0], RightY = right[1], RightZ = right[2],
                UpX = up[0], UpY = up[1], UpZ = up[2],
                FovScale = fovScale, Aspect = aspect,
                PanU = panU, PanV = panV,
                LightX = light[0], LightY = light[1], LightZ = light[2],
                MaxSteps = maxSteps, Eps = eps,
                CullRadiusSq = 0.0,
                InSetColor = ColorMap.InSetColor,
            };
            var qp = new QJuliaGpuParams
            {
                CX = cx, CY = cy, CZ = cz, CW = cw,
                SliceW = sliceW,
                Bailout2 = bailout2, DEIter = deIter,
                SceneRadius = sceneRadius,
            };
            var sp = GpuShadingParams.Build(in fx);
            _gpu ??= new QJuliaGpuCalculator();
            if (_gpu.Render(renderBuffer, rp, sp, qp, fx.VolumePalette))
            {
                // #84 — GPU raymarch skips the CPU post stack; draw the debug
                // HUD directly so the light compass still appears on GPU frames.
                ScreenSpacePost.ApplyDebugHud(renderBuffer, width, height, in fx);
                return;
            }
        }

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

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale + panV;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double u = (2.0 * (x + 0.5) / width - 1.0) * fovScale * aspect + panU;
                double rdx = right[0] * u + up[0] * v + fwd[0];
                double rdy = right[1] * u + up[1] * v + fwd[1];
                double rdz = right[2] * u + up[2] * v + fwd[2];
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn[0]; rdy = dn[1]; rdz = dn[2];

                double px = camPX, py = camPY, pz = camPZ;
                double tTotal = 0;
                bool hit = false;
                int hitStep = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = QuatJuliaDE(px, py, pz, sliceW,
                        cx, cy, cz, cw, bailout2, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit)
                {
                    // Ray-miss → sky backdrop when toggle on; InSetColor off (see MandelbulbCalculator).
                    renderBuffer[idx] = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx)
                        : ColorMap.InSetColor;
                    continue;
                }

                double h = eps * 2;
                double n0 = QuatJuliaDE(px + h, py, pz, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px - h, py, pz, sliceW, cx, cy, cz, cw, bailout2, deIter);
                double n1 = QuatJuliaDE(px, py + h, pz, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px, py - h, pz, sliceW, cx, cy, cz, cw, bailout2, deIter);
                double n2 = QuatJuliaDE(px, py, pz + h, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px, py, pz - h, sliceW, cx, cy, cz, cw, bailout2, deIter);
                var nrm = Normalize3(n0, n1, n2);

                float smooth = (float)hitStep * (192f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 0.5);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);

                // Phase 2 — shading via shared pipeline.
                var inputs = new ShadingInputs(
                    px, py, pz, nrm[0], nrm[1], nrm[2],
                    rdx, rdy, rdz, tTotal, 0.0, hitStep, eps);
                renderBuffer[idx] = ShadingPipeline.Shade<De>(
                    in inputs, baseColor, in fx, in deStruct, true,
                    idx, depthBuf, normalBuf, hdrBuf);
            }
        });

        ScreenSpacePost.BeginGpuFrame(renderBuffer, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplySsao(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        if (hdrBuf is not null && depthBuf is not null)
            ScreenSpacePost.ApplyHdrDof(hdrBuf, depthBuf, width, height, in fx);
        if (hdrBuf is not null)
            ScreenSpacePost.ApplyToneMapBloom(renderBuffer, hdrBuf, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplyEdgeInk(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        ScreenSpacePost.EndGpuFrame(in fx);

        if (lowRes)
            FracturingFog.Rendering.LowResPreview.UpscaleNearest(
                renderBuffer, width, height, ColorBuffer, fullW, fullH);

        // #84 — light-direction compass / param bars / scene clock. Standalone
        // final pass on the composited full-res buffer so it survives both the
        // low-res upscale and the GPU-raymarch early-out. Self-guards on flags.
        ScreenSpacePost.ApplyDebugHud(ColorBuffer, fullW, fullH, in fx);
    }

    /// <summary>
    /// Hubbard–Douady DE for the quaternion squaring map. Per iter:
    ///   dq := 2 · q · dq    (Hamilton product, derivative of q²)
    ///   q  := q² + c
    /// Exit when |q|² &gt; bailout (orbit has escaped). Distance estimate:
    ///   DE = 0.5 · |q| · ln|q| / |dq|.
    /// Quaternion components are stored as (W, X, Y, Z) where Hamilton
    /// product is the standard (a + bi + cj + dk)·(e + fi + gj + hk) form.
    /// </summary>
    /// <summary>P3 — concrete DE struct.</summary>
    public readonly struct De : FracturingFog.Rendering.Lighting.IDistanceEstimator
    {
        private readonly double _sliceW, _cx, _cy, _cz, _cw, _bailout2;
        private readonly int _iter;
        public De(double sliceW, double cx, double cy, double cz, double cw, double bailout2, int iter)
        { _sliceW = sliceW; _cx = cx; _cy = cy; _cz = cz; _cw = cw; _bailout2 = bailout2; _iter = iter; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => QuatJuliaDE(x, y, z, _sliceW, _cx, _cy, _cz, _cw, _bailout2, _iter);
    }

    private static double QuatJuliaDE(
        double sx, double sy, double sz, double sw,
        double cw_x, double cw_y, double cw_z, double cw_w,
        double bailout2, int iter)
    {
        // Pack pixel slice as quaternion. Convention: (X, Y, Z, W) — same
        // axis labels as the FractalParameters.QJuliaCX/Y/Z/W fields.
        double qx = sx, qy = sy, qz = sz, qw = sw;
        // dq starts at (1, 0, 0, 0) — the identity orbital derivative
        // wrt q at the seed.
        double dx = 1.0, dy = 0.0, dz = 0.0, dw = 0.0;

        for (int i = 0; i < iter; i++)
        {
            // dq := 2 · q · dq. Hamilton product q·dq with quaternion
            // components ordered (X, Y, Z, W) — derived from the canonical
            // (W + Xi + Yj + Zk) form by re-labelling:
            //   a = qx, b = qy, c = qz, d = qw   (q)
            //   e = dx, f = dy, g = dz, h = dw   (dq)
            //   (a+bi+cj+dk)(e+fi+gj+hk) where the first slot is "real"
            //   in our (X, Y, Z, W) packing, so X plays the role of W.
            double ndx = qx * dx - qy * dy - qz * dz - qw * dw;
            double ndy = qx * dy + qy * dx + qz * dw - qw * dz;
            double ndz = qx * dz - qy * dw + qz * dx + qw * dy;
            double ndw = qx * dw + qy * dz - qz * dy + qw * dx;
            dx = 2.0 * ndx; dy = 2.0 * ndy; dz = 2.0 * ndz; dw = 2.0 * ndw;

            // q := q² + c. Same packing: q·q under the (X, Y, Z, W) rule.
            double nqx = qx * qx - qy * qy - qz * qz - qw * qw;
            double nqy = 2.0 * qx * qy;
            double nqz = 2.0 * qx * qz;
            double nqw = 2.0 * qx * qw;
            qx = nqx + cw_x;
            qy = nqy + cw_y;
            qz = nqz + cw_z;
            qw = nqw + cw_w;

            double r2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (r2 > bailout2) break;
        }

        double q2 = qx * qx + qy * qy + qz * qz + qw * qw;
        double d2 = dx * dx + dy * dy + dz * dz + dw * dw;
        if (d2 < 1e-30) return 0.0;
        if (q2 < 1.0) return 0.0; // inside / converging — surface hit.
        double qMag = Math.Sqrt(q2);
        double dMag = Math.Sqrt(d2);
        return 0.5 * qMag * Math.Log(qMag) / dMag;
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
