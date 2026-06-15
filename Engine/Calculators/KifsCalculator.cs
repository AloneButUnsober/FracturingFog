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

    // P7a — lazily-constructed GPU calculator (Menger fold only).
    // Sierpinski fold stays on CPU until P7b adds a sibling kernel.
    private MengerGpuCalculator? _gpuMenger;

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
        // Default scale depends on fold table — Menger needs 3, Sierpinski 2.
        // Sentinel 0.0 means "use the canonical default". User can override.
        double rawScale = FractalParameters.KifsScale;
        double scale = rawScale > 0.0
            ? rawScale
            : (fold == KifsFoldKind.Sierpinski ? 2.0 : 3.0);
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
        double setRadius = fold == KifsFoldKind.Sierpinski ? 2.5 : 3.0;
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

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        DistanceEstimator deDelegate = (x, y, z) => sierp
            ? SierpDE(x, y, z, scale, ox, oy, oz, deIter)
            : MengerDE(x, y, z, scale, ox, oy, oz, deIter);

        // P7a — opt-in GPU raymarch path for the Menger fold (Sierpinski stays
        // on CPU). Cheap-palette shading only — see MandelbulbCalculator for
        // the FX-drop trade-off + P7c lift plan.
        if (fx.UseGpuRender && !lowRes && !sierp)
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
            var mp = new MengerGpuParams
            {
                Scale = scale, OffsetX = ox, OffsetY = oy, OffsetZ = oz,
                DEIter = deIter, SceneRadius = sceneRadius,
            };
            _gpuMenger ??= new MengerGpuCalculator();
            if (_gpuMenger.Render(renderBuffer, rp, mp)) return;
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
                    double dist = sierp
                        ? SierpDE(px, py, pz, scale, ox, oy, oz, deIter)
                        : MengerDE(px, py, pz, scale, ox, oy, oz, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit) { renderBuffer[idx] = ColorMap.InSetColor; continue; }

                double h = eps * 2;
                double n0, n1, n2;
                if (sierp)
                {
                    n0 = SierpDE(px + h, py, pz, scale, ox, oy, oz, deIter)
                       - SierpDE(px - h, py, pz, scale, ox, oy, oz, deIter);
                    n1 = SierpDE(px, py + h, pz, scale, ox, oy, oz, deIter)
                       - SierpDE(px, py - h, pz, scale, ox, oy, oz, deIter);
                    n2 = SierpDE(px, py, pz + h, scale, ox, oy, oz, deIter)
                       - SierpDE(px, py, pz - h, scale, ox, oy, oz, deIter);
                }
                else
                {
                    n0 = MengerDE(px + h, py, pz, scale, ox, oy, oz, deIter)
                       - MengerDE(px - h, py, pz, scale, ox, oy, oz, deIter);
                    n1 = MengerDE(px, py + h, pz, scale, ox, oy, oz, deIter)
                       - MengerDE(px, py - h, pz, scale, ox, oy, oz, deIter);
                    n2 = MengerDE(px, py, pz + h, scale, ox, oy, oz, deIter)
                       - MengerDE(px, py, pz - h, scale, ox, oy, oz, deIter);
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
}
