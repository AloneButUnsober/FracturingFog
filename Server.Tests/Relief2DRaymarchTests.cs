using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// #102 Phase 2 — oblique heightfield raymarch. These lock in that the render
// produces a real 3D view (surface hits + ray-miss sky = a silhouette) and
// that the shared volumetric fog stack (LightingFxData) reaches the 2D fractal.
public class Relief2DRaymarchTests
{
    private static (uint[] albedo, float[] height) Mandelbrot(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    [Fact]
    public void Raymarch_Produces_Surface_And_Sky_Silhouette()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            // Isolate the terrain-vs-sky silhouette: the #132 ground plane would
            // otherwise fill the ray-miss region with floor. Frame-fill (#128)
            // legitimately raises terrain coverage, so the upper bound is generous.
            Relief2DGroundPlane = false,
        };
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, dst, out double hitFrac);

        // A genuine 3D view has BOTH a lit surface and ray-miss sky.
        Assert.InRange(hitFrac, 0.10, 0.97);
    }

    [Fact]
    public void Volumetric_Fog_Alters_The_Image()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
        };
        var noFog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, noFog);

        var pf = p.Clone();
        var fx = pf.Lighting;
        fx.FogDensity = 0.9;
        fx.VolumeSteps = 24;
        pf.Lighting = fx;
        var fog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, pf, fog);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (fog[i] != noFog[i]) changed++;
        Assert.True(changed > w * h / 20,
            $"volumetric fog changed too few pixels: {changed} of {w * h}");
    }

    [Fact]
    public void Isolate_Culls_Background_And_Writes_Transparent()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var baseP = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 50,
            Relief2DGroundPlane = false,
        };
        var full = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, baseP, full, out double fullFrac);

        var iso = baseP.Clone();
        iso.Relief2DIsolate = true;
        iso.Relief2DIsolateByDetail = true;
        iso.Relief2DDetailThreshold = 0.6;
        var cut = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, iso, cut, out double isoFrac);

        int transparent = 0;
        for (int i = 0; i < w * h; i++) if (((cut[i] >> 24) & 0xFF) == 0) transparent++;

        // Isolation removes background (fewer surface hits), keeps some object,
        // and drops the background to transparent alpha.
        Assert.True(isoFrac < fullFrac, $"isolate did not cull: {isoFrac} vs {fullFrac}");
        Assert.True(isoFrac > 0.02, $"isolate culled everything: {isoFrac}");
        Assert.True(transparent > w * h / 10, $"too few transparent px: {transparent}");
    }

    // #143 — the decoupled-resolution overload with field dims equal to the
    // output dims must be byte-identical to the coupled overload (no behaviour
    // change for the common case).
    [Fact]
    public void Decoupled_Overload_Equal_Dims_Is_Identical()
    {
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
        };
        var coupled = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, coupled, out double fA);
        var decoupled = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, p, decoupled, out double fB);

        Assert.Equal(fA, fB);
        Assert.Equal(coupled, decoupled);
    }

    // #143 — a hi-res field (larger than the output) drives the raymarch through
    // the same view and still produces a valid 3D silhouette at the small output.
    [Fact]
    public void HiRes_Field_Renders_Valid_Silhouette_At_Small_Output()
    {
        int ow = 200, oh = 150;          // shrunk-window output
        int hw = 800, hh = 600;          // floor-res field, same view
        var (albedoLo, _)  = Mandelbrot(ow, oh);
        var (_, heightHi)  = Mandelbrot(hw, hh);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
            Relief2DGroundPlane = false,   // isolate terrain-vs-sky silhouette
        };
        var dst = new uint[ow * oh];
        HeightfieldRaymarch2D.Render(albedoLo, heightHi, ow, oh, hw, hh, p, dst, out double hitFrac);

        Assert.InRange(hitFrac, 0.10, 0.97);
    }

    // Lighting-FX debug HUD must draw on a Relief-3D raymarch frame (it used to
    // only run inside the 3D raymarcher calculators; the oblique-relief path
    // renders through HeightfieldRaymarch2D, so the host applies the HUD to the
    // relief buffer). Compass flag (0x1) draws in the top-right corner.
    [Fact]
    public void DebugHud_Draws_On_Relief_Raymarch_Buffer()
    {
        int w = 256, h = 192;   // both ≥ 128 so the HUD is not size-skipped
        var (albedo, height) = Mandelbrot(w, h);
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
        };
        var relief = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, relief);

        // Flags == 0 is a strict no-op (no HUD requested).
        var noHud = (uint[])relief.Clone();
        var fxOff = p.Lighting;
        fxOff.DebugHudFlags = 0;
        ScreenSpacePost.ApplyDebugHud(noHud, w, h, in fxOff);
        Assert.Equal(relief, noHud);

        // Compass on → the top-right 80×80 box gets a 50% black backdrop + ticks.
        var withHud = (uint[])relief.Clone();
        var fxOn = p.Lighting;
        fxOn.DebugHudFlags = 0x1;
        ScreenSpacePost.ApplyDebugHud(withHud, w, h, in fxOn);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (withHud[i] != relief[i]) changed++;
        Assert.True(changed > 0, "compass HUD drew nothing on the relief buffer");

        // The change is localised to the top-right compass region, not global.
        int cornerChanged = 0;
        for (int y = 0; y < 96; y++)
            for (int x = w - 96; x < w; x++)
                if (withHud[y * w + x] != relief[y * w + x]) cornerChanged++;
        Assert.True(cornerChanged > 0, "compass HUD missed the top-right corner");
        Assert.Equal(changed, cornerChanged);
    }

    // #155 — pre-pass cache. Re-rendering the SAME field+params must be
    // deterministic (a cache hit reproduces the recompute exactly); a changed
    // height SCALE reuses the cached field yet re-derives the correct, stable
    // image; and a changed FIELD must invalidate the cache (no stale bleed).
    [Fact]
    public void PrepassCache_Is_Deterministic_And_Invalidates()
    {
        int w = 320, h = 240;
        var (albedoA, heightA) = Mandelbrot(w, h);
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
        };

        var a1 = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedoA, heightA, w, h, p, a1);
        var a2 = new uint[w * h];   // cache HIT — identical inputs, identical out
        HeightfieldRaymarch2D.Render(albedoA, heightA, w, h, p, a2);
        Assert.Equal(a1, a2);

        // Same field, different height SCALE: cached pre-pass reused, sy/invLip
        // per-call, so the image legitimately differs but stays stable.
        var pScale = p.Clone();
        pScale.Relief2DHeightScale = 3.0;
        var s1 = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedoA, heightA, w, h, pScale, s1);
        var s2 = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedoA, heightA, w, h, pScale, s2);
        Assert.Equal(s1, s2);
        Assert.NotEqual(a1, s1);

        // Different FIELD must invalidate the cache.
        var calcB = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.5, CenterY = 0.6, Zoom = 8.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calcB.Calculate(default);
        var albedoB = (uint[])calcB.ColorBuffer.Clone();
        var heightB = (float[])calcB.SmoothBuffer.Clone();
        var b1 = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedoB, heightB, w, h, p, b1);
        Assert.NotEqual(a1, b1);

        // Re-render field A — must match the ORIGINAL A render (correct re-key).
        var a3 = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedoA, heightA, w, h, p, a3);
        Assert.Equal(a1, a3);
    }

    // #155 — the preview parameter builder drops the heavy per-hit FX (AO, SSAO,
    // reflections, volumetric) and supersample while keeping the cheap dominant
    // depth cues (soft shadow + specular, from auto-shade) so the preview frames
    // like the final.
    [Fact]
    public void PreviewParams_Drop_Heavy_Fx_Keep_Shadow()
    {
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DAutoShade = true,
            Relief2DSupersample = 4,
        };
        var pv = HeightfieldRaymarch2D.MakePreviewParams(p);

        Assert.Equal(1, pv.Relief2DSupersample);
        Assert.False(pv.Relief2DAutoShade);
        var fx = pv.Lighting;
        Assert.Equal(0, fx.AoSamples);
        Assert.Equal(0, fx.SsaoSamples);
        Assert.Equal(0.0, fx.ReflectionStrength);
        Assert.Equal(0, fx.VolumeSteps);
        Assert.True(fx.ShadowSteps > 0, "preview dropped the soft-shadow cue");
        Assert.True(fx.SpecularStrength > 0, "preview dropped the specular cue");
    }

    [Fact]
    public void Dead_Flat_Field_Is_Passthrough()
    {
        int w = 64, h = 64;
        var albedo = new uint[w * h];
        var height = new float[w * h];   // all zero = all interior
        for (int i = 0; i < w * h; i++) albedo[i] = 0xFF334455u;
        var dst = new uint[w * h];
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, dst, out double hitFrac);
        Assert.Equal(albedo, dst);
        Assert.Equal(0.0, hitFrac);
    }
}

// #159 (Relief 3D Slice 3a) — the GPU relief-raymarch foundation: the extracted
// shared camera must reproduce the render's original inline math bit-for-bit,
// and the CPU parity twin (the oracle the device gate diffs GPU output against)
// must render a valid, deterministic 3D silhouette.
public class ReliefRaymarchGpuTests
{
    // Smooth radial cosine bump — a well-sampled height field (no needles), so
    // the sphere trace converges cleanly and the twin is a clean oracle.
    private static (float[] hbuf, uint[] albedo, float maxH) BumpField(int hw, int hh, int aw, int ah)
    {
        var hbuf = new float[hw * hh];
        float maxH = 0f;
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            double u = (x + 0.5) / hw - 0.5, v = (y + 0.5) / hh - 0.5;
            double r = Math.Sqrt(u * u + v * v) / 0.5;
            float hv = r >= 1.0 ? 0f : (float)(0.5 * (1.0 + Math.Cos(Math.PI * r)));
            hbuf[y * hw + x] = hv;
            if (hv > maxH) maxH = hv;
        }
        var albedo = new uint[aw * ah];
        for (int i = 0; i < aw * ah; i++) albedo[i] = 0xFFB06030u;   // warm terrain
        return (hbuf, albedo, maxH);
    }

    private static ReliefUniforms BuildUniforms(int w, int h, int hw, int hh,
        float[] hbuf, float maxH, FractalParameters p, LightingFxData fx)
    {
        double aspect = (double)w / h;
        double sy = 0.35 * Math.Max(0.0, p.Relief2DHeightScale) / maxH;
        // Grid-slope maxima → world Lipschitz bound (mirrors Render's per-call math).
        float gx = 0f, gz = 0f;
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            if (x > 0) gx = Math.Max(gx, Math.Abs(hbuf[y * hw + x] - hbuf[y * hw + x - 1]));
            if (y > 0) gz = Math.Max(gz, Math.Abs(hbuf[y * hw + x] - hbuf[(y - 1) * hw + x]));
        }
        double worldDx = aspect / hw, worldDz = 1.0 / hh;
        double maxSlope = Math.Max(gx * sy / worldDx, gz * sy / worldDz);
        double invLip = 1.0 / Math.Sqrt(1.0 + maxSlope * maxSlope);
        return ReliefUniforms.Build(w, h, hw, hh, sy, aspect, invLip, maxH, p, in fx);
    }

    private static FractalParameters ReliefParams() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,   // isolate terrain-vs-sky silhouette
    };

    // The extracted BuildObliqueCamera must equal the ORIGINAL inline block that
    // lived in Render (pasted verbatim here as the authoritative reference), so
    // the extraction is provably byte-identical.
    [Fact]
    public void BuildObliqueCamera_Matches_Original_Inline_Math()
    {
        int w = 480, h = 360;
        double aspect = (double)w / h, sy = 0.21, maxH = 1.0;
        var p = ReliefParams();
        p.Relief2DCameraZoom = 1.3;

        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, sy, maxH, p);

        // ── verbatim original block ──
        double az = p.Relief2DCameraAzimuthDeg * Math.PI / 180.0;
        double el = Math.Clamp(p.Relief2DCameraElevationDeg, 5.0, 89.0) * Math.PI / 180.0;
        double fov = Math.Clamp(p.Relief2DCameraFovDeg, 15.0, 100.0) * Math.PI / 180.0;
        double framingAspect = Math.Min(aspect, 2.2);
        double extent = 0.5 * Math.Sqrt(framingAspect * framingAspect + 1.0);
        double zoom = Math.Clamp(p.Relief2DCameraZoom, 0.2, 5.0);
        double foreshorten = Math.Clamp(Math.Sin(el), 0.3, 1.0);
        double radius = extent * foreshorten / (Math.Tan(fov * 0.5) * zoom);
        double tgtY = 0.35 * sy * maxH;
        double camX = radius * Math.Cos(el) * Math.Sin(az);
        double camY = radius * Math.Sin(el);
        double camZ = radius * Math.Cos(el) * Math.Cos(az);
        double fX = -camX, fY = (tgtY - camY), fZ = -camZ;
        double fl = Math.Sqrt(fX * fX + fY * fY + fZ * fZ); fX /= fl; fY /= fl; fZ /= fl;
        double rX = -fZ, rY = 0.0, rZ = fX;
        double rl = Math.Sqrt(rX * rX + rZ * rZ); if (rl < 1e-9) rl = 1; rX /= rl; rZ /= rl;
        double uX = rY * fZ - rZ * fY;
        double uY = rZ * fX - rX * fZ;
        double uZ = rX * fY - rY * fX;
        double tanHalf = Math.Tan(fov * 0.5);
        double orthoHalfV = extent * foreshorten / zoom;
        double bx = aspect * 0.5, bz = 0.5, by = sy * maxH * 1.05 + 1e-3;
        double eps0 = p.Relief2DCameraOrthographic
            ? Math.Max(0.0009 * radius, orthoHalfV / h)
            : 0.0009 * radius;
        double pixelAngle = p.Relief2DCameraOrthographic ? 0.0 : tanHalf / h;
        // ── end verbatim ──

        Assert.Equal(camX, cam.CamX); Assert.Equal(camY, cam.CamY); Assert.Equal(camZ, cam.CamZ);
        Assert.Equal(fX, cam.FX); Assert.Equal(fY, cam.FY); Assert.Equal(fZ, cam.FZ);
        Assert.Equal(rX, cam.RX); Assert.Equal(rZ, cam.RZ);
        Assert.Equal(uX, cam.UX); Assert.Equal(uY, cam.UY); Assert.Equal(uZ, cam.UZ);
        Assert.Equal(tanHalf, cam.TanHalf);
        Assert.Equal(orthoHalfV, cam.OrthoHalfV);
        Assert.Equal(bx, cam.Bx); Assert.Equal(by, cam.By); Assert.Equal(bz, cam.Bz);
        Assert.Equal(eps0, cam.Eps0); Assert.Equal(pixelAngle, cam.PixelAngle);
        Assert.Equal(320, cam.MaxSteps);
        Assert.Equal(bx * 3.0, cam.FloorBx); Assert.Equal(bz * 3.0, cam.FloorBz);
    }

    // ReliefUniforms.Build must route the camera through the shared builder, so
    // the twin, the GPU kernel and the CPU render all frame identically.
    [Fact]
    public void ReliefUniforms_Build_Uses_Shared_Camera()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, _, maxH) = BumpField(hw, hh, w, h);
        double aspect = (double)w / h;
        double sy = 0.35 * p.Relief2DHeightScale / maxH;
        var fx = LightingFxData.CreateDefault();

        var u = ReliefUniforms.Build(w, h, hw, hh, sy, aspect, 0.5, maxH, p, in fx);
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, sy, maxH, p);
        Assert.Equal(cam.CamX, u.Cam.CamX);
        Assert.Equal(cam.FY, u.Cam.FY);
        Assert.Equal(cam.Eps0, u.Cam.Eps0);
        Assert.Equal(0.5, u.InvLip);
    }

    // The CPU twin (the parity oracle) renders a real 3D view — surface hits AND
    // ray-miss sky — and is fully deterministic (a re-run is bit-identical).
    [Fact]
    public void CpuMirror_Deterministic_And_Valid_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var fx = LightingFxData.CreateDefault();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);
        var u = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx);

        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, null, albedo, a, out double hitA);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, null, albedo, b, out double hitB);

        Assert.Equal(a, b);                 // deterministic
        Assert.Equal(hitA, hitB);
        Assert.InRange(hitA, 0.10, 0.90);   // a genuine silhouette: surface + sky
    }

    // Isolate mode writes a transparent background (alpha 0) on ray-miss so the
    // relief exports as a cutout — mirrors the CPU render's #135 behaviour.
    [Fact]
    public void CpuMirror_Isolate_Writes_Transparent_Background()
    {
        int w = 256, h = 192, hw = 256, hh = 192;
        var p = ReliefParams();
        p.Relief2DIsolate = true;
        var fx = LightingFxData.CreateDefault();
        fx.ShowSkyBackdrop = false;
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);
        var u = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx);

        var dst = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, null, albedo, dst, out double hit);

        int transparent = 0;
        for (int i = 0; i < w * h; i++) if (((dst[i] >> 24) & 0xFF) == 0) transparent++;
        Assert.True(transparent > w * h / 10, $"too few transparent px: {transparent}");
        Assert.True(hit > 0.05, "isolate culled the whole surface");
    }
}
