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
// norm as a conservative spectral-radius proxy. Final DE: 0.5 · r / |J|
// (Lipschitz form). Hubbard–Douady's log(r)·r/|J| only holds for power maps
// z → z^p + c; arbitrary user steps (trig, hyperbolic, polynomial mixes)
// do not have power-law growth, so the log factor distorts surfaces.
//
// Cost: 4× delegate calls per DE iter (vs 1× for the heuristic path). For
// the typical 96-step raymarch × 8 DE iters × 4 normal probes per pixel
// this is the dominant work item — expect frame times in the 30–60 s range
// on midrange CPUs at 800×600. Accuracy is the trade — surfaces stay
// geometrically correct for highly non-conformal maps where the Lipschitz
// proxy would over- or under-estimate.
//
// Surface normals are forward differences on DE field (3 extra probes per
// shaded pixel; base value reused from raymarch hit).

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
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
            var fn = result.ReturnValue;

            // Smoke test: invoke once with finite inputs; reject if it throws
            // or returns non-finite components. Lets the raymarch inner loop
            // drop its try/catch.
            try
            {
                var probe = fn(Vec3.Zero, new Vec3(0.5, 0.5, 0.5), 0);
                if (!double.IsFinite(probe.X) || !double.IsFinite(probe.Y) || !double.IsFinite(probe.Z))
                {
                    LastError = "Step function returned non-finite components on probe input.";
                    _compiled = null;
                    return;
                }
            }
            catch (Exception probeEx)
            {
                LastError = $"Step function threw on probe: {probeEx.Message}";
                _compiled = null;
                return;
            }

            _compiled = fn;
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
        double cullRadius = Math.Max(0.1, FractalParameters.UserBulbCullRadius);
        double cullRadiusSq = cullRadius * cullRadius;

        double camDist = FractalParameters.UserBulbCameraDistance / Math.Max(0.05, Zoom);
        double camTheta = FractalParameters.UserBulbCameraTheta;
        double camPhi = FractalParameters.UserBulbCameraPhi;

        // Orbit camera around target = (CenterX, -CenterY, 0). CenterX/Y is the
        // user pan in world units. Camera distance from target shrinks with
        // Zoom so the same world point stays centered on screen at every zoom
        // level. Previous build added pan to ray-angle u/v instead, which made
        // the apparent center drift as camDist changed.
        double targetX = CenterX;
        double targetY = -CenterY;
        double targetZ = 0.0;

        double camX = targetX + camDist * Math.Sin(camPhi) * Math.Cos(camTheta);
        double camY = targetY + camDist * Math.Cos(camPhi);
        double camZ = targetZ + camDist * Math.Sin(camPhi) * Math.Sin(camTheta);

        var fwd = Normalize3(targetX - camX, targetY - camY, targetZ - camZ);
        const double worldUpX = 0, worldUpY = 1, worldUpZ = 0;
        var right = Normalize3(
            fwd.Y * worldUpZ - fwd.Z * worldUpY,
            fwd.Z * worldUpX - fwd.X * worldUpZ,
            fwd.X * worldUpY - fwd.Y * worldUpX);
        var up = (
            X: right.Y * fwd.Z - right.Z * fwd.Y,
            Y: right.Z * fwd.X - right.X * fwd.Z,
            Z: right.X * fwd.Y - right.Y * fwd.X);

        double aspect = (double)width / height;
        double fovScale = Math.Tan(0.5 * Math.PI / 3.0); // 60° FOV

        var light = Normalize3(
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Cos(FractalParameters.UserBulbLightTheta),
            Math.Cos(FractalParameters.UserBulbLightPhi),
            Math.Sin(FractalParameters.UserBulbLightPhi) * Math.Sin(FractalParameters.UserBulbLightTheta));

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double v = (1.0 - 2.0 * (y + 0.5) / height) * fovScale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double u = (2.0 * (x + 0.5) / width - 1.0) * fovScale * aspect;
                double rdx = right.X * u + up.X * v + fwd.X;
                double rdy = right.Y * u + up.Y * v + fwd.Y;
                double rdz = right.Z * u + up.Z * v + fwd.Z;
                var dn = Normalize3(rdx, rdy, rdz);
                rdx = dn.X; rdy = dn.Y; rdz = dn.Z;

                // Bounding sphere clip: ray vs sphere centered on target,
                // radius = cullRadius. Skip raymarch entirely if miss.
                double ocx = camX - targetX;
                double ocy = camY - targetY;
                double ocz = camZ - targetZ;
                double bSphere = ocx * rdx + ocy * rdy + ocz * rdz;
                double cSphere = ocx * ocx + ocy * ocy + ocz * ocz - cullRadiusSq;
                double discSphere = bSphere * bSphere - cSphere;
                int idx = rowBase + x;
                if (discSphere < 0)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    continue;
                }
                double sqrtDisc = Math.Sqrt(discSphere);
                double tEnter = -bSphere - sqrtDisc;
                double tExit = -bSphere + sqrtDisc;
                if (tExit < 0)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    continue;
                }
                double tStart = Math.Max(0.0, tEnter);

                double px = camX + rdx * tStart;
                double py = camY + rdy * tStart;
                double pz = camZ + rdz * tStart;
                double tTotal = tStart;
                bool hit = false;
                int hitStep = 0;
                double hitDist = 0.0;

                for (int step = 0; step < maxSteps; step++)
                {
                    if (ct.IsCancellationRequested) return;
                    double dist = UserBulbDE(fn, px, py, pz, deIter, bailout, jacH);
                    if (dist < eps)
                    {
                        hit = true;
                        hitStep = step;
                        hitDist = dist;
                        break;
                    }
                    if (tTotal > tExit + 1.0) break;
                    px += rdx * dist; py += rdy * dist; pz += rdz * dist;
                    tTotal += dist;
                }

                if (!hit)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    continue;
                }

                // Forward-diff normals: reuse hitDist as f(p), 3 extra probes.
                double h = eps * 2;
                double invH = 1.0 / h;
                double n0 = (UserBulbDE(fn, px + h, py, pz, deIter, bailout, jacH) - hitDist) * invH;
                double n1 = (UserBulbDE(fn, px, py + h, pz, deIter, bailout, jacH) - hitDist) * invH;
                double n2 = (UserBulbDE(fn, px, py, pz + h, deIter, bailout, jacH) - hitDist) * invH;
                var nrm = Normalize3(n0, n1, n2);

                double diffuse = Math.Max(0.0, nrm.X * light.X + nrm.Y * light.Y + nrm.Z * light.Z);
                double ambient = 0.15;
                double shade = ambient + diffuse * (1.0 - ambient);

                // Color driver: raymarch step count + depth. See
                // MandelbulbCalculator for rationale (non-3D gradient themes
                // need a varying scalar across surface).
                float smooth = (float)hitStep * (256f / Math.Max(1, maxSteps))
                             + (float)(tTotal * 4.0);
                uint baseColor = (uint)ColorMap.Map(smooth, 0f, 256, (float)nrm.X, (float)nrm.Y);
                byte R = (byte)Math.Clamp(((baseColor >> 16) & 0xFF) * shade, 0, 255);
                byte G = (byte)Math.Clamp(((baseColor >> 8) & 0xFF) * shade, 0, 255);
                byte B = (byte)Math.Clamp((baseColor & 0xFF) * shade, 0, 255);
                ColorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }

    /// <summary>
    /// DE for user-supplied step. Iterates z_{n+1} = fn(z_n, c, n) from z = 0
    /// with c = world-space sample point. Numerical Jacobian: three parallel
    /// trajectories run with c perturbed by +h on each axis. Column lengths
    /// of (z_perturbed − z_base) / h bound dz/dc; max column length acts as
    /// the spectral-radius proxy. Final DE: 0.5 · r / |J| (Lipschitz form,
    /// works for arbitrary growth profiles).
    ///
    /// Cost = 4× delegate calls per DE iteration (1 base + 3 perturbed).
    ///
    /// Caller (Compile) smoke-tests the delegate for throw/non-finite so this
    /// hot loop omits try/catch; non-finite r breaks early.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double UserBulbDE(
        Func<Vec3, Vec3, int, Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double h)
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
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z  = fn(z,  cBase, i);
            zx = fn(zx, cPx,   i);
            zy = fn(zy, cPy,   i);
            zz = fn(zz, cPz,   i);
        }

        // Forward-diff Jacobian column lengths: |∂z/∂c_axis| ≈ |z_pert − z| / h.
        double j0 = (zx - z).Length / h;
        double j1 = (zy - z).Length / h;
        double j2 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), j2);

        return 0.5 * r / Math.Max(dr, 1e-10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double X, double Y, double Z) Normalize3(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-10) return (0.0, 0.0, 0.0);
        double inv = 1.0 / len;
        return (x * inv, y * inv, z * inv);
    }
}
