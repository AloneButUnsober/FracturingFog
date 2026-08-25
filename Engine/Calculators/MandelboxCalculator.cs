// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelboxCalculator.cs
//
// CPU distance-estimation raymarcher for the Mandelbox (box-fold +
// sphere-fold + scale, Tom Lowe 2010). Parallel-scanline render mirroring
// MandelbulbCalculator. Output color blended from surface normal + step
// count via the active IColorMap. DE is exact in the linear-derivative
// sense — folds are tracked as conditional sign flips on dz with a single
// scale-by-r²/min² (or fixed²/r²) inside the sphere-fold band.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class MandelboxCalculator : IFractalCalculator
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

    // P7a — lazily-constructed GPU calculator (see MandelbulbCalculator for contract).
    private MandelboxGpuCalculator? _gpu;

    public MandelboxCalculator(int width, int height) => Resize(width, height);

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
        // P2 — low-res preview.
        bool lowRes = LowResPreview;
        double lrScale = lowRes ? Math.Clamp(FractalParameters.LowResPreviewScale, 0.25, 1.0) : 1.0;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(fullW, fullH, lrScale);
        int width = dims.Width;
        int height = dims.Height;
        uint[] renderBuffer = lowRes ? new uint[width * height] : ColorBuffer;

        double scale = FractalParameters.MandelboxScale;
        double fixedR = Math.Max(1e-3, FractalParameters.MandelboxFixedRadius);
        double minR = Math.Max(1e-3, FractalParameters.MandelboxMinRadius);
        double fixedR2 = fixedR * fixedR;
        double minR2 = minR * minR;
        int deIter = Math.Max(2, FractalParameters.MandelboxIterations);
        int maxSteps = Math.Max(16, FractalParameters.MandelboxMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.MandelboxEpsilon);
        double bailout2 = Math.Max(16.0, FractalParameters.MandelboxBailout);
        bailout2 = bailout2 * bailout2;

        // Mandelbox set sits inside roughly radius (2·|scale|+2). Keep camera
        // outside that bound regardless of Zoom — at high zoom the user wants
        // the lens to zoom in (smaller FOV), not the camera to plunge into
        // the set. Past plunge produced "solid color" at high zoom because
        // every ray hit DE<eps on step 0.
        double setRadius = 2.0 * Math.Abs(scale) + 2.0;
        double camDistFloor = setRadius + 1.0;
        double rawCamDist = FractalParameters.MandelboxCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.MandelboxCameraTheta;
        double camPhi = FractalParameters.MandelboxCameraPhi;

        double camX = camDist * Math.Sin(camPhi) * Math.Cos(camTheta);
        double camY = camDist * Math.Cos(camPhi);
        double camZ = camDist * Math.Sin(camPhi) * Math.Sin(camTheta);

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

        // Phase 20b — true per-eye camera offset along the right basis.
        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camX += right[0] * eyeOffset;
            camY += right[1] * eyeOffset;
            camZ += right[2] * eyeOffset;
        }

        double aspect = (double)width / height;
        // FOV narrows once camera is at its floor — so additional Zoom past
        // the floor acts as a lens zoom rather than a no-op.
        double fovBase = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV
        double zoomLensFactor = rawCamDist >= camDistFloor
            ? 1.0
            : Math.Max(0.05, rawCamDist / camDistFloor);
        double fovScale = fovBase * zoomLensFactor;

        double panU = CenterX;
        double panV = -CenterY;

        double[] light = Normalize3(
            Math.Sin(FractalParameters.MandelboxLightPhi) * Math.Cos(FractalParameters.MandelboxLightTheta),
            Math.Cos(FractalParameters.MandelboxLightPhi),
            Math.Sin(FractalParameters.MandelboxLightPhi) * Math.Sin(FractalParameters.MandelboxLightTheta));

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        // Vol-color slice D (#180) — bake the active theme gradient for the
        // volumetric palette remap (no-op unless VolumePaletteStrength > 0).
        VolumePaletteBaker.Bake(ref fx, ColorMap);
        // P3 — concrete DE struct for inlined Evaluate in Shade<TDe>.
        var deStruct = new De(scale, fixedR2, minR2, bailout2, deIter);

        // Scene escape budget — see the CPU comment further below. Hoisted
        // here so both GPU (P7a) and CPU paths share one definition.
        double sceneRadius = camDist + setRadius * 2.0 + 4.0;

        // P7a — opt-in GPU raymarch path (cheap-palette shading). See
        // MandelbulbCalculator for the FX-drop trade-off + P7c lift plan.
        // #320 — force CPU while an AOV view is active (GPU has no view path).
        // S8 (#404) — GPU 3D-fractal kernels are directional-only; force the CPU
        // shade path when a point/spot light is active (LightSampler on CPU).
        if (fx.UseGpuRender && fx.DebugAov == AovView.Beauty && !lowRes && !fx.HasPositionalLight && !fx.HasAreaLight)
        {
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
                LightX = light[0], LightY = light[1], LightZ = light[2],
                MaxSteps = maxSteps, Eps = eps,
                CullRadiusSq = 0.0,
                InSetColor = ColorMap.InSetColor,
            };
            var bp = new MandelboxGpuParams
            {
                Scale = scale, FixedR2 = fixedR2, MinR2 = minR2,
                Bailout2 = bailout2, DEIter = deIter,
                SceneRadius = sceneRadius,
            };
            var sp = GpuShadingParams.Build(in fx);
            _gpu ??= new MandelboxGpuCalculator();
            if (_gpu.Render(renderBuffer, rp, sp, bp, fx.VolumePalette))
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

        // Scene escape budget is path-length from camera. Must cover the gap
        // from camera through the set and out the far side, otherwise low
        // Zoom (camera far away) marches give up before reaching the surface
        // — previous fixed 16 produced "all black" at Zoom<0.5 because the
        // ray exited the budget while still ~100 units from origin.
        // (Definition hoisted above the GPU dispatch; left here as marker.)

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

                double px = camX, py = camY, pz = camZ;
                double tTotal = 0;
                bool hit = false;
                int hitStep = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = MandelboxDE(px, py, pz, scale, fixedR2, minR2, bailout2, deIter);
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
                double n0 = MandelboxDE(px + h, py, pz, scale, fixedR2, minR2, bailout2, deIter)
                          - MandelboxDE(px - h, py, pz, scale, fixedR2, minR2, bailout2, deIter);
                double n1 = MandelboxDE(px, py + h, pz, scale, fixedR2, minR2, bailout2, deIter)
                          - MandelboxDE(px, py - h, pz, scale, fixedR2, minR2, bailout2, deIter);
                double n2 = MandelboxDE(px, py, pz + h, scale, fixedR2, minR2, bailout2, deIter)
                          - MandelboxDE(px, py, pz - h, scale, fixedR2, minR2, bailout2, deIter);
                var nrm = Normalize3(n0, n1, n2);

                // Color driver: scaled step count + small depth contribution.
                // Earlier `tTotal*4` wrapped ColorMap.MaxIterations=256 many
                // times across the surface and produced rainbow noise.
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
    /// Mandelbox distance estimator. z₀ = c = sample point. Per iter:
    ///   box-fold: clamp each component to [-1, 1] via reflection across ±1
    ///   sphere-fold: scale by fixedR²/r² in band r ∈ [minR, fixedR];
    ///                scale by fixedR²/minR² inside r &lt; minR
    ///   z = scale·z + c, dz tracked as scalar magnitude
    /// DE = |z| / |dz|. dz at +1 per iter accounts for the +c term.
    /// </summary>
    /// <summary>P3 — concrete DE struct. Inlines through Shade&lt;De&gt;.</summary>
    public readonly struct De
        : FracturingFog.Rendering.Lighting.IDistanceEstimator,
          FracturingFog.Rendering.Lighting.IOrbitTrapEstimator
    {
        private readonly double _scale, _fixedR2, _minR2, _bailout2;
        private readonly int _iter;
        public De(double scale, double fixedR2, double minR2, double bailout2, int iter)
        { _scale = scale; _fixedR2 = fixedR2; _minR2 = minR2; _bailout2 = bailout2; _iter = iter; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => MandelboxDE(x, y, z, _scale, _fixedR2, _minR2, _bailout2, _iter);

        // Origin orbit trap (roadmap S9, #391): closest the folded orbit passes to
        // the origin, normalized over the bailout radius. View-independent mesh
        // colour driver.
        public double OrbitTrap(double x, double y, double z)
            => MandelboxTrap(x, y, z, _scale, _fixedR2, _minR2, _bailout2, _iter);
    }

    private static double MandelboxTrap(double cx, double cy, double cz,
        double scale, double fixedR2, double minR2, double bailout2, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double minR2t = double.MaxValue;
        for (int i = 0; i < iter; i++)
        {
            if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
            if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
            if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;
            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < minR2) { double f = fixedR2 / minR2; zx *= f; zy *= f; zz *= f; }
            else if (r2 < fixedR2) { double f = fixedR2 / r2; zx *= f; zy *= f; zz *= f; }
            zx = scale * zx + cx; zy = scale * zy + cy; zz = scale * zz + cz;
            double rr = zx * zx + zy * zy + zz * zz;
            if (rr < minR2t) minR2t = rr;
            if (rr > bailout2) break;
        }
        double bail = Math.Sqrt(Math.Max(bailout2, 1e-9));
        return Math.Clamp(Math.Sqrt(minR2t) / bail, 0.0, 1.0);
    }

    private static double MandelboxDE(double cx, double cy, double cz,
        double scale, double fixedR2, double minR2, double bailout2, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        for (int i = 0; i < iter; i++)
        {
            // Box fold — reflection across ±1 planes.
            if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
            if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
            if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;

            // Sphere fold. Both branches multiply z and dr by same factor —
            // |z|/|dz| stays a valid linear lower bound.
            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < minR2)
            {
                double f = fixedR2 / minR2;
                zx *= f; zy *= f; zz *= f;
                dr *= f;
            }
            else if (r2 < fixedR2)
            {
                double f = fixedR2 / r2;
                zx *= f; zy *= f; zz *= f;
                dr *= f;
            }

            zx = scale * zx + cx;
            zy = scale * zy + cy;
            zz = scale * zz + cz;
            dr = dr * Math.Abs(scale) + 1.0;

            if (zx * zx + zy * zy + zz * zz > bailout2) break;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return rFinal / Math.Max(Math.Abs(dr), 1e-10);
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
