// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// KifsCalculator.cs
//
// Kaleidoscopic IFS distance-estimation raymarcher. Selects one of two
// reflective fold tables per iter:
//   • Menger sponge  — Knighty's sort-3 fold + scale-3 from (1,1,1).
//   • Sierpinski tetra — 3 vertex reflections + scale-2 from (1,1,1).
// DE = (length(z) − const) / scale^n, where the const is the bounding
// shape's circumscribed radius and scale^n shrinks as we recurse. Camera /
// lighting plumbing mirrors MandelboxCalculator so the User Bulb-style
// orbit-camera, Theta/Phi rotation and minimap suppression all match.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class KifsCalculator : IFractalCalculator, IStereoEyeCamera, IDepthAovSource, FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator, FracturingFog.Render.IGpuRouteSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>P2 — low-res interactive preview. See Mandelbulb for contract.</summary>
    public bool LowResPreview { get; set; } = false;
    /// <summary>#1373 — the host preview stage's scale for the next frame (null: the default).</summary>
    public double? LowResPreviewScaleOverride { get; set; }

    /// <summary>#1382 — adaptive anti-aliasing (Supersampling.AccumulateAlt): with a
    /// mask the CPU march computes only the marked pixels of a jittered sample.</summary>
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    public bool MaskExact => FractalParameters is { } maskFp && ScreenSpacePost.PixelLocal3D(maskFp.Lighting);

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 96;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <inheritdoc/>
    public double StereoEyeOffset { get; set; }

    /// <inheritdoc/>
    public float[]? DepthBuffer { get; private set; }

    // P7a — Menger-fold GPU calculator. P7b — Sierpinski sibling. Both lazy.
    private MengerGpuCalculator? _gpuMenger;
    private SierpinskiGpuCalculator? _gpuSierp;
    private KifsFoldGpuCalculator? _gpuFold;   // #1173-D — Octahedron / Dodecahedron / MandelboxRot

    public KifsCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    /// <summary>#1173-M — GPU route of the last frame (GPU, or CPU and why).</summary>
    public FracturingFog.Render.GpuRoute LastGpuRoute { get; private set; }

    public void Calculate(CancellationToken ct = default)
    {
        DepthBuffer = null;   // #1009 — never describe an older frame
        ColorMap.MaxIterations = 256;
        int fullW = Width;
        int fullH = Height;
        bool lowRes = LowResPreview;
        double lrScale = FracturingFog.Rendering.LowResPreview.EffectiveScale(lowRes, LowResPreviewScaleOverride, FractalParameters.LowResPreviewScale);   // #1373
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(fullW, fullH, lrScale);
        int width = dims.Width;
        int height = dims.Height;
        uint[] renderBuffer = lowRes ? new uint[width * height] : ColorBuffer;

        var fold = FractalParameters.KifsFold;
        // Default scale depends on fold table. Sentinel 0.0 means "use the
        // canonical default for this fold" (ResolveScale):
        //   Menger        — 3.0 (Knighty)
        //   Sierpinski    — 2.0 (Knighty)
        //   Octahedron    — 2.0 (Knighty octahedral fold, #1197)
        //   Dodecahedron  — φ² ≈ 2.618 (Knighty dodecahedral fold, #1197)
        //   MandelboxRot  — 2.0 (classic Mandelbox)
        double scale = ResolveScale(fold, FractalParameters.KifsScale);
        // #1197 — fold-relative offsets (see FoldOffset).
        var (ox, oy, oz) = FoldOffset(fold,
            FractalParameters.KifsOffsetX, FractalParameters.KifsOffsetY, FractalParameters.KifsOffsetZ);
        int deIter = Math.Max(2, FractalParameters.KifsIterations);
        int maxSteps = Math.Max(16, FractalParameters.KifsMaxSteps);
        double omega = SphereRelax.Clamp(FractalParameters.MarchRelaxation);   // #1335 over-relaxed sphere tracing
        double eps = Math.Max(1e-5, FractalParameters.KifsEpsilon);

        // KIFS attractor inscribed in a unit-ish cube; with offsets at 1
        // a safe outer-camera distance is ≈ 4. Anchor camera against the
        // floor the same way MandelboxCalculator does so high zoom narrows
        // FOV instead of plunging the camera into the set.
        double setRadius = fold switch
        {
            KifsFoldKind.Sierpinski   => 2.5,
            KifsFoldKind.Octahedron   => 2.5,
            KifsFoldKind.Dodecahedron => 2.8,
            // #1197 — a true Mandelbox spans |z| ≤ 2·|scale| + 2 (MandelboxCalculator's
            // framing); the old fixed-dr look-alike fitted inside 3.5.
            KifsFoldKind.MandelboxRot => 2.0 * Math.Abs(scale) + 2.0,
            _                         => 3.0, // Menger
        };
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.KifsCameraDistance / Math.Max(0.05, Zoom);
        if (fold == KifsFoldKind.MandelboxRot)
        {
            // #1197 — the camera distance is tuned for unit-sized KIFS shapes (4 for a
            // ~2.5 radius). A true Mandelbox is ~2·|scale|+2 across, so frame it the
            // way MandelboxCalculator does (distance 12 for radius 6): scale the user's
            // distance with the body and keep the eye a unit outside it.
            rawCamDist *= setRadius / 2.0;
            camDistFloor = setRadius + 1.0;
        }
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.KifsCameraTheta;
        double camPhi = FractalParameters.KifsCameraPhi;

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
        double eyeOffset = StereoEyeOffset;
        if (eyeOffset != 0)
        {
            camX += right[0] * eyeOffset;
            camY += right[1] * eyeOffset;
            camZ += right[2] * eyeOffset;
        }

        double aspect = (double)width / height;
        double fovBase = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV
        double zoomLensFactor = rawCamDist >= camDistFloor
            ? 1.0
            : Math.Max(0.05, rawCamDist / camDistFloor);
        double fovScale = fovBase * zoomLensFactor;

        double panU = CenterX;
        double panV = -CenterY;
        // #1079 — the froxel volume frames this exact camera (zoom / floor clamp,
        // eye offset, zoom lens, pan).
        var froxelView = ScreenSpacePost.Froxel3DViewOf(camX, camY, camZ, fwd[0], fwd[1], fwd[2],
            right[0], right[1], right[2], up[0], up[1], up[2], fovScale, panU, panV, camDist);

        double[] light = Normalize3(
            Math.Sin(FractalParameters.KifsLightPhi) * Math.Cos(FractalParameters.KifsLightTheta),
            Math.Cos(FractalParameters.KifsLightPhi),
            Math.Sin(FractalParameters.KifsLightPhi) * Math.Sin(FractalParameters.KifsLightTheta));

        double sceneRadius = camDist + setRadius * 2.0 + 4.0;
        bool sierp = fold == KifsFoldKind.Sierpinski;

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        // #1068 — froxel volumetrics: shade fog-free + arm the depth G-buffer;
        // the volume is composited after SSAO from the unstripped froxelFx.
        var froxelFx = fx;
        fx = ScreenSpacePost.FogFreeForFroxel3D(in fx);
        // Vol-color slice D (#180) — bake the active theme gradient for the
        // volumetric palette remap (no-op unless VolumePaletteStrength > 0).
        VolumePaletteBaker.Bake(ref fx, ColorMap);
        DistanceEstimator deDelegate = (x, y, z) => DispatchDE(
            fold, x, y, z, scale, ox, oy, oz, deIter);

        // P7a/P7b — opt-in GPU raymarch path. Menger + Sierpinski each get
        // their own kernel (branchy fold-switch in one kernel bloats the JIT);
        // #1173-D — the three Wave 5.9.f1 folds share KifsFoldGpuCalculator.
        // Colour-map LUT albedo (#1172) — see MandelbulbCalculator for the
        // GPU-path shading notes and the #1172 post stack.
        // #320 — force CPU while an AOV view is active (GPU has no view path).
        // S8 (#404/#486) — the Menger + Sierpinski kernels now resolve point/spot
        // lights on the GPU (GpuKernelUtils.ResolveLight), so the !HasPositionalLight
        // gate is lifted. #492 added a per-light area-capped shadow hardness, so area lights render on the GPU now too.
        // #1173-M — the same gate as before, plus the reason when it fails.
        var gpuGate = Gpu3DRoute.Gate(in fx, lowRes);
        LastGpuRoute = gpuGate ?? default;
        if (gpuGate is null)
        {
            // #1070 — Froxel3D on the GPU trace: the kernel also writes per-pixel
            // ray distance, and the CPU froxel pass composites over the GPU frame.
            // #1172 — the post stack's G-buffers, from the kernel, allocated under the CPU
            // path's conditions. Beauty only: a view mode leaves them empty on the CPU too.
            bool gpuPost = fx.DebugAov == AovView.Beauty;
            bool gpuG = gpuPost && ScreenSpacePost.WantsGBuffer(in fx);
            float[]? gpuDepth = ScreenSpacePost.GpuWantsDepth(in fx) || gpuG ? new float[width * height] : null;   // #323 — froxel and/or stereo depth
            float[]? gpuNormal = gpuG ? new float[3 * width * height] : null;
            float[]? gpuHdr = gpuPost && ScreenSpacePost.WantsHdrPost(in fx) ? new float[3 * width * height] : null;
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
                MaxSteps = maxSteps, Eps = eps, Relaxation = omega,   // #1335
                CullRadiusSq = 0.0,
                InSetColor = ColorMap.InSetColor,
                // S3 (#567) — thin-lens DOF on the GPU kernel. Inactive (aperture 0 /
                // one sample) -> the single centre ray -> byte-identical. Focus auto-
                // resolves to the camera distance when unset.
                DofAperture = ThinLensDof.IsActive(in fx) ? fx.DofAperture : 0.0,
                DofFocus = ThinLensDof.FocusDistance(in fx, camDist),
                DofSamples = ThinLensDof.SampleCount(in fx),
            };
            var sp = GpuShadingParams.Build(in fx);
            // #1172 / G2.2 — the CPU trace's colour-map albedo (same smooth coefficients).
            uint[] albedoLut = GpuAlbedoLut.Bake(ColorMap, 192.0, 0.5, sceneRadius, ref sp);
            uint[]? hdriEnv = GpuHdriEnv.Resolve(in fx, ref sp);   // #1173-B / G2.3 — HDRI sky, ambient + reflections
            bool gpuOk;
            string gpuError;
            if (fold is KifsFoldKind.Octahedron or KifsFoldKind.Dodecahedron or KifsFoldKind.MandelboxRot)
            {
                var kfold = fold switch
                {
                    KifsFoldKind.Octahedron => KifsGpuFold.Octahedron,
                    KifsFoldKind.Dodecahedron => KifsGpuFold.Dodecahedron,
                    _ => KifsGpuFold.MandelboxRot,
                };
                var kp = KifsFoldGpuParams.For(kfold, scale, ox, oy, oz, deIter, sceneRadius);
                _gpuFold ??= new KifsFoldGpuCalculator();
                gpuOk = _gpuFold.Render(renderBuffer, rp, sp, kp, fx.VolumePalette, gpuDepth, ct, gpuNormal, gpuHdr, albedoLut, hdriEnv);
                gpuError = _gpuFold.LastError;
            }
            else if (sierp)
            {
                var sip = new SierpinskiGpuParams
                {
                    Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                    DEIter = deIter, SceneRadius = sceneRadius,
                };
                _gpuSierp ??= new SierpinskiGpuCalculator();
                gpuOk = _gpuSierp.Render(renderBuffer, rp, sp, sip, fx.VolumePalette, gpuDepth, ct, gpuNormal, gpuHdr, albedoLut, hdriEnv);
                gpuError = _gpuSierp.LastError;
            }
            else
            {
                var mp = new MengerGpuParams
                {
                    Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                    DEIter = deIter, SceneRadius = sceneRadius,
                };
                _gpuMenger ??= new MengerGpuCalculator();
                gpuOk = _gpuMenger.Render(renderBuffer, rp, sp, mp, fx.VolumePalette, gpuDepth, ct, gpuNormal, gpuHdr, albedoLut, hdriEnv);
                gpuError = _gpuMenger.LastError;
            }
            LastGpuRoute = Gpu3DRoute.AfterRender(gpuOk, gpuError);
            if (gpuOk)
            {
                DepthBuffer = ScreenSpacePost.PublishDepth(gpuDepth, width, height, width, height, in fx, valid: !ThinLensDof.IsActive(in fx));   // #323 — stereo depth from the GPU trace
                ScreenSpacePost.ApplyPost3D(renderBuffer, gpuHdr, gpuDepth, gpuNormal, width, height,
                    ThinLensDof.IsActive(in fx), in fx, in froxelView, in froxelFx, new DelegateDeAdapter(deDelegate));   // #1172 — the full post stack on the GPU frame
                // #84 — the GPU branch returns before the CPU tail; draw the debug
                // HUD directly so the light compass still shows on GPU frames.
                ScreenSpacePost.FinishFrame3D(renderBuffer, width, height, in fx, preview: lowRes);
                return;
            }
        }

        // Phase 4 — G-buffer for SSAO post-pass.
        float[]? depthBuf = null;
        float[]? normalBuf = null;
        if (ScreenSpacePost.WantsGBuffer(in fx))   // #1009 — was SSAO-only
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

        // #1336 — the live view's distance-field cache: march the baked field while
        // only the camera moves (null without a cache, before the bake's coarse grid,
        // or with the camera too close for its voxels).
        var field = DistanceFieldCache.Current?.Acquire(DeKey(fold, scale, ox, oy, oz, deIter),
            0, 0, 0, setRadius, eps, camX, camY, camZ, camDist, 2.0 * fovScale / height,
            (x, y, z) => DispatchDE(fold, x, y, z, scale, ox, oy, oz, deIter));
        if (field is not null) deDelegate = field.Sample;
        double normalH = field is null ? eps * 2 : Math.Max(eps * 2, 0.5 * field.Voxel);

        // #1337 — the live view's temporal AO / shadow reuse (no-op without a cache).
        TemporalLighting.BeginFrame(width, height,
            new PinholeCamera(camX, camY, camZ, right[0], right[1], right[2], up[0], up[1], up[2],
                fwd[0], fwd[1], fwd[2], fovScale, aspect, panU, panV),
            TemporalLighting.SceneKey(in fx, eps, (int)fold, scale, ox, oy, oz, deIter),
            usable: eyeOffset == 0 && fx.DebugAov == AovView.Beauty);

        // #1382 — adaptive AA: only the marked pixels (a full-size, full-detail frame).
        byte[]? mask = !lowRes && width == fullW && height == fullH
            && PixelMask is { } pmask && pmask.Length == width * height ? pmask : null;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale + panV;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask != null && mask[rowBase + x] == 0) continue;   // #1382
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

                var relax = new SphereRelax(omega);   // #1335 over-relaxed sphere tracing
                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = field is null ? DispatchDE(fold, px, py, pz, scale, ox, oy, oz, deIter)
                                                : field.Sample(px, py, pz);
                    // #1335: the step here overshot (spheres apart): back up, march plainly.
                    double back = relax.BackOff(dist);
                    if (back > 0) { px -= rdx * back; py -= rdy * back; pz -= rdz * back; tTotal -= back; continue; }
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    double rs = relax.Step(dist);   // #1335
                    px += rdx * rs; py += rdy * rs; pz += rdz * rs;
                    tTotal += rs;
                }

                int idx = rowBase + x;
                if (!hit)
                {
                    // Ray-miss → sky backdrop when toggle on; InSetColor off (see MandelbulbCalculator).
                    renderBuffer[idx] = ShadingPipeline.MissColor(rdx, rdy, rdz, in fx, ColorMap.InSetColor);
                    continue;
                }

                double h = normalH;   // #1336: half a voxel on the cached field (smooth preview normals)
                double n0, n1, n2;
                if (field is null)
                {
                    n0 = DispatchDE(fold, px + h, py, pz, scale, ox, oy, oz, deIter)
                       - DispatchDE(fold, px - h, py, pz, scale, ox, oy, oz, deIter);
                    n1 = DispatchDE(fold, px, py + h, pz, scale, ox, oy, oz, deIter)
                       - DispatchDE(fold, px, py - h, pz, scale, ox, oy, oz, deIter);
                    n2 = DispatchDE(fold, px, py, pz + h, scale, ox, oy, oz, deIter)
                       - DispatchDE(fold, px, py, pz - h, scale, ox, oy, oz, deIter);
                }
                else
                {
                    n0 = field.Sample(px + h, py, pz) - field.Sample(px - h, py, pz);
                    n1 = field.Sample(px, py + h, pz) - field.Sample(px, py - h, pz);
                    n2 = field.Sample(px, py, pz + h) - field.Sample(px, py, pz - h);
                }
                var nrm = Normalize3(n0, n1, n2);

                float smooth = (float)hitStep * (192f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 0.5);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);

                // Phase 2 — shading via shared pipeline.
                var inputs = new ShadingInputs(
                    px, py, pz, nrm[0], nrm[1], nrm[2],
                    rdx, rdy, rdz, tTotal, 0.0, hitStep, eps);
                renderBuffer[idx] = ShadingPipeline.Shade(
                    in inputs, baseColor, in fx, deDelegate,
                    idx, depthBuf, normalBuf, hdrBuf);
            }
        });

        ScreenSpacePost.BeginGpuFrame(renderBuffer, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplySsao(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        ScreenSpacePost.ApplyFroxel3D(renderBuffer, hdrBuf, depthBuf, width, height,
            in froxelView, in froxelFx, new DelegateDeAdapter(deDelegate));   // #1068 / #1078 shadowed by the DE
        if (hdrBuf is not null && depthBuf is not null)
            ScreenSpacePost.ApplyHdrDof(hdrBuf, depthBuf, width, height, in fx);
        if (hdrBuf is not null)
            ScreenSpacePost.ApplyToneMapBloom(renderBuffer, hdrBuf, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplyEdgeInk(renderBuffer, depthBuf, normalBuf, width, height, in fx);
        ScreenSpacePost.EndGpuFrame(in fx);

        // #1009 — publish the depth for post-frame passes (Fake stereo).
        DepthBuffer = ScreenSpacePost.PublishDepth(depthBuf, width, height, fullW, fullH, in fx, valid: true);

        if (lowRes)
            FracturingFog.Rendering.LowResPreview.UpscaleNearest(
                renderBuffer, width, height, ColorBuffer, fullW, fullH);

        // #84 — light-direction compass / param bars / scene clock. Standalone
        // final pass on the composited full-res buffer so it survives both the
        // low-res upscale and the GPU-raymarch early-out. Self-guards on flags.
        ScreenSpacePost.FinishFrame3D(ColorBuffer, fullW, fullH, in fx, preview: lowRes);
    }

    /// <summary>
    /// Menger-sponge DE (Knighty's formulation). Per iter:
    ///   z = |z|                                        (octant fold)
    ///   sort components so |x| >= |y| >= |z|           (3 conditional swaps)
    ///   z = scale·z − (scale−1)·offset
    ///   if z.z < -(scale−1)·offset.z/2  →  z.z += (scale−1)·offset.z
    /// Iteration runs to fixed depth; dr = scale^N is constant. DE is the
    /// distance to a bounding sphere of radius 2 around the iterated point.
    /// </summary>
    private static double MengerDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        double mirrorThresh = -0.5 * offZ;
        for (int i = 0; i < iter; i++)
        {
            zx = Math.Abs(zx); zy = Math.Abs(zy); zz = Math.Abs(zz);
            double t;
            if (zx - zy < 0) { t = zx; zx = zy; zy = t; }
            if (zx - zz < 0) { t = zx; zx = zz; zz = t; }
            if (zy - zz < 0) { t = zy; zy = zz; zz = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz;
            // Knighty corner-mirror: when the smallest (post-scale) component
            // ends up below −offset/2, fold it back by +offset. Reference:
            // p.z < −0.5·offset·(scale−1) → p.z += offset·(scale−1).
            if (zz < mirrorThresh) zz += offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) * Math.Pow(scale, -iter);
    }

    /// <summary>
    /// Sierpinski tetrahedron DE (Knighty's formulation). Per iter:
    ///   3 vertex reflections (flip-on-negative-sum across the tetra faces).
    ///   z = scale·z − (scale−1)·offset.
    /// Iteration runs to fixed depth; dr = scale^N is constant.
    /// </summary>
    private static double SierpDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        for (int i = 0; i < iter; i++)
        {
            double t;
            if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
            if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
            if (zy + zz < 0) { t = -zz; zz = -zy; zy = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return rFinal * Math.Pow(scale, -iter);
    }

    /// <summary>#1336 — the distance-field cache key: every input of <see cref="DispatchDE"/>.</summary>
    internal static long DeKey(KifsFoldKind fold, double scale, double ox, double oy, double oz, int deIter)
    {
        var h = new HashCode();
        h.Add(0x4B494653);   // "KIFS"
        h.Add((int)fold); h.Add(scale); h.Add(ox); h.Add(oy); h.Add(oz); h.Add(deIter);
        return h.ToHashCode();
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }

    // Wave 5.9 — fold-table dispatcher. Hot path inside the raymarch +
    // normal-estimation loops. JIT branches on a 5-way switch; CPU branch
    // predictor pins the dominant fold per frame because every pixel of a
    // given frame walks the same arm.
    private static double DispatchDE(KifsFoldKind fold,
        double x, double y, double z,
        double scale, double ox, double oy, double oz, int iter) => fold switch
        {
            KifsFoldKind.Sierpinski   => SierpDE(x, y, z, scale, ox, oy, oz, iter),
            KifsFoldKind.Octahedron   => OctaDE(x, y, z, scale, ox, oy, oz, iter),
            KifsFoldKind.Dodecahedron => DodecaDE(x, y, z, scale, ox, oy, oz, iter),
            KifsFoldKind.MandelboxRot => MandelboxRotDE(x, y, z, scale, ox, oy, oz, iter),
            _                         => MengerDE(x, y, z, scale, ox, oy, oz, iter),
        };

    // ── #1197 — true Octahedron / Dodecahedron / Mandelbox-rot folds ─────────
    //
    // The polyhedral folds follow Knighty's Kaleidoscopic IFS formulation
    // (fractalforums.com, "Kaleidoscopic (escape time IFS)"), as published in
    // Syntopia's Fragmentarium examples (Examples/Kaleidoscopic IFS/
    // Octahedron.frag, Dodecahedron.frag). The key to each is scaling toward a
    // real VERTEX of the polyhedron: the earlier ports scaled toward the shared
    // default offset (1, 1, 1) — a cube corner — which collapsed the octahedron
    // into a solid cube. FoldOffset maps the user offset onto each fold's vertex.
    // MandelboxRot is the canonical Mandelbox (MandelboxCalculator's running-
    // derivative DE) with a per-iteration rotation.
    //
    // Keep these in lockstep with KifsFoldGpuCalculator's twins.

    /// <summary>#1197 — golden ratio φ.</summary>
    public const double Phi = 1.6180339887498949;

    /// <summary>#1197 — the fold's scale: the user's when set (&gt; 0), else the fold's
    /// canonical one — Menger 3, Dodecahedron φ² (Knighty / Fragmentarium), others 2.</summary>
    public static double ResolveScale(KifsFoldKind fold, double rawScale)
        => rawScale > 0.0 ? rawScale : fold switch
        {
            KifsFoldKind.Menger       => 3.0,
            KifsFoldKind.Dodecahedron => Phi * Phi,
            _                         => 2.0,
        };

    /// <summary>
    /// #1197 — the offset the fold actually uses. The KIFS offset is shared by every
    /// fold and defaults to (1, 1, 1), the scaling vertex of Menger, Sierpinski and
    /// the dodecahedron. It is read RELATIVE to that default: the fold's own vertex
    /// plus (offset − (1, 1, 1)). So defaults give each fold its true shape and a
    /// tweak moves it the same way for every fold.
    /// <list type="bullet">
    /// <item>Octahedron — vertex (1, 0, 0).</item>
    /// <item>MandelboxRot — no scaling vertex (c is the point itself); the delta
    /// shifts c instead.</item>
    /// <item>Menger / Sierpinski / Dodecahedron — unchanged (vertex (1, 1, 1)).</item>
    /// </list>
    /// </summary>
    public static (double X, double Y, double Z) FoldOffset(KifsFoldKind fold, double ox, double oy, double oz)
        => fold switch
        {
            KifsFoldKind.Octahedron   => (ox, oy - 1.0, oz - 1.0),
            KifsFoldKind.MandelboxRot => (ox - 1.0, oy - 1.0, oz - 1.0),
            _                         => (ox, oy, oz),
        };

    /// <summary>#1197 — the dodecahedral fold's three mirror normals (Knighty):
    /// (−1, φ−1, 1/(φ−1)), (φ−1, 1/(φ−1), −1), (1/(φ−1), −1, φ−1), normalised.</summary>
    public static readonly (double X, double Y, double Z)[] DodecaNormals = BuildDodecaNormals();

    private static (double X, double Y, double Z)[] BuildDodecaNormals()
    {
        double a = Phi - 1.0, b = 1.0 / (Phi - 1.0);
        static (double, double, double) N(double x, double y, double z)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            return (x / l, y / l, z / l);
        }
        return new[] { N(-1.0, a, b), N(a, b, -1.0), N(b, -1.0, a) };
    }

    /// <summary>Per-iteration Y rotation of the Mandelbox-rot fold (π/48).</summary>
    public const double MandelboxRotAngle = Math.PI / 48.0;

    /// <summary>
    /// Octahedron — Knighty's octahedral fold: four mirror planes x+y, x+z, x−y,
    /// x−z (folding into the octahedron's fundamental domain), then scale toward
    /// the vertex (1, 0, 0) (offset via <see cref="FoldOffset"/>). DE = |z|·s⁻ⁿ.
    /// </summary>
    private static double OctaDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox, offY = k * oy, offZ = k * oz;
        for (int i = 0; i < iter; i++)
        {
            double t;
            if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
            if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
            if (zx - zy < 0) { t = zy; zy = zx; zx = t; }
            if (zx - zz < 0) { t = zz; zz = zx; zx = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        return Math.Sqrt(zx * zx + zy * zy + zz * zz) * Math.Pow(scale, -iter);
    }

    /// <summary>
    /// Dodecahedron — Knighty's dodecahedral (icosahedral-symmetry) fold: three
    /// golden-ratio mirror planes, applied three times per iteration, then scale
    /// toward the dodecahedron vertex (1, 1, 1) at scale φ². Escapes past r² 1e9;
    /// DE = |z|·s^−(n+1) (Fragmentarium's "subtract one" form).
    /// </summary>
    private static double DodecaDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        var n = DodecaNormals;
        double n1x = n[0].X, n1y = n[0].Y, n1z = n[0].Z;
        double n2x = n[1].X, n2y = n[1].Y, n2z = n[1].Z;
        double n3x = n[2].X, n3y = n[2].Y, n3z = n[2].Z;
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox, offY = k * oy, offZ = k * oz;
        int it = 0;
        for (; it < iter; it++)
        {
            for (int rep = 0; rep < 3; rep++)
            {
                double d = zx * n1x + zy * n1y + zz * n1z;
                if (d < 0) { zx -= 2 * d * n1x; zy -= 2 * d * n1y; zz -= 2 * d * n1z; }
                d = zx * n2x + zy * n2y + zz * n2z;
                if (d < 0) { zx -= 2 * d * n2x; zy -= 2 * d * n2y; zz -= 2 * d * n2z; }
                d = zx * n3x + zy * n3y + zz * n3z;
                if (d < 0) { zx -= 2 * d * n3x; zy -= 2 * d * n3y; zz -= 2 * d * n3z; }
            }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
            if (zx * zx + zy * zy + zz * zz > 1e9) break;
        }
        return Math.Sqrt(zx * zx + zy * zy + zz * zz) * Math.Pow(scale, -(it + 1));
    }

    /// <summary>
    /// Mandelbox-rot — the canonical Mandelbox (box fold at ±1, sphere fold with
    /// min radius ½ / fixed radius 1, z = s·z + c, running derivative dr, DE =
    /// |z| / |dr| — as <c>MandelboxCalculator</c>) with a π/48 rotation about Y
    /// each iteration. The rotation is an isometry, so dr is unchanged by it.
    /// c is the point itself, shifted by the offset delta (<see cref="FoldOffset"/>).
    /// </summary>
    private static double MandelboxRotDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double ccx = cx + ox, ccy = cy + oy, ccz = cz + oz;
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        double cosR = Math.Cos(MandelboxRotAngle), sinR = Math.Sin(MandelboxRotAngle);
        double absScale = Math.Abs(scale);
        for (int i = 0; i < iter; i++)
        {
            if (zx > 1.0) zx = 2.0 - zx; else if (zx < -1.0) zx = -2.0 - zx;
            if (zy > 1.0) zy = 2.0 - zy; else if (zy < -1.0) zy = -2.0 - zy;
            if (zz > 1.0) zz = 2.0 - zz; else if (zz < -1.0) zz = -2.0 - zz;

            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < 0.25) { zx *= 4.0; zy *= 4.0; zz *= 4.0; dr *= 4.0; }
            else if (r2 < 1.0) { double f = 1.0 / r2; zx *= f; zy *= f; zz *= f; dr *= f; }

            double nx = cosR * zx + sinR * zz;
            double nz = -sinR * zx + cosR * zz;
            zx = nx; zz = nz;

            zx = scale * zx + ccx;
            zy = scale * zy + ccy;
            zz = scale * zz + ccz;
            dr = dr * absScale + 1.0;
            if (zx * zx + zy * zy + zz * zz > 1e6) break;
        }
        return Math.Sqrt(zx * zx + zy * zy + zz * zz) / Math.Max(Math.Abs(dr), 1e-10);
    }

    /// <summary>Wave 5.9.f1 — test hook exposing <see cref="DispatchDE"/> for the
    /// headless <c>--kifsprobe</c> geometric self-test (no GUI needed to verify a
    /// fold produces a bounded, non-cubic surface). Not used by the render path.</summary>
    public static double ProbeDE(KifsFoldKind fold,
        double x, double y, double z,
        double scale, double ox, double oy, double oz, int iter)
    {
        // #1197 — callers pass the user offset; resolve it per fold like Calculate.
        var (fx, fy, fz) = FoldOffset(fold, ox, oy, oz);
        return DispatchDE(fold, x, y, z, scale, fx, fy, fz, iter);
    }
}
