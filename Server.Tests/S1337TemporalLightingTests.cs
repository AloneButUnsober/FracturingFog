// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1337 — temporal reuse of the 3D views' AO and soft shadows while the view moves
// (TemporalLighting, ShadingPipeline.Shade<TDe>, the CPU raymarch families and the
// relief preview). Invariants:
//   • without a cache, and in a settle frame, every family renders exactly the full
//     frame (byte for byte), and a first frame (no history) does too;
//   • while the view moves, pixels do reuse, and no value is ever older than the
//     period; frames stay close to the full frame;
//   • an independent analytic scene (a sphere over a floor, smooth AO / shadows):
//     every reused AO / shadow value is within a small tolerance of the exact one,
//     including the floor the moving camera uncovers from behind the sphere (a value
//     carried over from the sphere — a ghost — would be far off);
//   • a lighting or fractal change, DoF, a stereo eye or an AOV view reuse nothing;
//   • the live host: after a moving frame it renders one settle frame, which is the
//     full frame exactly; with the option off nothing changes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1337TemporalLightingTests(ITestOutputHelper output)
{
    private const int W = 128, H = 96;

    public enum Family { Mandelbulb, Mandelbox, Menger, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion, DualOrbitVolume, UserBulb }

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) d.Add(f);
        return d;
    }

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = false;
        fx.AoSamples = 5; fx.AoStrength = 1.0;
        fx.ShadowSteps = 32; fx.ShadowSoftK = 16;
        fx.SsaoSamples = 0;
        return fx;
    }

    private static IFractalCalculator Calc(Family fam, double zoom, double pan, LightingFxData fx, Action<FractalParameters>? tweak = null)
    {
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        tweak?.Invoke(fp);
        IFractalCalculator c = fam switch
        {
            Family.Mandelbulb      => new MandelbulbCalculator(W, H),
            Family.Mandelbox       => new MandelboxCalculator(W, H),
            Family.Menger          => new KifsCalculator(W, H),
            Family.QuatJulia       => new QuatJuliaCalculator(W, H),
            Family.QuatMandelbrot  => new QuatMandelbrotCalculator(W, H),
            Family.Kleinian        => new KleinianCalculator(W, H),
            Family.Bicomplex       => new BicomplexMandelbrotCalculator(W, H),
            Family.Coquaternion    => new CoquaternionMandelbrotCalculator(W, H),
            Family.DualOrbitVolume => new DualOrbitVolumeCalculator(W, H),
            _                      => new UserBulbCalculator(W, H),
        };
        c.ColorMap = ColorPalette.BuiltIns[0];
        ((dynamic)c).FractalParameters = fp;
        c.Zoom = zoom;
        c.CenterX = pan;
        return c;
    }

    private static uint[] Render(Family fam, double zoom, double pan, LightingFxData fx, TemporalLighting? cache = null,
                                 bool settle = false, Action<FractalParameters>? tweak = null)
    {
        var c = Calc(fam, zoom, pan, fx, tweak);
        if (cache != null) { cache.BeginRender(); cache.Settle = settle; }
        using (TemporalLighting.Use(cache)) c.Calculate(CancellationToken.None);
        return c.ColorBuffer.Take(W * H).ToArray();
    }

    private static int MaxChannelDiff(uint a, uint b)
        => Math.Max(Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)),
           Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)),
                    Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_Family_Renders_The_Full_Frame_Without_Reuse(Family fam)
    {
        var fx = Fx();
        var full0 = Render(fam, 1.0, 0.0, fx);
        var full1 = Render(fam, 1.02, 0.01, fx);
        var cache = new TemporalLighting();
        // First frame: nothing to reuse — exactly the full frame, and the family is wired.
        Assert.Equal(full0, Render(fam, 1.0, 0.0, fx, cache));
        Assert.True(cache.LastComputed > W * H / 20, $"{fam}: the frame never reached the cache ({cache.LastComputed} pixels)");
        Assert.Equal(0, cache.LastReused);
        // A moving frame reuses (precondition), the settle frame is exactly the full frame.
        var moving = Render(fam, 1.02, 0.01, fx, cache);
        Assert.True(cache.LastReused > cache.LastComputed / 20, $"{fam}: only {cache.LastReused} reused, {cache.LastComputed} computed");
        Assert.True(cache.LastFrameApproximate);
        Assert.Equal(full1, Render(fam, 1.02, 0.01, fx, cache, settle: true));
        Assert.Equal(0, cache.LastReused);
        Assert.False(cache.LastFrameApproximate);
        int diff = Enumerable.Range(0, W * H).Count(i => moving[i] != full1[i]);
        output.WriteLine($"{fam}: moving frame differs from full on {diff} of {W * H} pixels");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Moving_Views_Reuse_With_Bounded_Age_And_Stay_Close(Family fam)
    {
        var fx = Fx();
        var cache = new TemporalLighting();
        double sumErr = 0; long px = 0; int bigErr = 0; long reused = 0, computed = 0;
        for (int f = 0; f < 8; f++)
        {
            double zoom = 1.0 * Math.Pow(1.01, f), pan = 0.004 * f;
            var t = Render(fam, zoom, pan, fx, cache);
            if (f == 0) continue;
            reused += cache.LastReused; computed += cache.LastComputed;
            for (int i = 0; i < W * H; i++)
                if (cache.HasData(i))
                    Assert.True(cache.AgeAt(i) < cache.Period, $"{fam} frame {f}: pixel {i} is {cache.AgeAt(i)} frames old");
            var full = Render(fam, zoom, pan, fx);
            for (int i = 0; i < W * H; i++)
            {
                int e = MaxChannelDiff(t[i], full[i]);
                sumErr += e; px++;
                if (e > 32) bigErr++;
            }
        }
        double mean = sumErr / px, share = reused / (double)Math.Max(1, reused + computed);
        output.WriteLine($"{fam}: reused {share:P0} of shaded pixels, mean error {mean:F2} levels, {bigErr} of {px} pixels > 32");
        Assert.True(share > 0.05, $"{fam}: reused only {share:P1}");
        Assert.True(mean < 3.0, $"{fam}: mean error {mean:F2} levels");
        Assert.True(bigErr < px / 20, $"{fam}: {bigErr} pixels more than 32 levels off");
    }

    [Fact]
    public void Lighting_Or_Fractal_Changes_And_Unusable_Frames_Reuse_Nothing()
    {
        const Family fam = Family.Mandelbulb;
        var fx = Fx();
        var cache = new TemporalLighting();
        void Arm() { Render(fam, 1.0, 0.0, fx, cache); Render(fam, 1.01, 0.0, fx, cache); Assert.True(cache.LastReused > 0); }

        Arm();
        var moved = fx; moved.Light1.Theta += 0.3;
        Assert.Equal(Render(fam, 1.01, 0.0, moved), Render(fam, 1.01, 0.0, moved, cache));
        Assert.Equal(0, cache.LastReused);

        Arm();
        Assert.Equal(Render(fam, 1.01, 0.0, fx, tweak: p => p.BulbPower = 7.5), Render(fam, 1.01, 0.0, fx, cache, tweak: p => p.BulbPower = 7.5));
        Assert.Equal(0, cache.LastReused);

        // DoF and AOV views: two consecutive moving frames each (the first has no
        // history under the new scene key; the second would reuse if it were armed).
        var dof = fx; dof.DofThinLens = true; dof.DofAperture = 0.05; dof.DofSamples = 4;
        var aov = fx; aov.DebugAov = AovView.AmbientOcclusion;
        foreach (var (label, v) in new[] { ("DoF", dof), ("AOV", aov) })
        {
            Arm();
            for (int f = 0; f < 2; f++)
            {
                double z = 1.02 + 0.01 * f;
                Assert.Equal(Render(fam, z, 0.0, v), Render(fam, z, 0.0, v, cache));
                Assert.True(cache.LastReused == 0, $"{label} frame {f} reused {cache.LastReused}");
                Assert.True(cache.LastComputed == 0, $"{label} frame {f} armed the cache ({cache.LastComputed} stored)");
            }
        }

        Arm();
        var eye = (MandelbulbCalculator)Calc(fam, 1.01, 0.0, fx);
        eye.StereoEyeOffset = 0.03;
        cache.BeginRender();
        using (TemporalLighting.Use(cache)) eye.Calculate(CancellationToken.None);
        Assert.Equal(0, cache.LastReused);
        Assert.Equal(0, cache.LastComputed);
    }

    // ── An analytic scene: a unit-diameter sphere resting on the floor y = 0 ──────

    private readonly struct SphereOnFloor : IDistanceEstimator
    {
        public double Evaluate(double x, double y, double z)
            => Math.Min(Math.Sqrt(x * x + (y - 0.5) * (y - 0.5) + z * z) - 0.5, y);
    }

    /// <summary>One frame of the scene from a camera at (camX, 1.2, −3) looking at
    /// (camX·0.6, 0.35, 0): per pixel the AO and light-1 shadow Shade used (NaN = no hit).</summary>
    private const int SceneW = 160, SceneH = 120;

    private static (float[] Ao, float[] Sh) Scene(double camX, in LightingFxData fx, TemporalLighting? cache)
    {
        const int w = SceneW, h = SceneH;
        var de = new SphereOnFloor();
        double ox = camX, oy = 1.2, oz = -3.0;
        double tx = camX * 0.6 - ox, ty = 0.35 - oy, tz = 0 - oz;
        double fl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
        double fX = tx / fl, fY = ty / fl, fZ = tz / fl;
        double rX = fZ, rY = 0, rZ = -fX;   // right = up(0,1,0) x fwd, normalised
        double rl = Math.Sqrt(rX * rX + rZ * rZ); rX /= rl; rZ /= rl;
        double uX = fY * rZ - fZ * rY, uY = fZ * rX - fX * rZ, uZ = fX * rY - fY * rX;
        double fov = Math.Tan(0.5 * Math.PI / 3.0), aspect = (double)w / h;
        var comp = new ShadingPipeline.ShadeComponents[w * h];
        var ao = new float[w * h]; var sh = new float[w * h];
        Array.Fill(ao, float.NaN); Array.Fill(sh, float.NaN);
        if (cache != null) cache.BeginRender();
        using (TemporalLighting.Use(cache))
        {
            TemporalLighting.BeginFrame(w, h, new PinholeCamera(ox, oy, oz, rX, rY, rZ, uX, uY, uZ, fX, fY, fZ, fov, aspect, 0, 0),
                TemporalLighting.SceneKey(in fx, 0.02, in de), usable: true);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (2.0 * (x + 0.5) / w - 1.0) * fov * aspect, v = (1.0 - 2.0 * (y + 0.5) / h) * fov;
                double dx = rX * u + uX * v + fX, dy = rY * u + uY * v + fY, dz = rZ * u + uZ * v + fZ;
                double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz); dx /= dl; dy /= dl; dz /= dl;
                double t = 0; bool hit = false;
                for (int s = 0; s < 400 && t < 20; s++)
                {
                    double d = de.Evaluate(ox + dx * t, oy + dy * t, oz + dz * t);
                    if (d < 1e-5) { hit = true; break; }
                    t += d;
                }
                if (!hit) continue;
                double px = ox + dx * t, py = oy + dy * t, pz = oz + dz * t, e = 1e-5;
                double nx = de.Evaluate(px + e, py, pz) - de.Evaluate(px - e, py, pz);
                double ny = de.Evaluate(px, py + e, pz) - de.Evaluate(px, py - e, pz);
                double nz = de.Evaluate(px, py, pz + e) - de.Evaluate(px, py, pz - e);
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz); nx /= nl; ny /= nl; nz /= nl;
                var inp = new ShadingInputs(px, py, pz, nx, ny, nz, dx, dy, dz, t, 0, 0, 0.02);   // AO probes 0.08 … 1.28 out
                int idx = y * w + x;
                ShadingPipeline.Shade(in inp, 0xFF808080u, in fx, in de, true, idx, compBuf: comp);
                ao[idx] = comp[idx].Ao; sh[idx] = comp[idx].Shadow;
            }
        }
        return (ao, sh);
    }

    [Fact]
    public void Reused_Values_Match_The_Exact_Ones_On_A_Smooth_Scene_And_Uncovered_Floor_Never_Ghosts()
    {
        var fx = Fx();
        fx.AoStrength = 2.0;   // a strong contact shadow under the sphere
        fx.Light1.Theta = 2.2; fx.Light1.Phi = 0.9;
        var cache = new TemporalLighting();
        int reused = 0, checkedPx = 0, smooth = 0, outside = 0;
        double smoothMax = 0;
        var aoRange = new List<double>();
        for (int f = 0; f < 10; f++)
        {
            double camX = -0.6 + 0.04 * f;   // strafe: the floor behind the sphere's edge appears
            var t = Scene(camX, in fx, cache);
            var exact = Scene(camX, in fx, null);
            if (f == 0) continue;
            reused += cache.LastReused;
            foreach (var (tv, ev) in new[] { (t.Ao, exact.Ao), (t.Sh, exact.Sh) })
            for (int i = 0; i < tv.Length; i++)
            {
                if (float.IsNaN(ev[i])) continue;
                checkedPx++;
                // The exact values around the pixel: a reused value comes from a point
                // under a neighbouring pixel, so it lies within their range; a value
                // carried over from another surface (a ghost) does not.
                int x = i % SceneW, y = i / SceneW; double lo = 2, hi = -1;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= SceneW || yy >= SceneH) continue;
                    float v = ev[yy * SceneW + xx];
                    if (float.IsNaN(v)) continue;
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                }
                if (tv[i] < lo - 0.02 || tv[i] > hi + 0.02) outside++;
                if (hi - lo <= 0.1) { smooth++; smoothMax = Math.Max(smoothMax, Math.Abs(tv[i] - ev[i])); }
            }
            aoRange.Add(exact.Ao.Where(v => !float.IsNaN(v)).Min());
        }
        output.WriteLine($"reused {reused}, checked {checkedPx} values ({smooth} smooth): outside the 3x3 range {outside}, smooth max error {smoothMax:F4}, darkest AO {aoRange.Min():F2}");
        Assert.True(reused > checkedPx / 8, $"precondition: only {reused} reused");
        Assert.True(aoRange.Min() < 0.6, "precondition: the AO is flat (no contact shadow)");
        Assert.Equal(0, outside);
        Assert.True(smoothMax < 0.05, $"smooth-region error {smoothMax:F4}");
    }

    /// <summary>A 16 x 12 frame whose every pixel hits a point 2 units along its own
    /// ray with normal <paramref name="normal"/>: the camera never moves, so each
    /// hit projects back onto its own pixel; only the normal can make it differ.</summary>
    private static int SameHitsFrame(TemporalLighting cache, (double X, double Y, double Z) normal, in LightingFxData fx)
    {
        const int w = 16, h = 12;
        var de = new SphereOnFloor();
        var cam = new PinholeCamera(0, 1, -3, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.577, (double)w / h, 0, 0);
        cache.BeginRender();
        using (TemporalLighting.Use(cache))
        {
            TemporalLighting.BeginFrame(w, h, cam, TemporalLighting.SceneKey(in fx, 0.02, in de), usable: true);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (2.0 * (x + 0.5) / w - 1.0) * cam.FovScale * cam.Aspect, v = (1.0 - 2.0 * (y + 0.5) / h) * cam.FovScale;
                double dl = Math.Sqrt(u * u + v * v + 1);
                double dx = u / dl, dy = v / dl, dz = 1 / dl;
                var inp = new ShadingInputs(dx * 2, 1 + dy * 2, -3 + dz * 2, normal.X, normal.Y, normal.Z, dx, dy, dz, 2.0, 0, 0, 0.02);
                ShadingPipeline.Shade(in inp, 0xFF808080u, in fx, in de, true, y * w + x);
            }
        }
        return cache.LastReused;
    }

    [Fact]
    public void A_Different_Normal_At_The_Same_Point_Is_Never_Reused()
    {
        // The two faces of a sharp ridge are a pixel apart in 3D, within the anchor
        // bound: only the normal tells their (opposite) shadows apart.
        var fx = Fx();
        var a = (0.0, 1.0, 0.0);
        var tilted = (Math.Sin(0.5), Math.Cos(0.5), 0.0);   // 29°: below the 0.9 cosine
        var close = (Math.Sin(0.3), Math.Cos(0.3), 0.0);    // 17°: the same surface
        var cache = new TemporalLighting();
        Assert.Equal(0, SameHitsFrame(cache, a, in fx));
        Assert.Equal(0, SameHitsFrame(cache, tilted, in fx));
        Assert.Equal(0, SameHitsFrame(cache, a, in fx));
        int same = SameHitsFrame(cache, close, in fx);
        Assert.True(same >= 16 * 12 / 2, $"the same surface reused only {same} of {16 * 12}");
    }

    [Fact]
    public void Relief_Preview_Reuses_And_Settles_Exactly()
    {
        const int w = 96, h = 72;
        var rnd = new Random(1337);
        var albedo = Enumerable.Range(0, w * h).Select(_ => 0xFF808080u).ToArray();
        var height = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            height[y * w + x] = (float)(0.5 + 0.4 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17));
        FractalParameters P(double az)
        {
            var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DSupersample = 1,
                Relief2DCameraAzimuthDeg = az, Lighting = Fx() };
            return p;
        }
        uint[] Run(double az, TemporalLighting? cache, bool settle = false)
        {
            var dst = new uint[w * h];
            if (cache != null) { cache.BeginRender(); cache.Settle = settle; }
            using (TemporalLighting.Use(cache)) HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, P(az), dst, out _);
            return dst;
        }
        var cache = new TemporalLighting();
        Assert.Equal(Run(10, null), Run(10, cache));
        Assert.True(cache.LastComputed > w * h / 4, $"the relief frame never reached the cache ({cache.LastComputed})");
        Run(11, cache);
        Assert.True(cache.LastReused > cache.LastComputed / 4, $"relief: reused {cache.LastReused}, computed {cache.LastComputed}");
        Assert.Equal(Run(11, null), Run(11, cache, settle: true));
        // Supersampling: one sample per pixel only, so nothing is reused.
        var ss = P(11); ss.Relief2DSupersample = 2;
        cache.BeginRender();
        using (TemporalLighting.Use(cache)) HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, ss, new uint[w * h], out _);
        Assert.Equal(0, cache.LastReused);
    }

    // ── The live host ──────────────────────────────────────────────────────────

    private sealed class RecordingRenderer : IFractalRenderer
    {
        public readonly List<uint[]> Frames = new();
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            lock (Frames) Frames.Add(colorBuffer.Take(width * height).ToArray());
        }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "record";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static List<uint[]> HostRun(bool temporal, out uint[] final)
    {
        var renderer = new RecordingRenderer();
        var fp = new FractalParameters { Lighting = Fx() };
        var state = new FractalViewState { FractalType = FractalType.Mandelbulb, FractalParameters = fp, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]) { TemporalLighting3D = temporal };
        int completed = 0;
        using var done = new SemaphoreSlim(0);
        host.FrameCompleted += (_, _) => { Interlocked.Increment(ref completed); done.Release(); };
        host.Trigger();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no first frame");
        state.Zoom = 1.02;
        host.Trigger();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no moving frame");
        if (temporal) Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no settle frame");
        Assert.False(done.Wait(TimeSpan.FromSeconds(2)), "an extra frame after the settle frame");
        final = host.SnapshotFrame(out int sw, out int sh).Take(sw * sh).ToArray();
        lock (renderer.Frames) return renderer.Frames.ToList();
    }

    [Fact]
    public void Live_Host_Settles_On_The_Full_Frame()
    {
        var off = HostRun(temporal: false, out var offFinal);
        var on = HostRun(temporal: true, out var onFinal);
        output.WriteLine($"uploads: off {off.Count}, on {on.Count}");
        Assert.Equal(off.Count + 1, on.Count);              // one settle frame more
        Assert.Equal(offFinal, onFinal);                    // the settled image is the full frame
        Assert.NotEqual(on[^2], on[^1]);                     // the moving frame was approximate
        Assert.Equal(off[0], on[0]);                         // the first frame had no history
    }

    [Fact]
    public void The_Option_Defaults_Off_And_The_Menu_Forwards_It()
    {
        var renderer = new RecordingRenderer();
        using var host = new FractalRenderHost(renderer, new FractalViewState { FractalType = FractalType.Mandelbulb }, W, H, ColorPalette.BuiltIns[0]);
        Assert.False(host.TemporalLighting3D);
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FloatingMenuViewModel();
        bool? got = null;
        vm.TemporalLighting3DToggled += (_, v) => got = v;
        vm.SetTemporalLighting3DState(true);
        Assert.True(vm.TemporalLighting3D);
        Assert.Null(got);                                   // seeding does not echo back
        vm.TemporalLighting3D = false;
        Assert.False(got);
    }
}
