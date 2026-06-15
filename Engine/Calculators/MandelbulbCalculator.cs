// MandelbulbCalculator.cs
//
// CPU distance-estimation raymarcher for the Mandelbulb (3D Mandelbrot
// analogue, triplex power-N formula). Parallel-scanline render. Output color
// blended from surface normal + iteration escape count via the active
// IColorMap. Slow vs GPU compute, but interactive at 800×600.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class MandelbulbCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 96;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

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
        int width = Width;
        int height = Height;
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

        // AO / fog / shadow walks reuse the same DE the primary raymarch uses.
        DistanceEstimator deDelegate = (x, y, z) => MandelbulbDE(x, y, z, power, deIter, out _);

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
                    ColorBuffer[idx] = ColorMap.InSetColor;
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
                ColorBuffer[idx] = ShadingPipeline.Shade(
                    in inputs, baseColor, in fx, deDelegate,
                    idx, depthBuf, normalBuf, hdrBuf);
            }
        });

        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplySsao(ColorBuffer, depthBuf, normalBuf, width, height, in fx);
        if (hdrBuf is not null)
            ScreenSpacePost.ApplyToneMapBloom(ColorBuffer, hdrBuf, width, height, in fx);
        if (depthBuf is not null && normalBuf is not null)
            ScreenSpacePost.ApplyEdgeInk(ColorBuffer, depthBuf, normalBuf, width, height, in fx);
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
