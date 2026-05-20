// AttractorCalculator.cs
//
// Iterates a 2D / 3D strange attractor map and accumulates a per-pixel hit
// density buffer. Log-tone-maps the density to a color through the active
// IColorMap. Built-in attractors: Clifford, De Jong, Hopalong, Lorenz (3D).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class AttractorCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    private uint[] _hits = Array.Empty<uint>();

    /// <summary>
    /// Known-good default (a, b, c, d) for each attractor preset. Picked from
    /// canonical published parameter sets. Called by the params dialog when
    /// the preset combo changes so the user sees a meaningful render instead
    /// of a single fixed point.
    /// </summary>
    public static (double a, double b, double c, double d) DefaultParams(string preset) => preset switch
    {
        "Clifford" => (-1.4, 1.6, 1.0, 0.7),
        "De Jong"  => (1.4, -2.3, 2.4, -2.1),
        "Hopalong" => (2.0, 1.0, 0.0, 0.0),
        "Lorenz"   => (10.0, 28.0, 8.0 / 3.0, 0.0),
        _          => (-1.4, 1.6, 1.0, 0.7),
    };

    public AttractorCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        _hits = new uint[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(_hits);

        string name = FractalParameters.AttractorPresetName ?? "Clifford";
        double a = FractalParameters.AttractorA;
        double b = FractalParameters.AttractorB;
        double c = FractalParameters.AttractorC;
        double d = FractalParameters.AttractorD;
        int total = FractalParameters.AttractorIterations;
        int width = Width;
        int height = Height;

        // First pass: warm-up + bbox.
        double minX, maxX, minY, maxY;
        ComputeBBox(name, a, b, c, d, out minX, out maxX, out minY, out maxY);
        double spanX = Math.Max(1e-9, maxX - minX);
        double spanY = Math.Max(1e-9, maxY - minY);
        double worldSpan = Math.Max(spanX, spanY);
        double mapFit = 3.0 / worldSpan;
        double mx = (minX + maxX) * 0.5;
        double my = (minY + maxY) * 0.5;
        double pixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;
        double centerX = CenterX;
        double centerY = CenterY;

        int threads = Math.Max(1, Environment.ProcessorCount);
        int perThread = total / threads;
        var localBuffers = new uint[threads][];
        for (int t = 0; t < threads; t++) localBuffers[t] = new uint[width * height];

        Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, t =>
        {
            if (ct.IsCancellationRequested) return;
            double x = 0.1, y = 0.0, z = 0.0;
            // Warm-up.
            for (int i = 0; i < 200; i++) Step(name, ref x, ref y, ref z, a, b, c, d);

            var local = localBuffers[t];
            for (int i = 0; i < perThread; i++)
            {
                Step(name, ref x, ref y, ref z, a, b, c, d);
                double worldX = (x - mx) * mapFit;
                double worldY = -(y - my) * mapFit;
                int ix = (int)((worldX - centerX) / pixelScale + width * 0.5);
                int iy = (int)((worldY - centerY) / pixelScale + height * 0.5);
                if ((uint)ix < (uint)width && (uint)iy < (uint)height)
                    local[iy * width + ix]++;
            }
        });

        for (int t = 0; t < threads; t++)
        {
            var local = localBuffers[t];
            for (int i = 0; i < _hits.Length; i++) _hits[i] += local[i];
        }

        uint maxHit = 0;
        for (int i = 0; i < _hits.Length; i++) if (_hits[i] > maxHit) maxHit = _hits[i];
        double invLog = maxHit > 1 ? 1.0 / Math.Log(maxHit + 1) : 1.0;

        ColorMap.MaxIterations = 256;
        for (int i = 0; i < _hits.Length; i++)
        {
            uint h = _hits[i];
            if (h == 0) { ColorBuffer[i] = ColorMap.InSetColor; continue; }
            double norm = Math.Log(h + 1) * invLog;
            ColorBuffer[i] = (uint)ColorMap.Map((float)(norm * 256), 0f, 256);
        }
    }

    private static void ComputeBBox(string name, double a, double b, double c, double d,
        out double minX, out double maxX, out double minY, out double maxY)
    {
        double x = 0.1, y = 0.0, z = 0.0;
        for (int i = 0; i < 500; i++) Step(name, ref x, ref y, ref z, a, b, c, d);
        minX = maxX = x; minY = maxY = y;
        for (int i = 0; i < 50_000; i++)
        {
            Step(name, ref x, ref y, ref z, a, b, c, d);
            if (x < minX) minX = x; else if (x > maxX) maxX = x;
            if (y < minY) minY = y; else if (y > maxY) maxY = y;
        }
    }

    private static void Step(string name, ref double x, ref double y, ref double z,
        double a, double b, double c, double d)
    {
        switch (name)
        {
            case "Clifford":
            {
                double nx = Math.Sin(a * y) + c * Math.Cos(a * x);
                double ny = Math.Sin(b * x) + d * Math.Cos(b * y);
                x = nx; y = ny;
                break;
            }
            case "De Jong":
            {
                double nx = Math.Sin(a * y) - Math.Cos(b * x);
                double ny = Math.Sin(c * x) - Math.Cos(d * y);
                x = nx; y = ny;
                break;
            }
            case "Hopalong":
            {
                double nx = y - Math.Sign(x) * Math.Sqrt(Math.Abs(b * x - c));
                double ny = a - x;
                x = nx; y = ny;
                break;
            }
            case "Lorenz":
            {
                // Lorenz 3D: ẋ=σ(y-x), ẏ=x(ρ-z)-y, ż=xy-βz. RK4 step, project (x, z) to 2D.
                const double sigma = 10.0, rho = 28.0, beta = 8.0 / 3.0;
                const double dt = 0.005;
                double dx1 = sigma * (y - x);
                double dy1 = x * (rho - z) - y;
                double dz1 = x * y - beta * z;
                double xk2 = x + dx1 * dt * 0.5, yk2 = y + dy1 * dt * 0.5, zk2 = z + dz1 * dt * 0.5;
                double dx2 = sigma * (yk2 - xk2);
                double dy2 = xk2 * (rho - zk2) - yk2;
                double dz2 = xk2 * yk2 - beta * zk2;
                double xk3 = x + dx2 * dt * 0.5, yk3 = y + dy2 * dt * 0.5, zk3 = z + dz2 * dt * 0.5;
                double dx3 = sigma * (yk3 - xk3);
                double dy3 = xk3 * (rho - zk3) - yk3;
                double dz3 = xk3 * yk3 - beta * zk3;
                double xk4 = x + dx3 * dt, yk4 = y + dy3 * dt, zk4 = z + dz3 * dt;
                double dx4 = sigma * (yk4 - xk4);
                double dy4 = xk4 * (rho - zk4) - yk4;
                double dz4 = xk4 * yk4 - beta * zk4;
                x += (dx1 + 2 * dx2 + 2 * dx3 + dx4) * dt / 6.0;
                double newY = y + (dy1 + 2 * dy2 + 2 * dy3 + dy4) * dt / 6.0;
                z += (dz1 + 2 * dz2 + 2 * dz3 + dz4) * dt / 6.0;
                y = z; // project Z onto y axis for 2D rendering
                z = newY;
                break;
            }
            default:
            {
                double nx = Math.Sin(a * y) + c * Math.Cos(a * x);
                double ny = Math.Sin(b * x) + d * Math.Cos(b * y);
                x = nx; y = ny;
                break;
            }
        }
    }
}
