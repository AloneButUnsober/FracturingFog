// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbulbCalculator.cs
//
// CPU distance-estimation raymarcher for the Mandelbulb (3D Mandelbrot
// analogue, triplex power-N formula). Parallel-scanline render. Output color
// blended from surface normal + iteration escape count via the active
// IColorMap. Slow vs GPU compute, but interactive at 800×600.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class MandelbulbCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>P2 — when true, render at <c>FractalParameters.LowResPreviewScale</c>
    /// of full resolution then nearest-upscale into ColorBuffer. Host toggles
    /// this during interactive rotate/pan/zoom and clears it for the deferred
    /// full-res render.</summary>
    public bool LowResPreview { get; set; } = false;

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 96;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // P7a — lazily constructed GPU calculator. Borrows GpuAcceleratorHost's
    // shared accelerator; Dispose is a no-op for the host so leaving this
    // null-or-set is safe across resize / parameter changes.
    private MandelbulbGpuCalculator? _gpu;

    public MandelbulbCalculator(int width, int height) => Resize(width, height);

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
        // P2 — low-res preview. Render at scaled-down dims, nearest-upscale at end.
        bool lowRes = LowResPreview;
        double lrScale = lowRes ? Math.Clamp(FractalParameters.LowResPreviewScale, 0.25, 1.0) : 1.0;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(fullW, fullH, lrScale);
        int width = dims.Width;
        int height = dims.Height;
        uint[] renderBuffer = lowRes ? new uint[width * height] : ColorBuffer;
        double power = FractalParameters.BulbPower;
        int deIter = Math.Max(2, FractalParameters.BulbIterations);
        int maxSteps = Math.Max(16, FractalParameters.BulbMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.BulbEpsilon);

        // Camera: orbit around origin. CenterX/Y act as a 2D screen-space pan
        // on top so MainForm's pan logic still drags the image.
        double camDist = FractalParameters.BulbCameraDistance / Math.Max(0.05, Zoom);
        double camTheta = FractalParameters.BulbCameraTheta;
        double camPhi = FractalParameters.BulbCameraPhi;

        double camX = camDist * Math.Sin(camPhi) * Math.Cos(camTheta);
        double camY = camDist * Math.Cos(camPhi);
        double camZ = camDist * Math.Sin(camPhi) * Math.Sin(camTheta);

        // Camera basis: look at origin.
        double[] fwd = Normalize3(-camX, -camY, -camZ);
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

        // Phase 20b — true per-eye stereo. RenderTrueStereo sets the transient
        // EyeOffset to ±IPD/2 before each Calculate; shift camera origin along
        // the right basis. Default 0 = mono (legacy bit-identical).
        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camX += right[0] * eyeOffset;
            camY += right[1] * eyeOffset;
            camZ += right[2] * eyeOffset;
        }

        double aspect = (double)width / height;
        double fovScale = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV

        // Screen-space pan (CenterX/Y in NDC-ish units). Y is negated so that
        // dragging UP (which makes CenterY positive in Mandelbrot pan logic)
        // shifts content UP rather than down — matches every other calculator.
        double panU = CenterX;
        double panV = -CenterY;

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        // Legacy BulbLightTheta/Phi defaults match LightingFxData.CreateDefault()
        // so freshly-opened scenes look identical; saved regions that customised
        // the legacy fields will reflect the Lighting struct values they were
        // saved under (Phase 9 region preset captures Lighting too).
        var fx = FractalParameters.Lighting;

        // P3 — concrete DE struct so Shade<MandelbulbDe> devirtualizes every
        // Evaluate call site (AO inner loop, soft shadow march, reflection
        // march, volumetric in-scatter). ~8–15 % raymarch speedup vs the
        // legacy DistanceEstimator delegate path.
        var deStruct = new MandelbulbDe(power, deIter);

        // P7a — opt-in GPU raymarch path. Cheap-palette shading only (no full
        // ShadingPipeline lift until P7c), so SSAO / tonemap / bloom / shadow
        // / AO / edge / DoF / volumetric all silently drop on the GPU branch.
        // Caller toggles via fx.UseGpuRender when they want raw speed and
        // accept the visual trade. Skipped for lowRes since the CPU low-res
        // preview is already fast and runs the full FX stack.
        if (fx.UseGpuRender && !lowRes)
        {
            double lightX = Math.Sin(fx.Light1.Phi) * Math.Cos(fx.Light1.Theta);
            double lightY = Math.Cos(fx.Light1.Phi);
            double lightZ = Math.Sin(fx.Light1.Phi) * Math.Sin(fx.Light1.Theta);
            var rp = new GpuRaymarchParams
            {
                Width = width, Height = height,
                CamX = camX, CamY = camY, CamZ = camZ,
                TargetX = 0, TargetY = 0, TargetZ = 0,
                FwdX = fwd[0], FwdY = fwd[1], FwdZ = fwd[2],
                RightX = right[0], RightY = right[1], RightZ = right[2],
                UpX = up[0], UpY = up[1], UpZ = up[2],
                FovScale = fovScale, Aspect = aspect,
                PanU = panU, PanV = panV,
                LightX = lightX, LightY = lightY, LightZ = lightZ,
                MaxSteps = maxSteps, Eps = eps,
                CullRadiusSq = 0.0,  // No sphere clip — CPU path bails on tTotal>12 instead
                InSetColor = ColorMap.InSetColor,
            };
            var bp = new MandelbulbGpuParams
            {
                Power = power, DEIter = deIter, Bailout = 2.0,
                SceneRadius = 12.0,
            };
            var sp = GpuShadingParams.Build(in fx);
            _gpu ??= new MandelbulbGpuCalculator();
            if (_gpu.Render(renderBuffer, rp, sp, bp)) return;
            // Fall through to CPU on init or kernel failure.
        }

        // Phase 4 — G-buffer for SSAO post-pass. Allocated only when SSAO active
        // so the off case pays no memory cost.
        float[]? depthBuf = null;
        float[]? normalBuf = null;
        if (fx.SsaoSamples > 0)
        {
            depthBuf = new float[width * height];
            normalBuf = new float[3 * width * height];
            ScreenSpacePost.ClearGBuffer(depthBuf, normalBuf);
        }

        // Phase 7 — HDR float buffer for tonemap/bloom. NaN sentinel marks sky.
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
                // Ray direction in world coords.
                double rdx = right[0] * u + up[0] * v + fwd[0];
                double rdy = right[1] * u + up[1] * v + fwd[1];
                double rdz = right[2] * u + up[2] * v + fwd[2];
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn[0]; rdy = dn[1]; rdz = dn[2];

                double px = camX, py = camY, pz = camZ;
                double tTotal = 0;
                bool hit = false;
                int hitStep = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = MandelbulbDE(px, py, pz, power, deIter, out _);
                    if (dist < eps)
                    {
                        hit = true;
                        hitStep = step;
                        break;
                    }
                    if (tTotal > 12.0) break; // escaped scene
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit)
                {
                    // Ray-miss → sky backdrop when toggle on; flat
                    // InSetColor when off (lets the user keep IBL surface
                    // lighting without the photographic backdrop competing
                    // with the fractal for focus). SkyColorHdri routes
                    // through HDRI sample when SkyMode=Hdri + HDRI loaded,
                    // gradient otherwise.
                    renderBuffer[idx] = fx.ShowSkyBackdrop
                        ? ShadingPipeline.SkyColorHdri(rdx, rdy, rdz, in fx)
                        : ColorMap.InSetColor;
                    continue;
                }

                // Estimate surface normal via central differences.
                double h = eps * 2;
                double n0 = MandelbulbDE(px + h, py, pz, power, deIter, out _) - MandelbulbDE(px - h, py, pz, power, deIter, out _);
                double n1 = MandelbulbDE(px, py + h, pz, power, deIter, out _) - MandelbulbDE(px, py - h, pz, power, deIter, out _);
                double n2 = MandelbulbDE(px, py, pz + h, power, deIter, out _) - MandelbulbDE(px, py, pz - h, power, deIter, out _);
                var nrm = Normalize3(n0, n1, n2);

                // Color driver: raymarch step count + depth. Spans well across
                // surface even when DE iter-escape is constant, so non-3D
                // gradient themes show variation. 3D themes still get nrm.
                float smooth = (float)hitStep * (256f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 4.0);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);

                // Phase 2 — shading via shared pipeline.
                var inputs = new ShadingInputs(
                    px, py, pz, nrm[0], nrm[1], nrm[2],
                    rdx, rdy, rdz, tTotal, 0.0, hitStep, eps);
                renderBuffer[idx] = ShadingPipeline.Shade<MandelbulbDe>(
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
    }

    /// <summary>
    /// Triplex Mandelbulb distance estimator. Returns lower-bound distance to
    /// the surface from (x, y, z). Also reports the escape iteration count
    /// for color modulation.
    /// </summary>
    private static double MandelbulbDE(double x, double y, double z, double power, int iter, out double escape)
    {
        double zx = x, zy = y, zz = z;
        double dr = 1.0;
        double r = 0.0;
        escape = iter;
        for (int i = 0; i < iter; i++)
        {
            r = Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > 2.0) { escape = i; break; }

            double theta = Math.Acos(zz / r);
            double phi = Math.Atan2(zy, zx);
            double rPow = Math.Pow(r, power);
            dr = Math.Pow(r, power - 1.0) * power * dr + 1.0;

            double newTheta = theta * power;
            double newPhi = phi * power;
            double sinT = Math.Sin(newTheta);
            zx = rPow * sinT * Math.Cos(newPhi) + x;
            zy = rPow * sinT * Math.Sin(newPhi) + y;
            zz = rPow * Math.Cos(newTheta) + z;
        }
        return 0.5 * Math.Log(Math.Max(r, 1e-10)) * r / dr;
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}

/// <summary>P3 — concrete DE struct for the Mandelbulb. Passed to
/// <see cref="FracturingFog.Rendering.Lighting.ShadingPipeline.Shade{TDe}"/>
/// so every shadow / AO / reflection / volumetric DE call inlines.</summary>
public readonly struct MandelbulbDe : FracturingFog.Rendering.Lighting.IDistanceEstimator
{
    public readonly double Power;
    public readonly int Iter;
    public MandelbulbDe(double power, int iter) { Power = power; Iter = iter; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double x, double y, double z)
    {
        double zx = x, zy = y, zz = z;
        double dr = 1.0;
        double r = 0.0;
        int iter = Iter;
        double power = Power;
        for (int i = 0; i < iter; i++)
        {
            r = System.Math.Sqrt(zx * zx + zy * zy + zz * zz);
            if (r > 2.0) break;
            double theta = System.Math.Acos(zz / r);
            double phi = System.Math.Atan2(zy, zx);
            double rPow = System.Math.Pow(r, power);
            dr = System.Math.Pow(r, power - 1.0) * power * dr + 1.0;
            double newTheta = theta * power;
            double newPhi = phi * power;
            double sinT = System.Math.Sin(newTheta);
            zx = rPow * sinT * System.Math.Cos(newPhi) + x;
            zy = rPow * sinT * System.Math.Sin(newPhi) + y;
            zz = rPow * System.Math.Cos(newTheta) + z;
        }
        return 0.5 * System.Math.Log(System.Math.Max(r, 1e-10)) * r / dr;
    }
}
