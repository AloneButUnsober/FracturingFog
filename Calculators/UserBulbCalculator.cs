// UserBulbCalculator.cs
//
// CPU distance-estimation raymarcher for a 3D escape-time fractal whose
// per-iteration step function is supplied at runtime as a C# expression body,
// compiled via Roslyn scripting. Conceptually the 3D analogue of
// UserEquationCalculator: that one drives 2D escape-time over Complex; this
// one drives Mandelbulb-style raymarched 3D over Vec3.
//
// User source signature (wrapped before compile):
//   Vec3 Step(Vec3 z, Vec3 c, int n)  -> returns new z
//
// DE estimation: no closed-form |dz/dc| for an arbitrary user step, so we
// approximate the Jacobian numerically. Per DE call, four iteration
// trajectories run in lockstep — base c plus three perturbed-c trajectories
// (c + h·êx, c + h·êy, c + h·êz). After iteration, the three column lengths
// (|z_perturbed − z_base| / h) bound the Jacobian; we take the max column
// norm as a conservative spectral-radius proxy. Final DE: 0.5 · log(r)·r/|J|
// (Hubbard–Douady form, identical to Mandelbulb's analytic version).
//
// Cost: 4× delegate calls per DE iter (vs 1× for the heuristic path). For
// the typical 96-step raymarch × 8 DE iters × 4 normal probes per pixel
// this is the dominant work item — expect frame times in the 30–60 s range
// on midrange CPUs at 800×600. Accuracy is the trade — surfaces stay
// geometrically correct for highly non-conformal maps where the Lipschitz
// proxy would over- or under-estimate.
//
// Surface normals are central differences on DE field — three extra raymarch
// DE evaluations per shaded pixel (Mandelbulb-style), matching the existing
// MandelbulbCalculator visual.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class UserBulbCalculator : IFractalCalculator
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

    public string LastError { get; private set; } = string.Empty;
    public bool IsCompiled => _compiled != null;

    private Func<Vec3, Vec3, int, Vec3>? _compiled;
    private string _compiledSource = string.Empty;

    public UserBulbCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    /// <summary>
    /// Compiles the user source. Body of:
    ///   Vec3 Step(Vec3 z, Vec3 c, int n) { ... }
    /// Available APIs: full System.Math (re-exported via static import) and
    /// Vec3 (FracturingFog.Models). Vec3 exposes Sin/Cos/Sinh/Cosh/etc as
    /// component-wise statics so users can write Vec3.Sin(z) or just use
    /// Math.Sin(z.X) inside a `new Vec3(...)`.
    /// </summary>
    public void Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            _compiled = null;
            LastError = "Source is empty";
            return;
        }

        try
        {
            string code = WrapUserSource(source);
            var options = ScriptOptions.Default
                .AddReferences(
                    typeof(Vec3).Assembly,
                    typeof(Complex).Assembly,
                    typeof(object).Assembly,
                    typeof(Math).Assembly)
                .AddImports("System", "System.Numerics", "System.Math", "FracturingFog.Models");

            var script = CSharpScript.Create<Func<Vec3, Vec3, int, Vec3>>(code, options);
            var compilation = script.Compile();
            if (compilation.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var diag in compilation)
                    sb.AppendLine(diag.ToString());
                LastError = sb.ToString();
                _compiled = null;
                return;
            }
            var result = script.RunAsync().GetAwaiter().GetResult();
            _compiled = result.ReturnValue;
            _compiledSource = source;
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _compiled = null;
        }
    }

    private static string WrapUserSource(string body)
    {
        string wrappedBody = body.Contains("return") ? body : $"return {body};";
        return $@"
Vec3 __Step(Vec3 z, Vec3 c, int n)
{{
    {wrappedBody}
}}
return (Func<Vec3, Vec3, int, Vec3>)((Vec3 z, Vec3 c, int n) => __Step(z, c, n));
";
    }

    public void Calculate(CancellationToken ct = default)
    {
        // Lazy compile from FractalParameters if not yet done.
        if (_compiled == null)
        {
            if (!string.IsNullOrWhiteSpace(FractalParameters.UserBulbSource)
                && FractalParameters.UserBulbSource != _compiledSource)
            {
                Compile(FractalParameters.UserBulbSource);
            }
        }

        var fn = _compiled;
        System.Diagnostics.Debug.WriteLine(
            $"[UserBulb] Calculate W={Width} H={Height} compiled={fn != null} " +
            $"srcLen={(FractalParameters.UserBulbSource ?? string.Empty).Length} " +
            $"lastError='{LastError}'");
        if (fn == null)
        {
            Array.Clear(ColorBuffer);
            uint bg = ColorMap.InSetColor;
            for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;
            return;
        }

        ColorMap.MaxIterations = 256;
        int width = Width;
        int height = Height;
        int deIter = Math.Max(2, FractalParameters.UserBulbIterations);
        int maxSteps = Math.Max(16, FractalParameters.UserBulbMaxSteps);
        double eps = Math.Max(1e-5, FractalParameters.UserBulbEpsilon);
        double bailout = Math.Max(1.0, FractalParameters.UserBulbBailout);
        double jacH = Math.Max(1e-8, FractalParameters.UserBulbJacobianH);

        double camDist = FractalParameters.UserBulbCameraDistance / Math.Max(0.05, Zoom);
        double camTheta = FractalParameters.UserBulbCameraTheta;
        double camPhi = FractalParameters.UserBulbCameraPhi;

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
        double fovScale = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV

        double panU = CenterX;
        double panV = -CenterY;

        double[] light = Normalize3(
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Cos(FractalParameters.UserBulbLightTheta),
            Math.Cos(FractalParameters.UserBulbLightPhi),
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Sin(FractalParameters.UserBulbLightTheta));

        int hits = 0;
        int total = 0;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale + panV;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                System.Threading.Interlocked.Increment(ref total);
                double u = (2.0 * (x + 0.5) / width - 1.0) * fovScale * aspect + panU;
                double rdx = right[0] * u + up[0] * v + fwd[0];
                double rdy = right[1] * u + up[1] * v + fwd[1];
                double rdz = right[2] * u + up[2] * v + fwd[2];
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn[0]; rdy = dn[1]; rdz = dn[2];

                double px = camX, py = camY, pz = camZ;
                double tTotal = 0;
                bool hit = false;
                double iterEscape = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    if (ct.IsCancellationRequested) return;
                    double dist = UserBulbDE(fn, px, py, pz, deIter, bailout, jacH, out iterEscape);
                    if (dist < eps)
                    {
                        hit = true;
                        break;
                    }
                    if (tTotal > 12.0) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                int idx = rowBase + x;
                if (!hit)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    continue;
                }
                System.Threading.Interlocked.Increment(ref hits);

                double h = eps * 2;
                double n0 = UserBulbDE(fn, px + h, py, pz, deIter, bailout, jacH, out _) - UserBulbDE(fn, px - h, py, pz, deIter, bailout, jacH, out _);
                double n1 = UserBulbDE(fn, px, py + h, pz, deIter, bailout, jacH, out _) - UserBulbDE(fn, px, py - h, pz, deIter, bailout, jacH, out _);
                double n2 = UserBulbDE(fn, px, py, pz + h, deIter, bailout, jacH, out _) - UserBulbDE(fn, px, py, pz - h, deIter, bailout, jacH, out _);
                var nrm = Normalize3(n0, n1, n2);

                double diffuse = Math.Max(0.0, nrm[0] * light[0] + nrm[1] * light[1] + nrm[2] * light[2]);
                double ambient = 0.15;
                double shade = ambient + diffuse * (1.0 - ambient);

                float smooth = (float)(iterEscape / Math.Max(1, deIter)) * 200f;
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm[0], (float)nrm[1]);
                byte R = (byte)Math.Clamp(((baseColor >> 16) & 0xFF) * shade, 0, 255);
                byte G = (byte)Math.Clamp(((baseColor >> 8) & 0xFF) * shade, 0, 255);
                byte B = (byte)Math.Clamp((baseColor & 0xFF) * shade, 0, 255);
                ColorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
        System.Diagnostics.Debug.WriteLine(
            $"[UserBulb] done hits={hits}/{total} ({(total > 0 ? 100.0*hits/total : 0):F1}%)");
    }

    /// <summary>
    /// DE for user-supplied step. Iterates z_{n+1} = fn(z_n, c, n) from z = 0
    /// with c = world-space sample point. Numerical Jacobian: three parallel
    /// trajectories run with c perturbed by +h on each axis. Column lengths
    /// of (z_perturbed − z_base) / h bound dz/dc; max column length acts as
    /// the spectral-radius proxy. Final DE: 0.5 · log(r) · r / |J|.
    ///
    /// Cost = 4× delegate calls per DE iteration (1 base + 3 perturbed).
    /// </summary>
    private static double UserBulbDE(
        Func<Vec3, Vec3, int, Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double h,
        out double escape)
    {
        var cBase = new Vec3(cx, cy, cz);
        var cPx = new Vec3(cx + h, cy, cz);
        var cPy = new Vec3(cx, cy + h, cz);
        var cPz = new Vec3(cx, cy, cz + h);

        var z = Vec3.Zero;
        var zx = Vec3.Zero;
        var zy = Vec3.Zero;
        var zz = Vec3.Zero;
        double r = 0.0;
        escape = iter;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (r > bailout) { escape = i; break; }
            try
            {
                z  = fn(z,  cBase, i);
                zx = fn(zx, cPx,   i);
                zy = fn(zy, cPy,   i);
                zz = fn(zz, cPz,   i);
            }
            catch
            {
                escape = iter;
                r = bailout * 2;
                break;
            }
        }

        // Forward-diff Jacobian column lengths: |∂z/∂c_axis| ≈ |z_pert − z| / h.
        double j0 = (zx - z).Length / h;
        double j1 = (zy - z).Length / h;
        double j2 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), j2);

        return 0.5 * Math.Log(Math.Max(r, 1e-10)) * r / Math.Max(dr, 1e-10);
    }

    private static double[] Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return new[] { 0.0, 0.0, 0.0 };
        return new[] { x / len, y / len, z / len };
    }
}
