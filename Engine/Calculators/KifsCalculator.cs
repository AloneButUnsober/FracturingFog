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

public sealed class KifsCalculator : IFractalCalculator
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

    // P7a — Menger-fold GPU calculator. P7b — Sierpinski sibling. Both lazy.
    private MengerGpuCalculator? _gpuMenger;
    private SierpinskiGpuCalculator? _gpuSierp;

    public KifsCalculator(int width, int height) => Resize(width, height);

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

        var fold = FractalParameters.KifsFold;
        // Default scale depends on fold table. Sentinel 0.0 means "use the
        // canonical default for this fold". Per-fold defaults:
        //   Menger        — 3.0 (Knighty)
        //   Sierpinski    — 2.0 (Knighty)
        //   Octahedron    — 2.0 (Menger minus z-mirror; tighter scale)
        //   Dodecahedron  — 2.0 (Knighty PHI fold)
        //   MandelboxRot  — 2.0 (classic Mandelbox)
        double rawScale = FractalParameters.KifsScale;
        double scale = rawScale > 0.0
            ? rawScale
            : (fold == KifsFoldKind.Menger ? 3.0 : 2.0);
        double ox = FractalParameters.KifsOffsetX;
        double oy = FractalParameters.KifsOffsetY;
        double oz = FractalParameters.KifsOffsetZ;
        int deIter = Math.Max(2, FractalParameters.KifsIterations);
        int maxSteps = Math.Max(16, FractalParameters.KifsMaxSteps);
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
            KifsFoldKind.MandelboxRot => 3.5,
            _                         => 3.0, // Menger
        };
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.KifsCameraDistance / Math.Max(0.05, Zoom);
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
        double eyeOffset = FractalParameters.Lighting.StereoEyeOffset;
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

        double[] light = Normalize3(
            Math.Sin(FractalParameters.KifsLightPhi) * Math.Cos(FractalParameters.KifsLightTheta),
            Math.Cos(FractalParameters.KifsLightPhi),
            Math.Sin(FractalParameters.KifsLightPhi) * Math.Sin(FractalParameters.KifsLightTheta));

        double sceneRadius = camDist + setRadius * 2.0 + 4.0;
        bool sierp = fold == KifsFoldKind.Sierpinski;
        // GPU paths only exist for Menger + Sierpinski. New folds fall through
        // to the CPU Parallel.For loop below.
        bool gpuEligibleFold = fold == KifsFoldKind.Menger || fold == KifsFoldKind.Sierpinski;

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        DistanceEstimator deDelegate = (x, y, z) => DispatchDE(
            fold, x, y, z, scale, ox, oy, oz, deIter);

        // P7a/P7b — opt-in GPU raymarch path. Menger + Sierpinski each get
        // their own kernel (branchy fold-switch in one kernel bloats the JIT).
        // Cheap-palette shading only — see MandelbulbCalculator for the
        // FX-drop trade-off + P7c lift plan.
        if (fx.UseGpuRender && !lowRes && gpuEligibleFold)
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
            var sp = GpuShadingParams.Build(in fx);
            if (sierp)
            {
                var sip = new SierpinskiGpuParams
                {
                    Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                    DEIter = deIter, SceneRadius = sceneRadius,
                };
                _gpuSierp ??= new SierpinskiGpuCalculator();
                if (_gpuSierp.Render(renderBuffer, rp, sp, sip)) return;
            }
            else
            {
                var mp = new MengerGpuParams
                {
                    Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                    DEIter = deIter, SceneRadius = sceneRadius,
                };
                _gpuMenger ??= new MengerGpuCalculator();
                if (_gpuMenger.Render(renderBuffer, rp, sp, mp)) return;
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

                double px = camX, py = camY, pz = camZ;
                double tTotal = 0;
                bool hit = false;
                int hitStep = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    double dist = DispatchDE(fold, px, py, pz, scale, ox, oy, oz, deIter);
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
                double n0 = DispatchDE(fold, px + h, py, pz, scale, ox, oy, oz, deIter)
                          - DispatchDE(fold, px - h, py, pz, scale, ox, oy, oz, deIter);
                double n1 = DispatchDE(fold, px, py + h, pz, scale, ox, oy, oz, deIter)
                          - DispatchDE(fold, px, py - h, pz, scale, ox, oy, oz, deIter);
                double n2 = DispatchDE(fold, px, py, pz + h, scale, ox, oy, oz, deIter)
                          - DispatchDE(fold, px, py, pz - h, scale, ox, oy, oz, deIter);
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

    /// <summary>
    /// Octahedron fold — Menger sponge with rotated coord system. Pre-iter
    /// rotation by 30° around the Y axis swings the sort axes off the world
    /// axes, so the Menger holes-in-corners pattern emerges along diagonals
    /// instead of cardinal axes. Visually a tilted / faceted version of the
    /// Menger sponge — reads as an octahedral approximation when viewed from
    /// the default camera angle.
    ///
    /// NOTE (5.9.f1): a faithful Mandelbulber-style apex-fold port was attempted
    /// but the <c>--kifsprobe</c> harness showed it collapses to a solid cube
    /// (hitFrac 1.0, radius signature 1:√2:√3). Reverted to the shipped
    /// rotated-Menger approximation, which at least keeps the Menger facets. A
    /// correct octahedral IFS still needs a reference formula + visual iteration
    /// — see the 5.9.f1 status log. The probe is now in place to gate that work.
    /// </summary>
    private static double OctaDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        double mirrorThresh = -0.5 * offZ;
        const double rot = Math.PI / 6.0; // 30°
        double cosR = Math.Cos(rot);
        double sinR = Math.Sin(rot);
        for (int i = 0; i < iter; i++)
        {
            // Y-axis pre-rotation — twists the Menger fold off the cardinal
            // axes, producing the octahedral-facet pattern.
            double rxr = cosR * zx + sinR * zz;
            double rzr = -sinR * zx + cosR * zz;
            zx = rxr; zz = rzr;

            // Menger sort-3 + corner mirror.
            zx = Math.Abs(zx); zy = Math.Abs(zy); zz = Math.Abs(zz);
            double t;
            if (zx - zy < 0) { t = zx; zx = zy; zy = t; }
            if (zx - zz < 0) { t = zx; zx = zz; zz = t; }
            if (zy - zz < 0) { t = zy; zy = zz; zz = t; }

            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz;
            if (zz < mirrorThresh) zz += offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) * Math.Pow(scale, -iter);
    }

    /// <summary>
    /// Dodecahedron-flavoured fold — Sierpinski tetrahedron with a per-iter
    /// rotation around the (1, 1, 1) diagonal axis (axis-angle 36° per iter).
    /// The accumulated rotation breaks the 4-fold tetrahedral symmetry into a
    /// 5-fold-ish pentagonal pattern reminiscent of icosahedral filaments.
    ///
    /// NOTE (5.9.f1): an exact Coxeter [5,3] mirror-plane icosahedral fold was
    /// attempted but the <c>--kifsprobe</c> harness showed it diverges to an
    /// all-black render (hitFrac 0.0) — the scale-from-vertex sends every orbit
    /// to infinity because the user offset is not an icosahedron vertex.
    /// Reverted to the shipped rotated-Sierpinski, which at least renders a
    /// visible (if not truly dodecahedral) shape. A correct icosahedral IFS
    /// needs the scaling centre pinned to a real vertex + visual iteration —
    /// see the 5.9.f1 status log.
    /// </summary>
    private static double DodecaDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        // 36° per-iter rotation around the (1, 1, 1) diagonal — Rodrigues
        // rotation matrix collapsed to inline coefficients.
        const double ang = Math.PI / 5.0; // 36°
        double cosA = Math.Cos(ang);
        double sinA = Math.Sin(ang);
        const double inv3 = 1.0 / 3.0;                  // axis component squared
        const double invSqrt3 = 0.5773502691896258;     // 1/√3
        // Rodrigues entries for axis (1,1,1)/√3:
        // R_ii = cos + (1-cos)·(1/3)
        // R_ij (i!=j) = (1-cos)·(1/3) ± sin·(1/√3)
        double k1 = cosA + (1.0 - cosA) * inv3;
        double k2 = (1.0 - cosA) * inv3 - sinA * invSqrt3;
        double k3 = (1.0 - cosA) * inv3 + sinA * invSqrt3;

        for (int i = 0; i < iter; i++)
        {
            // Rotate around (1,1,1)/√3 by 36°.
            double nx = k1 * zx + k2 * zy + k3 * zz;
            double ny = k3 * zx + k1 * zy + k2 * zz;
            double nz = k2 * zx + k3 * zy + k1 * zz;
            zx = nx; zy = ny; zz = nz;

            // Sierpinski tetrahedron fold.
            double t;
            if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
            if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
            if (zy + zz < 0) { t = -zz; zz = -zy; zy = t; }

            // Scale from corner.
            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return rFinal * Math.Pow(scale, -iter);
    }

    /// <summary>
    /// Mandelbox-style KIFS fold. Per iter:
    ///   box-fold at ±1        (reflect points outside [−1,1] inward)
    ///   sphere-fold           (radial scale: r&lt;½ → 4·z; ½&lt;r&lt;1 → z/r²)
    ///   Y-axis rotation π/48  (per-iter twist)
    ///   z = scale·z − (scale−1)·offset
    /// Uses the fixed-dr KIFS DE scheme (no per-iter |dz| tracking), so the
    /// result is visually Mandelbox-flavoured but is NOT the canonical
    /// Mandelbox DE — the proper sphere-fold + dr-magnitude update lives in
    /// <c>MandelboxCalculator</c>.
    ///
    /// NOTE (5.9.f1): the documented dr-accumulator fix (DE = length/dr with
    /// dr updated by the sphere-fold factor and dr·|scale|+1 per iter) was
    /// implemented and probed. The <c>--kifsprobe</c> harness showed it makes
    /// the object span to radius ~6 — larger than this fold's camera
    /// <c>setRadius</c> (3.5), so the camera would sit inside the body. Reverted
    /// to the shipped fixed-dr scheme rather than ship an unverifiable framing
    /// regression; a correct fix needs the dr accumulator *and* a matched
    /// camera-distance retune, verified visually. See the 5.9.f1 status log.
    /// </summary>
    private static double MandelboxRotDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double k = scale - 1.0;
        double offX = k * ox;
        double offY = k * oy;
        double offZ = k * oz;
        const double rot = Math.PI / 48.0;
        double cosR = Math.Cos(rot);
        double sinR = Math.Sin(rot);
        for (int i = 0; i < iter; i++)
        {
            // Box-fold at ±1.
            if      (zx >  1.0) zx =  2.0 - zx;
            else if (zx < -1.0) zx = -2.0 - zx;
            if      (zy >  1.0) zy =  2.0 - zy;
            else if (zy < -1.0) zy = -2.0 - zy;
            if      (zz >  1.0) zz =  2.0 - zz;
            else if (zz < -1.0) zz = -2.0 - zz;

            // Sphere-fold — pulls points near origin outward, points in the
            // [½, 1] shell get rescaled toward the unit sphere.
            double r2 = zx * zx + zy * zy + zz * zz;
            if (r2 < 0.25)
            {
                zx *= 4.0; zy *= 4.0; zz *= 4.0;
            }
            else if (r2 < 1.0)
            {
                double m = 1.0 / r2;
                zx *= m; zy *= m; zz *= m;
            }

            // Y-axis rotation.
            double nx = cosR * zx + sinR * zz;
            double nz = -sinR * zx + cosR * zz;
            zx = nx; zz = nz;

            // Scale + offset.
            zx = scale * zx - offX;
            zy = scale * zy - offY;
            zz = scale * zz - offZ;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) * Math.Pow(scale, -iter);
    }

    /// <summary>Wave 5.9.f1 — test hook exposing <see cref="DispatchDE"/> for the
    /// headless <c>--kifsprobe</c> geometric self-test (no GUI needed to verify a
    /// fold produces a bounded, non-cubic surface). Not used by the render path.</summary>
    public static double ProbeDE(KifsFoldKind fold,
        double x, double y, double z,
        double scale, double ox, double oy, double oz, int iter)
        => DispatchDE(fold, x, y, z, scale, ox, oy, oz, iter);
}
