// BicomplexMandelbrotCalculator.cs
//
// Bicomplex (tessarine) Mandelbrot distance-estimation raymarcher. Iteration
// t := t² + c with t, c ∈ ℂ², the commutative 4D algebra spanned by
// (1, i, j, k) under the relations i² = j² = −1, k² = +1, ij = ji = k. Unlike
// quaternions, multiplication commutes — and unlike split-complex algebras,
// the algebra has zero divisors (anything of the form a + a·k with a ∈ ℂ).
// Renderer slices the 4D set: pixel (x, y, z) ↦ c = (x, y, z, sliceW). The
// orbit starts at the origin (Mandelbrot membership test) and the derivative
// dt/dc is tracked through chain rule for Hubbard–Douady DE.
//
// Squaring map for t = (t1 + t2·i + t3·j + t4·k):
//   t²_R = t1² − t2² − t3² + t4²
//   t²_i = 2·(t1·t2 − t3·t4)
//   t²_j = 2·(t1·t3 − t2·t4)
//   t²_k = 2·(t1·t4 + t2·t3)
// (verify: i·j coefficient = 2·t_i·t_j with no sign flip = 2·(t2·t3 + t3·t2)/2
//  → drops to the k coefficient above; commutativity removes the Hamilton
//  ordering subtlety quaternions need.)
//
// Visually the bicomplex Mandelbrot reproduces the familiar 2D Mandelbrot on
// the (i, j = 0, k = 0) slice and develops thin filaments / zero-divisor seam
// surfaces away from that plane. Less iconic than the quaternion set but
// distinct — the seam plane (t.k = ±|t.real|) introduces flat slabs that
// don't appear in any Hamilton-algebra rendering.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class BicomplexMandelbrotCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // P7b — lazily-constructed GPU calculator. See MandelbulbCalculator for contract.
    private BicomplexGpuCalculator? _gpu;

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

    public BicomplexMandelbrotCalculator(int width, int height) => Resize(width, height);

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

        double sliceW = FractalParameters.BicomplexSliceW;
        int deIter = Math.Max(2, FractalParameters.BicomplexIterations);
        double bailout2 = Math.Max(4.0, FractalParameters.BicomplexBailout);
        int maxSteps = Math.Max(16, FractalParameters.BicomplexMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.BicomplexEpsilon);

        double setRadius = 2.0;
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.BicomplexCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.BicomplexCameraTheta;
        double camPhi = FractalParameters.BicomplexCameraPhi;

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

        double aspect = (double)width / height;
        double fovBase = Math.Tan(0.5 * Math.PI / 3.0);
        double zoomLensFactor = rawCamDist >= camDistFloor
            ? 1.0
            : Math.Max(0.05, rawCamDist / camDistFloor);
        double fovScale = fovBase * zoomLensFactor;

        double panU = CenterX;
        double panV = -CenterY;

        double[] light = Normalize3(
            Math.Sin(FractalParameters.BicomplexLightPhi) * Math.Cos(FractalParameters.BicomplexLightTheta),
            Math.Cos(FractalParameters.BicomplexLightPhi),
            Math.Sin(FractalParameters.BicomplexLightPhi) * Math.Sin(FractalParameters.BicomplexLightTheta));

        // Phase 1c — Lighting struct is authoritative for Light1/2/3.
        var fx = FractalParameters.Lighting;
        var deStruct = new De(sliceW, bailout2, deIter);

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
            var bp = new BicomplexGpuParams
            {
                SliceW = sliceW,
                Bailout2 = bailout2, DEIter = deIter,
                SceneRadius = sceneRadius,
            };
            var sp = GpuShadingParams.Build(in fx);
            _gpu ??= new BicomplexGpuCalculator();
            if (_gpu.Render(renderBuffer, rp, sp, bp)) return;
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
                    double dist = BicomplexDE(px, py, pz, sliceW, bailout2, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit) { renderBuffer[idx] = ColorMap.InSetColor; continue; }

                double h = eps * 2;
                double n0 = BicomplexDE(px + h, py, pz, sliceW, bailout2, deIter)
                          - BicomplexDE(px - h, py, pz, sliceW, bailout2, deIter);
                double n1 = BicomplexDE(px, py + h, pz, sliceW, bailout2, deIter)
                          - BicomplexDE(px, py - h, pz, sliceW, bailout2, deIter);
                double n2 = BicomplexDE(px, py, pz + h, sliceW, bailout2, deIter)
                          - BicomplexDE(px, py, pz - h, sliceW, bailout2, deIter);
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
    }

    /// <summary>
    /// Hubbard–Douady DE for the bicomplex squaring map. Per iter:
    ///   dt := 2 · t · dt + 1   (bicomplex product; 1 = (1, 0, 0, 0))
    ///   t  := t² + c
    /// Bicomplex components are packed (1, i, j, k) under i² = j² = −1,
    /// k² = +1, ij = ji = k. Multiplication commutes — the 2·t·dt step
    /// uses the symmetric bicomplex product, not the Hamilton order.
    /// </summary>
    /// <summary>P3 — concrete DE struct.</summary>
    public readonly struct De : FracturingFog.Rendering.Lighting.IDistanceEstimator
    {
        private readonly double _sliceW, _bailout2;
        private readonly int _iter;
        public De(double sliceW, double bailout2, int iter)
        { _sliceW = sliceW; _bailout2 = bailout2; _iter = iter; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public double Evaluate(double x, double y, double z)
            => BicomplexDE(x, y, z, _sliceW, _bailout2, _iter);
    }

    private static double BicomplexDE(
        double sx, double sy, double sz, double sliceW,
        double bailout2, int iter)
    {
        // Pack pixel as c: (1, i, j, k) = (sx, sy, sz, sliceW).
        double c1 = sx, c2 = sy, c3 = sz, c4 = sliceW;

        // t starts at zero (Mandelbrot membership test).
        double t1 = 0.0, t2 = 0.0, t3 = 0.0, t4 = 0.0;
        // dt/dc starts at zero because t_0 = 0.
        double d1 = 0.0, d2 = 0.0, d3 = 0.0, d4 = 0.0;

        for (int i = 0; i < iter; i++)
        {
            // dt := 2·t·dt + 1. Bicomplex product (commutative).
            double nd1 = t1 * d1 - t2 * d2 - t3 * d3 + t4 * d4;
            double nd2 = t1 * d2 + t2 * d1 - t3 * d4 - t4 * d3;
            double nd3 = t1 * d3 + t3 * d1 - t2 * d4 - t4 * d2;
            double nd4 = t1 * d4 + t4 * d1 + t2 * d3 + t3 * d2;
            d1 = 2.0 * nd1 + 1.0;
            d2 = 2.0 * nd2;
            d3 = 2.0 * nd3;
            d4 = 2.0 * nd4;

            // t := t² + c. Bicomplex squaring (derived from the product
            // table for i² = j² = −1, k² = +1, ij = ji = k).
            double nt1 = t1 * t1 - t2 * t2 - t3 * t3 + t4 * t4;
            double nt2 = 2.0 * (t1 * t2 - t3 * t4);
            double nt3 = 2.0 * (t1 * t3 - t2 * t4);
            double nt4 = 2.0 * (t1 * t4 + t2 * t3);
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
