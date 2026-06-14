// QuatMandelbrotCalculator.cs
//
// Quaternion Mandelbrot distance-estimation raymarcher. Iteration q := q² + c
// with q, c ∈ ℍ; for the Mandelbrot variant c varies per pixel — pixel
// (x, y, z) ↦ c = (x, y, z, sliceW). The orbit q starts at the origin and the
// derivative dq is taken wrt c. Chain rule (since q_{n+1} = q_n² + c):
//
//   dq_{n+1} = 2 · q_n · dq_n + 1     (1 = identity quaternion)
//   dq_0     = 0                       (q_0 = 0, so dq_0 / dc = 0)
//
// DE estimator is the same Hubbard–Douady form as QuatJulia:
//
//   DE = 0.5 · |q| · ln|q| / |dq|
//
// Camera / lighting plumbing mirrors QuatJuliaCalculator so the orbit-camera,
// theta/phi rotation, minimap suppression and field-of-view zoom-narrowing
// path all match.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class QuatMandelbrotCalculator : IFractalCalculator
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

    public QuatMandelbrotCalculator(int width, int height) => Resize(width, height);

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

        double sliceZ = FractalParameters.QMandelSliceZ;
        double sliceW = FractalParameters.QMandelSliceW;
        int deIter = Math.Max(2, FractalParameters.QMandelIterations);
        double bailout2 = Math.Max(4.0, FractalParameters.QMandelBailout);
        int maxSteps = Math.Max(16, FractalParameters.QMandelMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.QMandelEpsilon);

        // The quaternion Mandelbrot lives inside roughly the same |c| ≤ 2 ball
        // as the complex Mandelbrot — set radius 2 + ½ buffer matches QuatJulia.
        double setRadius = 2.0;
        double camDistFloor = setRadius + 0.5;
        double rawCamDist = FractalParameters.QMandelCameraDistance / Math.Max(0.05, Zoom);
        double camDist = Math.Max(camDistFloor, rawCamDist);
        double camTheta = FractalParameters.QMandelCameraTheta;
        double camPhi = FractalParameters.QMandelCameraPhi;

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
            Math.Sin(FractalParameters.QMandelLightPhi) * Math.Cos(FractalParameters.QMandelLightTheta),
            Math.Cos(FractalParameters.QMandelLightPhi),
            Math.Sin(FractalParameters.QMandelLightPhi) * Math.Sin(FractalParameters.QMandelLightTheta));

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
                    double dist = QuatMandelDE(px, py, pz, sliceZ, sliceW,
                        bailout2, deIter);
                    if (dist < eps) { hit = true; hitStep = step; break; }
                    if (tTotal > sceneRadius) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit) { ColorBuffer[idx] = ColorMap.InSetColor; continue; }

                double h = eps * 2;
                double n0 = QuatMandelDE(px + h, py, pz, sliceZ, sliceW, bailout2, deIter)
                          - QuatMandelDE(px - h, py, pz, sliceZ, sliceW, bailout2, deIter);
                double n1 = QuatMandelDE(px, py + h, pz, sliceZ, sliceW, bailout2, deIter)
                          - QuatMandelDE(px, py - h, pz, sliceZ, sliceW, bailout2, deIter);
                double n2 = QuatMandelDE(px, py, pz + h, sliceZ, sliceW, bailout2, deIter)
                          - QuatMandelDE(px, py, pz - h, sliceZ, sliceW, bailout2, deIter);
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
    /// Hubbard–Douady DE for the quaternion Mandelbrot squaring map. Per iter:
    ///   dq := 2 · q · dq + 1   (Hamilton product; 1 = identity quaternion)
    ///   q  := q² + c
    /// Quaternion components are packed (X, Y, Z, W) — same convention as
    /// QuatJuliaCalculator. The "real" slot for q² is X.
    /// </summary>
    private static double QuatMandelDE(
        double sx, double sy, double sz, double sliceZ, double sliceW,
        double bailout2, int iter)
    {
        // Pack pixel as c: (X, Y, Z, W) = (sx, sy, sliceZ, sliceW).
        // Wait — the raymarcher gives us (sx, sy, sz) as the 3D slice axes;
        // the 4th (W) is the fixed slice constant. The QMandelSliceZ field is
        // unused for the standard 3D-slice convention but kept reserved for
        // alternate slice planes. For now route the raymarched z into c.Z and
        // keep sliceZ as a future-use placeholder (suppress unused-warn).
        _ = sliceZ;
        double cx = sx, cy = sy, cz = sz, cw = sliceW;

        // q starts at the origin (Mandelbrot membership test).
        double qx = 0.0, qy = 0.0, qz = 0.0, qw = 0.0;
        // dq/dc starts at zero because q_0 = 0 has no c-dependence yet.
        double dx = 0.0, dy = 0.0, dz = 0.0, dw = 0.0;

        for (int i = 0; i < iter; i++)
        {
            // dq := 2 · q · dq + 1. Hamilton product q·dq under the
            // (X, Y, Z, W) packing — X plays the "real" slot.
            double ndx = qx * dx - qy * dy - qz * dz - qw * dw;
            double ndy = qx * dy + qy * dx + qz * dw - qw * dz;
            double ndz = qx * dz - qy * dw + qz * dx + qw * dy;
            double ndw = qx * dw + qy * dz - qz * dy + qw * dx;
            dx = 2.0 * ndx + 1.0;  // identity quaternion = (1, 0, 0, 0)
            dy = 2.0 * ndy;
            dz = 2.0 * ndz;
            dw = 2.0 * ndw;

            // q := q² + c.
            double nqx = qx * qx - qy * qy - qz * qz - qw * qw;
            double nqy = 2.0 * qx * qy;
            double nqz = 2.0 * qx * qz;
            double nqw = 2.0 * qx * qw;
            qx = nqx + cx;
            qy = nqy + cy;
            qz = nqz + cz;
            qw = nqw + cw;

            double r2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (r2 > bailout2) break;
        }

        double q2 = qx * qx + qy * qy + qz * qz + qw * qw;
        double d2 = dx * dx + dy * dy + dz * dz + dw * dw;
        if (d2 < 1e-30) return 0.0;
        if (q2 < 1.0) return 0.0; // inside the set — surface hit.
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
