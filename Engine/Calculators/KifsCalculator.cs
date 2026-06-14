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

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class KifsCalculator : IFractalCalculator
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
        int width = Width;
        int height = Height;

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
        double bailout2 = Math.Max(16.0, FractalParameters.KifsBailout);

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
                        ? SierpDE(px, py, pz, scale, ox, oy, oz, bailout2, deIter)
                        : MengerDE(px, py, pz, scale, ox, oy, oz, bailout2, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit) { ColorBuffer[idx] = ColorMap.InSetColor; continue; }

                double h = eps * 2;
                double n0, n1, n2;
                if (sierp)
                {
                    n0 = SierpDE(px + h, py, pz, scale, ox, oy, oz, bailout2, deIter)
                       - SierpDE(px - h, py, pz, scale, ox, oy, oz, bailout2, deIter);
                    n1 = SierpDE(px, py + h, pz, scale, ox, oy, oz, bailout2, deIter)
                       - SierpDE(px, py - h, pz, scale, ox, oy, oz, bailout2, deIter);
                    n2 = SierpDE(px, py, pz + h, scale, ox, oy, oz, bailout2, deIter)
                       - SierpDE(px, py, pz - h, scale, ox, oy, oz, bailout2, deIter);
                }
                else
                {
                    n0 = MengerDE(px + h, py, pz, scale, ox, oy, oz, bailout2, deIter)
                       - MengerDE(px - h, py, pz, scale, ox, oy, oz, bailout2, deIter);
                    n1 = MengerDE(px, py + h, pz, scale, ox, oy, oz, bailout2, deIter)
                       - MengerDE(px, py - h, pz, scale, ox, oy, oz, bailout2, deIter);
                    n2 = MengerDE(px, py, pz + h, scale, ox, oy, oz, bailout2, deIter)
                       - MengerDE(px, py, pz - h, scale, ox, oy, oz, bailout2, deIter);
                }
                var nrm = Normalize3(n0, n1, n2);

                double diffuse = Math.Max(0.0, nrm[0] * light[0] + nrm[1] * light[1] + nrm[2] * light[2]);
                double ambient = 0.15;
                double shade = ambient + diffuse * (1.0 - ambient);

                float smooth = (float)hitStep * (192f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 0.5);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);
                byte R = (byte)Math.Clamp(((baseColor >> 16) & 0xFF) * shade, 0, 255);
                byte G = (byte)Math.Clamp(((baseColor >> 8) & 0xFF) * shade, 0, 255);
                byte B = (byte)Math.Clamp((baseColor & 0xFF) * shade, 0, 255);
                ColorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }

    /// <summary>
    /// Menger-sponge DE. Per iter:
    ///   v = |z|                                       (3 reflections)
    ///   sort components by descending magnitude       (3 conditional swaps)
    ///   z = scale·v − (scale−1)·offset, except the smallest component
    ///   which keeps scale·v (no offset)               (this is Knighty's
    ///   formulation; the "tile-3" fold).
    /// Tracking dr = scale^n gives DE = (|z| − r0) / dr.
    /// </summary>
    private static double MengerDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, double bailout2, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        for (int i = 0; i < iter; i++)
        {
            zx = Math.Abs(zx); zy = Math.Abs(zy); zz = Math.Abs(zz);
            double t;
            if (zx - zy < 0) { t = zx; zx = zy; zy = t; }
            if (zx - zz < 0) { t = zx; zx = zz; zz = t; }
            if (zy - zz < 0) { t = zy; zy = zz; zz = t; }

            zx = scale * zx - (scale - 1.0) * ox;
            zy = scale * zy - (scale - 1.0) * oy;
            // Smallest component (zz here, after sort) is left at scale·z
            // — no subtraction — to drive the sponge holes.
            zz = scale * zz;
            if (zz - (scale - 1.0) * oz * 0.5 < 0) zz -= (scale - 1.0) * oz;

            dr *= scale;
            if (zx * zx + zy * zy + zz * zz > bailout2) break;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        // Subtract bounding sphere of the iterated cube.
        return (rFinal - 2.0) / Math.Max(Math.Abs(dr), 1e-10);
    }

    /// <summary>
    /// Sierpinski tetrahedron DE. Per iter:
    ///   3 vertex reflections (Knighty's flip-on-negative-sum trick).
    ///   scale-2 from (1,1,1).
    /// dr = scale^n drives the DE divisor.
    /// </summary>
    private static double SierpDE(double cx, double cy, double cz,
        double scale, double ox, double oy, double oz, double bailout2, int iter)
    {
        double zx = cx, zy = cy, zz = cz;
        double dr = 1.0;
        for (int i = 0; i < iter; i++)
        {
            double t;
            if (zx + zy < 0) { t = -zy; zy = -zx; zx = t; }
            if (zx + zz < 0) { t = -zz; zz = -zx; zx = t; }
            if (zy + zz < 0) { t = -zz; zz = -zy; zy = t; }

            zx = scale * zx - (scale - 1.0) * ox;
            zy = scale * zy - (scale - 1.0) * oy;
            zz = scale * zz - (scale - 1.0) * oz;

            dr *= scale;
            if (zx * zx + zy * zy + zz * zz > bailout2) break;
        }
        double rFinal = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        return (rFinal - 2.0) / Math.Max(Math.Abs(dr), 1e-10);
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
