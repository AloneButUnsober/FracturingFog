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

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class QuatJuliaCalculator : IFractalCalculator
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
        int width = Width;
        int height = Height;

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

        double sceneRadius = camDist + setRadius * 2.0 + 4.0;

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
                if (!hit) { ColorBuffer[idx] = ColorMap.InSetColor; continue; }

                double h = eps * 2;
                double n0 = QuatJuliaDE(px + h, py, pz, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px - h, py, pz, sliceW, cx, cy, cz, cw, bailout2, deIter);
                double n1 = QuatJuliaDE(px, py + h, pz, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px, py - h, pz, sliceW, cx, cy, cz, cw, bailout2, deIter);
                double n2 = QuatJuliaDE(px, py, pz + h, sliceW, cx, cy, cz, cw, bailout2, deIter)
                          - QuatJuliaDE(px, py, pz - h, sliceW, cx, cy, cz, cw, bailout2, deIter);
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
    /// Hubbard–Douady DE for the quaternion squaring map. Per iter:
    ///   dq := 2 · q · dq    (Hamilton product, derivative of q²)
    ///   q  := q² + c
    /// Exit when |q|² &gt; bailout (orbit has escaped). Distance estimate:
    ///   DE = 0.5 · |q| · ln|q| / |dq|.
    /// Quaternion components are stored as (W, X, Y, Z) where Hamilton
    /// product is the standard (a + bi + cj + dk)·(e + fi + gj + hk) form.
    /// </summary>
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
