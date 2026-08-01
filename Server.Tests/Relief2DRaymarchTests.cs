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

    // #184 — the shared segment in-scatter walk adds single-scatter light over an
    // explicit air segment [tStart,tEnd] and composites it over the incoming
    // background as bg·T + inScatter. With a NullDe (no occluder) and the default
    // key light on, a black backdrop is lifted toward the light; a zero-length
    // segment is a strict no-op. This is the kernel behind the sky/miss god-rays.
    [Fact]
    public void VolumetricInScatterSegment_Adds_Light_Over_Air_Segment()
    {
        var fx = LightingFxData.CreateDefault();   // Light1 on, white, no shadow
        fx.FogDensity = 0.8;
        fx.VolumeSteps = 16;
        var de = default(NullDe);

        // Zero-length segment: strict no-op (background untouched).
        double br = 0, bg = 0, bb = 0;
        ShadingPipeline.VolumetricInScatterSegment<NullDe>(
            in fx, in de, 0, 0, 0, 0, 1, 0, 1e-3, 2.0, 2.0, ref br, ref bg, ref bb);
        Assert.Equal(0.0, br); Assert.Equal(0.0, bg); Assert.Equal(0.0, bb);

        // Real segment over a black backdrop: unshadowed in-scatter lifts every
        // channel above zero.
        br = 0; bg = 0; bb = 0;
        ShadingPipeline.VolumetricInScatterSegment<NullDe>(
            in fx, in de, 0, 0, 0, 0, 1, 0, 1e-3, 0.0, 4.0, ref br, ref bg, ref bb);
        Assert.True(br > 1.0 && bg > 1.0 && bb > 1.0,
            $"segment added no light: ({br},{bg},{bb})");
    }

    // #184 — the headline fix: crepuscular shafts now form against the SKY. With
    // the ground plane off and the sky backdrop off (miss → the DropColor const),
    // ray-miss pixels that traverse the fog slab pick up shadow-carved in-scatter,
    // so they no longer equal DropColor. Before the fix, sky pixels bypassed the
    // fog entirely and stayed exactly DropColor — this asserts they now glow.
    [Fact]
    public void SkyMiss_Rays_Receive_GodRay_InScatter()
    {
        const uint Drop = 0xFF0A0A0Eu;   // HeightfieldRaymarch2D.DropColor
        int w = 320, h = 240;
        var (albedo, height) = Mandelbrot(w, h);

        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 2.5,       // tall slab → more grazing sky rays
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 28, // low camera → high silhouette
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,     // sky, not floor, behind the terrain
        };
        var fxOff = p.Lighting;
        fxOff.ShowSkyBackdrop = false;       // miss → DropColor const
        p.Lighting = fxOff;
        var noFog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, p, noFog);

        var pf = p.Clone();
        var fx = pf.Lighting;
        fx.FogDensity = 1.5;
        fx.VolumeSteps = 24;
        fx.VolumeAnisotropy = 0.7;           // forward scatter → shaft punch
        pf.Lighting = fx;
        var fog = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, pf, fog);

        int skyPixels = 0, godRaySky = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (noFog[i] == Drop)            // a genuine ray-miss (sky) pixel
            {
                skyPixels++;
                if (fog[i] != Drop) godRaySky++;
            }
        }
        Assert.True(skyPixels > w * h / 20, $"test setup has too little sky: {skyPixels}");
        Assert.True(godRaySky > 0,
            $"no sky pixel received god-ray in-scatter ({godRaySky} of {skyPixels})");
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

    // ── 4a (#165): Cook-Torrance GGX specular ─────────────────────────────

    private static int Luma(uint c)
        => (int)((c >> 16) & 0xFF) + (int)((c >> 8) & 0xFF) + (int)(c & 0xFF);

    // SpecularStrength == 0 must be byte-identical regardless of Roughness /
    // Metallic — the material knobs are fully gated, so 4a can't regress the
    // Slice-3 flat-Lambert path.
    [Fact]
    public void CpuMirror_SpecOff_ByteIdentical_Regardless_Of_Material()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.SpecularStrength = 0.0; fxA.Metallic = 1.0; fxA.Roughness = 0.1;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.SpecularStrength = 0.0; fxB.Metallic = 0.0; fxB.Roughness = 1.0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // Turning spec on adds an additive GGX highlight — some lit-slope pixels get
    // strictly brighter — while the terrain silhouette (hit fraction) is
    // untouched (spec never moves geometry).
    [Fact]
    public void CpuMirror_SpecOn_Brightens_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.SpecularStrength = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.SpecularStrength = 0.8; fxOn.Roughness = 0.4; fxOn.Metallic = 0.0;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int brighter = 0;
        for (int i = 0; i < w * h; i++) if (Luma(on[i]) > Luma(off[i])) brighter++;
        Assert.True(brighter > 50, $"GGX spec added no visible highlight ({brighter} px)");
    }

    // Metallic = 1 with spec on suppresses diffuse (diffSuppress = 1 − metallic),
    // so away from the highlight the metal render is darker than the dielectric.
    [Fact]
    public void CpuMirror_Metallic_Suppresses_Diffuse()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxDiel = LightingFxData.CreateDefault();
        fxDiel.SpecularStrength = 0.6; fxDiel.Roughness = 0.5; fxDiel.Metallic = 0.0;
        var uDiel = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxDiel);
        var diel = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uDiel, hbuf, null, albedo, diel, out _);

        var fxMetal = LightingFxData.CreateDefault();
        fxMetal.SpecularStrength = 0.6; fxMetal.Roughness = 0.5; fxMetal.Metallic = 1.0;
        var uMetal = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxMetal);
        var metal = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uMetal, hbuf, null, albedo, metal, out _);

        int darker = 0;
        for (int i = 0; i < w * h; i++) if (Luma(metal[i]) < Luma(diel[i])) darker++;
        Assert.True(darker > 100, $"metallic did not suppress diffuse ({darker} px)");
    }

    // ── 4b (#166): IQ soft shadow ─────────────────────────────────────────

    // ShadowSteps == 0 must be byte-identical regardless of ShadowSoftK /
    // ShadowLightMask — soft shadow is fully gated, no regression to 4a.
    [Fact]
    public void CpuMirror_ShadowOff_ByteIdentical_Regardless_Of_ShadowKnobs()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.ShadowSteps = 0; fxA.ShadowSoftK = 8.0; fxA.ShadowLightMask = 0x7;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.ShadowSteps = 0; fxB.ShadowSoftK = 0.0; fxB.ShadowLightMask = 0x0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // Soft shadow on casts the dome's shadow across the ground plane, darkening a
    // swath of floor pixels. The grazing default key light (phi ≈ 81°) throws a
    // long shadow; the terrain silhouette (hit fraction) is untouched.
    [Fact]
    public void CpuMirror_ShadowOn_Casts_On_Ground()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        p.Relief2DGroundPlane = true;   // give the dome a floor to cast onto
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.ShadowSteps = 0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.ShadowSteps = 32; fxOn.ShadowSoftK = 8.0; fxOn.ShadowLightMask = 0x1;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int darker = 0;
        for (int i = 0; i < w * h; i++) if (Luma(on[i]) < Luma(off[i])) darker++;
        Assert.True(darker > 200, $"soft shadow darkened too few px ({darker})");
    }

    // ── 4c (#167): DE-cone ambient occlusion ──────────────────────────────

    // AoSamples == 0 must be byte-identical regardless of AoStrength — AO is
    // fully gated, so 4c can't regress the 4a/4b path.
    [Fact]
    public void CpuMirror_AoOff_ByteIdentical_Regardless_Of_AoStrength()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.AoSamples = 0; fxA.AoStrength = 2.0;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.AoSamples = 0; fxB.AoStrength = 0.0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // AO on darkens occluded terrain (the dome's foot near the ground plane,
    // where the cone-march sees nearby surface) without moving the silhouette.
    // Spec is left untouched by AO, so no pixel gets brighter.
    [Fact]
    public void CpuMirror_AoOn_Darkens_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        p.Relief2DGroundPlane = true;   // creases at the dome foot for AO to bite
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.AoSamples = 0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.AoSamples = 5; fxOn.AoStrength = 1.0;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int darker = 0, brighter = 0;
        for (int i = 0; i < w * h; i++)
        {
            int d = Luma(on[i]) - Luma(off[i]);
            if (d < 0) darker++; else if (d > 0) brighter++;
        }
        Assert.True(darker > 200, $"AO darkened too few px ({darker})");
        Assert.Equal(0, brighter);
    }

    // ── 4d (#168): IBL-modulated ambient + triplanar procedural texture ────

    // TriplanarStrength == 0 must be byte-identical regardless of Kind/Scale/Tint
    // — triplanar is fully gated, no regression to 4a/4b/4c.
    [Fact]
    public void CpuMirror_TriplanarOff_ByteIdentical_Regardless_Of_Knobs()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.TriplanarStrength = 0.0; fxA.TriplanarKind = TriplanarTextureKind.Rock;
        fxA.TriplanarScale = 9.0; fxA.TriplanarTint = 0xFF3366CCu;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.TriplanarStrength = 0.0; fxB.TriplanarKind = TriplanarTextureKind.None;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // IblStrength == 0 must be byte-identical regardless of SkyMode — the IBL
    // ambient blend is fully gated, ambient stays the scalar AmbientStrength.
    [Fact]
    public void CpuMirror_IblOff_ByteIdentical_Regardless_Of_SkyMode()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.IblStrength = 0.0; fxA.SkyMode = SkyMode.Solid;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.IblStrength = 0.0; fxB.SkyMode = SkyMode.Gradient;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // Triplanar on modulates the terrain albedo before lighting — many surface
    // pixels change colour — while the silhouette (hit fraction) is untouched
    // (texture never moves geometry). Sky/floor pixels are unaffected.
    [Fact]
    public void CpuMirror_TriplanarOn_Retextures_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.TriplanarStrength = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.TriplanarKind = TriplanarTextureKind.Marble;
        fxOn.TriplanarStrength = 0.7; fxOn.TriplanarScale = 4.0; fxOn.TriplanarTint = 0xFFFFFFFFu;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"triplanar retextured too few px ({changed})");
    }

    // IBL ambient on blends the gradient env (sampled at the surface normal) into
    // the flat ambient, shifting shaded terrain pixels without moving the
    // silhouette.
    [Fact]
    public void CpuMirror_IblOn_Shifts_Ambient_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.IblStrength = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.IblStrength = 0.6; fxOn.SkyMode = SkyMode.Gradient;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"IBL ambient shifted too few px ({changed})");
    }

    // #184 (Slice 2) — GPU twin parity oracle: sky/miss rays that traverse the
    // fog slab now pick up shadow-carved in-scatter, so ray-miss pixels no longer
    // equal the plain backdrop. Mirrors the CPU relief render's sky god-ray path;
    // this is the oracle the D3D/Vulkan device gates diff against.
    [Fact]
    public void CpuMirror_SkyMiss_Receives_GodRay_InScatter()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();                 // ground off
        p.Relief2DCameraElevationDeg = 28;      // low camera → high silhouette
        p.Relief2DHeightScale = 2.5;            // tall slab → grazing sky rays
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.ShowSkyBackdrop = false;          // miss → DropColor const
        fxOff.FogDensity = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out _);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.ShowSkyBackdrop = false;
        fxOn.FogDensity = 1.5; fxOn.VolumeSteps = 24;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out _);

        uint drop = uOff.DropColor;
        int skyPixels = 0, godRaySky = 0;
        for (int i = 0; i < w * h; i++)
            if (off[i] == drop) { skyPixels++; if (on[i] != drop) godRaySky++; }
        Assert.True(skyPixels > w * h / 20, $"too little sky: {skyPixels}");
        Assert.True(godRaySky > 0, $"no sky god-ray in-scatter ({godRaySky}/{skyPixels})");
    }

    // #184 Slice 3 (B) — GPU twin: Henyey-Greenstein anisotropy reshapes the
    // in-scatter (forward-scatter concentration toward the key light), so g=0.8
    // differs from isotropic g=0. Silhouette unmoved. This is the knob the
    // cookbook's "single hard shaft" recipe relies on — it was ignored on GPU.
    [Fact]
    public void CpuMirror_Anisotropy_Reshapes_InScatter()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fx0 = LightingFxData.CreateDefault();
        fx0.FogDensity = 0.9; fx0.VolumeSteps = 24; fx0.VolumeAnisotropy = 0.0;
        var u0 = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx0);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u0, hbuf, null, albedo, a, out double hitA);

        var fx1 = LightingFxData.CreateDefault();
        fx1.FogDensity = 0.9; fx1.VolumeSteps = 24; fx1.VolumeAnisotropy = 0.8;
        var u1 = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fx1);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in u1, hbuf, null, albedo, b, out double hitB);

        Assert.Equal(hitA, hitB);
        int changed = 0;
        for (int i = 0; i < w * h; i++) if (a[i] != b[i]) changed++;
        Assert.True(changed > 100, $"anisotropy reshaped too few px ({changed})");
    }

    // #184 Slice 3 (C) — GPU twin: fog color tints the accumulated in-scatter. A
    // green medium biases the in-scatter green vs a white medium.
    [Fact]
    public void CpuMirror_FogColor_Tints_InScatter()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxW = LightingFxData.CreateDefault();
        fxW.FogDensity = 1.2; fxW.VolumeSteps = 24; fxW.FogColor = 0xFFFFFFFFu;
        var uW = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxW);
        var wImg = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uW, hbuf, null, albedo, wImg, out _);

        var fxG = LightingFxData.CreateDefault();
        fxG.FogDensity = 1.2; fxG.VolumeSteps = 24; fxG.FogColor = 0xFF00FF00u;
        var uG = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxG);
        var gImg = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uG, hbuf, null, albedo, gImg, out _);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (wImg[i] != gImg[i]) changed++;
        Assert.True(changed > 100, $"fog color tinted too few px ({changed})");
    }

    // 4e — FogDensity == 0 must be byte-identical regardless of VolumeSteps /
    // FogHeightFalloff. The whole fog+volumetric block is gated on FogDensity>0.
    [Fact]
    public void CpuMirror_FogOff_ByteIdentical_Regardless_Of_VolumeKnobs()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.FogDensity = 0.0; fxA.VolumeSteps = 24; fxA.FogHeightFalloff = 0.7; fxA.VolumeStepsFalloff = 0.5;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.FogDensity = 0.0; fxB.VolumeSteps = 0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // Legacy exponential fog (VolumeSteps == 0, FogDensity > 0) blends the shaded
    // terrain toward the gradient sky by 1-exp(-tHit·density). Surface pixels
    // shift; the silhouette (hit fraction) never moves — fog is a post-shade
    // colour blend, not geometry. Sky pixels stay untouched (fog runs on hits).
    [Fact]
    public void CpuMirror_LegacyFog_Blends_Toward_Sky_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.FogDensity = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.FogDensity = 1.5; fxOn.VolumeSteps = 0;   // legacy exp-fog path
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"legacy fog blended too few px ({changed})");
    }

    // Volumetric in-scatter (VolumeSteps > 0, FogDensity > 0, key light on) walks
    // the primary ray adding single-scatter light and attenuating the surface by
    // Beer-Lambert transmittance — a different result from legacy exp fog and from
    // no fog, still without moving the silhouette.
    [Fact]
    public void CpuMirror_VolumetricInScatter_Changes_Shade_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.FogDensity = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxVol = LightingFxData.CreateDefault();
        fxVol.FogDensity = 0.5; fxVol.FogHeightFalloff = 0.3; fxVol.VolumeSteps = 16;
        var uVol = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxVol);
        var vol = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uVol, hbuf, null, albedo, vol, out double hitVol);

        Assert.Equal(hitOff, hitVol);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (vol[i] != off[i]) changed++;
        Assert.True(changed > 100, $"volumetric in-scatter changed too few px ({changed})");
    }

    // 4f — the empty-space skip must cut the primary sphere-trace step count (it
    // leaps the flat air above the dome instead of crawling) while landing on the
    // SAME surface: the silhouette barely moves and the image stays within the
    // float-parity band. A conservative skip never overshoots the first hit.
    [Fact]
    public void CpuMirror_EmptySkip_CutsMarchSteps_Preserving_Image()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);
        var fx = LightingFxData.CreateDefault();

        var pOff = ReliefParams(); pOff.Relief2DEmptySkip = false;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, pOff, fx);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff, out long stepsOff);

        var pOn = ReliefParams(); pOn.Relief2DEmptySkip = true;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, pOn, fx);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn, out long stepsOn);

        // Real step-count win (magnitude is scene-dependent — the skip pays off
        // most on tall/steep terrain where the slope-limited point DE crawls; on
        // this dome the flat exterior already leaps, so a strict reduction is the
        // honest guard against a no-op regression).
        Assert.True(stepsOn < stepsOff,
            $"empty-space skip did not cut steps (off {stepsOff}, on {stepsOn})");

        // Same surface: silhouette barely moves, and per-pixel change stays tiny —
        // judged by magnitude (like the device gates), not raw changed-pixel count.
        // A conservative skip only shifts where the last pre-hit sample lands, so
        // the mean channel diff is a fraction of an LSB and only a few block-edge
        // columns move by more than a hair.
        Assert.True(Math.Abs(hitOn - hitOff) < 0.01, $"silhouette moved ({hitOff} → {hitOn})");
        long sumAbs = 0; int big = 0;
        for (int i = 0; i < w * h; i++)
        {
            uint a = off[i], b = on[i];
            int dr = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
            int dg = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
            int db = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
            sumAbs += dr + dg + db;
            if (Math.Max(dr, Math.Max(dg, db)) > 16) big++;
        }
        double meanCh = sumAbs / (3.0 * w * h);
        Assert.True(meanCh < 0.5, $"empty-space skip shifted the image too much (mean {meanCh:0.000})");
        Assert.True(big < w * h / 100, $"empty-space skip flipped too many px hard ({big})");
    }

    // The coarse max-height grid must be a conservative UPPER bound: every base
    // cell's height ≤ the max stored for the coarse cell that (with its halo)
    // covers it. That is what makes the skip safe (never overshoots a spike).
    [Fact]
    public void ReliefHeightMip_MaxGrid_Is_Conservative_Upper_Bound()
    {
        int hw = 200, hh = 150, blk = ReliefHeightMip.Blk;
        var (hbuf, _, _) = BumpField(hw, hh, hw, hh);
        var grid = ReliefHeightMip.BuildMaxGrid(hbuf, hw, hh, blk, out int mw, out int mh);

        for (int z = 0; z < hh; z++)
        for (int x = 0; x < hw; x++)
        {
            int cx = Math.Min(x / blk, mw - 1);
            int cz = Math.Min(z / blk, mh - 1);
            Assert.True(hbuf[z * hw + x] <= grid[cz * mw + cx] + 1e-6f,
                $"cell ({x},{z}) height {hbuf[z * hw + x]} exceeds block max {grid[cz * mw + cx]}");
        }
    }

    // ── 4d-ii (#171): HDRI equirect environment (ambient + sky + roughness mip) ──

    // Small procedural equirect HDRI (azimuthal R/B, polar G), registered under a
    // unique name so the parity twin routes IBL ambient + sky through the flattened
    // t4 buffer. Values in [0.05,0.95] keep the linear env well-behaved.
    private static string RegisterProcHdri(string suffix)
    {
        const int w = 48, hgt = 24;
        var data = new float[w * hgt * 3];
        for (int y = 0; y < hgt; y++)
        {
            double v = (y + 0.5) / hgt;
            for (int x = 0; x < w; x++)
            {
                double uu = (x + 0.5) / w;
                int i = (y * w + x) * 3;
                data[i]     = (float)Math.Clamp(0.5 + 0.4 * Math.Sin(2.0 * Math.PI * uu), 0.05, 0.95);
                data[i + 1] = (float)Math.Clamp(0.9 - 0.6 * v, 0.05, 0.95);
                data[i + 2] = (float)Math.Clamp(0.5 + 0.35 * Math.Cos(2.0 * Math.PI * uu), 0.05, 0.95);
            }
        }
        string name = "relief-test-hdri-" + suffix;
        HdriRegistry.Register(name, new HdriImage(w, hgt, data));
        return name;
    }

    // SkyMode == Hdri but the environment name doesn't resolve must fall back to the
    // gradient path byte-for-byte — an unresolved HDRI can never regress the #168
    // gradient/solid env.
    [Fact]
    public void CpuMirror_HdriUnresolved_FallsBack_ByteIdentical()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.IblStrength = 0.5; fxA.ShowSkyBackdrop = true;
        fxA.SkyMode = SkyMode.Hdri; fxA.EnvironmentName = "no-such-hdri-xyz";
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        Assert.Null(uA.HdriBuf);   // unresolved → no HDRI buffer → gradient fallback
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.IblStrength = 0.5; fxB.ShowSkyBackdrop = true;
        fxB.SkyMode = SkyMode.Gradient;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // HDRI ambient (IblStrength>0, SkyMode=Hdri) blends the HDRI sample at the
    // surface normal into the ambient — shifting shaded terrain — without moving
    // the silhouette. ShowSky is off (bg = DropColor, identical both) so the change
    // is isolated to the terrain ambient, proving the HDRI ambient branch.
    [Fact]
    public void CpuMirror_HdriAmbient_Shifts_Surface_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.IblStrength = 0.6; fxOff.ShowSkyBackdrop = false; fxOff.SkyMode = SkyMode.Gradient;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.IblStrength = 0.6; fxOn.ShowSkyBackdrop = false;
        fxOn.SkyMode = SkyMode.Hdri; fxOn.EnvironmentName = RegisterProcHdri("amb");
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        Assert.NotNull(uOn.HdriBuf);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);
        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"HDRI ambient shifted too few px ({changed})");
    }

    // HDRI sky (ShowSky on, SkyMode=Hdri) replaces the ray-miss gradient backdrop
    // with the equirect HDRI sample along the view ray. IblStrength=0 leaves terrain
    // shading identical, so every changed pixel is a background pixel — isolating
    // the HDRI sky branch — and the silhouette (hit fraction) never moves.
    [Fact]
    public void CpuMirror_HdriSky_Replaces_Background_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.IblStrength = 0.0; fxOff.ShowSkyBackdrop = true; fxOff.SkyMode = SkyMode.Gradient;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.IblStrength = 0.0; fxOn.ShowSkyBackdrop = true;
        fxOn.SkyMode = SkyMode.Hdri; fxOn.EnvironmentName = RegisterProcHdri("sky");
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);
        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"HDRI sky changed too few px ({changed})");
    }

    // The flattened HDRI buffer's sampler must reproduce HdriImage.Sample(dir,
    // roughness) exactly (both double, same equirect + bilinear + mip-select math)
    // — that identity is what makes the twin the oracle for the GPU port.
    [Fact]
    public void ReliefHdriBuffer_Flatten_RoundTrips_HdriImage_Sample()
    {
        const int w = 32, hgt = 16;
        var data = new float[w * hgt * 3];
        for (int y = 0; y < hgt; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 3;
            data[i]     = 0.1f + 0.8f * ((x * 7 + y * 3) % 11) / 11f;
            data[i + 1] = 0.2f + 0.6f * ((x * 5 + y * 13) % 7) / 7f;
            data[i + 2] = 0.3f + 0.5f * ((x + y * 2) % 5) / 5f;
        }
        var img = new HdriImage(w, hgt, data);
        var buf = ReliefHdriBuffer.Flatten(img);
        Assert.Equal(img.MipLevels, ReliefHdriBuffer.Levels(buf));

        (double, double, double)[] dirs =
        {
            (1, 0, 0), (0, 1, 0), (0, 0, 1), (-1, 0, 0), (0, -1, 0),
            (0.3, 0.6, -0.7), (-0.5, 0.2, 0.84), (0.71, -0.71, 0.0),
        };
        foreach (var (dx, dy, dz) in dirs)
        foreach (double rough in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var e = img.Sample(dx / l, dy / l, dz / l, rough);
            var g = ReliefHdriBuffer.Sample(buf, dx / l, dy / l, dz / l, rough);
            Assert.True(Math.Abs(e.R - g.R) < 1e-9 && Math.Abs(e.G - g.G) < 1e-9 && Math.Abs(e.B - g.B) < 1e-9,
                $"dir ({dx},{dy},{dz}) rough {rough}: HdriImage ({e.R},{e.G},{e.B}) != flattened ({g.R},{g.G},{g.B})");
        }
    }

    // ── 4e-ii (#172): shader-side reflections + FBM cloud-noise volumetrics ──

    // ReflectionStrength == 0 is the off-switch: the whole reflection probe (steps,
    // bounce count, GGX toggle) must not touch a single pixel. Proves the phase is
    // additively gated (byte-identical to pre-4e-ii) regardless of the other knobs.
    [Fact]
    public void CpuMirror_ReflectionsOff_ByteIdentical_Regardless_Of_Knobs()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.ReflectionStrength = 0.0;
        fxA.ReflectionSteps = 24; fxA.MaxBounces = 4; fxA.UseGgxSampling = true;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.ReflectionStrength = 0.0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // Mirror reflections (ReflectionStrength > 0, UseGgxSampling off) sphere-trace
    // reflect(rd,N) along the height DE and add a Fresnel-weighted env/sky tint to
    // the surface. Terrain pixels shift; the silhouette never moves (reflection is a
    // post-shade colour add on hits, not geometry).
    [Fact]
    public void CpuMirror_ReflectionsOn_Adds_Tint_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.ReflectionStrength = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.ReflectionStrength = 0.6; fxOn.ReflectionSteps = 24; fxOn.MaxBounces = 2;
        fxOn.UseGgxSampling = false; fxOn.Metallic = 0.8;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);
        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"reflections shifted too few px ({changed})");
    }

    // VolumeNoiseAmount == 0 is the off-switch for the FBM cloud modulation: with
    // the in-scatter walk running, toggling the self-shadow / scale / speed / octave
    // knobs must not change a pixel — the density multiplier stays 1 (byte-identical
    // to the #169 in-scatter render).
    [Fact]
    public void CpuMirror_VolumeNoiseOff_ByteIdentical_Regardless_Of_NoiseKnobs()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxA = LightingFxData.CreateDefault();
        fxA.FogDensity = 0.5; fxA.FogHeightFalloff = 0.3; fxA.VolumeSteps = 16;
        fxA.VolumeNoiseAmount = 0.0;
        fxA.VolumeNoiseScale = 0.7; fxA.VolumeNoiseSpeed = 1.3; fxA.VolumeNoiseOctaves = 5;
        fxA.VolumeSelfShadow = 0.9; fxA.VolumeSelfShadowSteps = 8; fxA.SceneTime = 2.0;
        var uA = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxA);
        var a = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uA, hbuf, null, albedo, a, out _);

        var fxB = LightingFxData.CreateDefault();
        fxB.FogDensity = 0.5; fxB.FogHeightFalloff = 0.3; fxB.VolumeSteps = 16;
        fxB.VolumeNoiseAmount = 0.0;
        var uB = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxB);
        var b = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uB, hbuf, null, albedo, b, out _);

        Assert.Equal(a, b);
    }

    // FBM cloud-noise (VolumeNoiseAmount > 0) modulates the per-step in-scatter
    // density (and, with self-shadow on, the per-step transmittance), producing a
    // different fog result from the smooth #169 walk — still without moving the
    // silhouette (it is a density modulation inside the post-shade fog blend).
    [Fact]
    public void CpuMirror_VolumeNoiseOn_Modulates_Fog_Without_Moving_Silhouette()
    {
        int w = 320, h = 240, hw = 320, hh = 240;
        var p = ReliefParams();
        var (hbuf, albedo, maxH) = BumpField(hw, hh, w, h);

        var fxOff = LightingFxData.CreateDefault();
        fxOff.FogDensity = 0.5; fxOff.FogHeightFalloff = 0.3; fxOff.VolumeSteps = 16;
        fxOff.VolumeNoiseAmount = 0.0;
        var uOff = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOff);
        var off = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOff, hbuf, null, albedo, off, out double hitOff);

        var fxOn = LightingFxData.CreateDefault();
        fxOn.FogDensity = 0.5; fxOn.FogHeightFalloff = 0.3; fxOn.VolumeSteps = 16;
        fxOn.VolumeNoiseAmount = 0.8; fxOn.VolumeNoiseScale = 0.5; fxOn.VolumeNoiseOctaves = 3;
        fxOn.VolumeSelfShadow = 0.6; fxOn.VolumeSelfShadowSteps = 4;
        var uOn = BuildUniforms(w, h, hw, hh, hbuf, maxH, p, fxOn);
        var on = new uint[w * h];
        ReliefRaymarchGpu.RenderCpuMirror(in uOn, hbuf, null, albedo, on, out double hitOn);

        Assert.Equal(hitOff, hitOn);
        int changed = 0;
        for (int i = 0; i < w * h; i++) if (on[i] != off[i]) changed++;
        Assert.True(changed > 100, $"cloud-noise modulated too few px ({changed})");
    }
}

// #162 (Slice 3d) — host-wiring seam. HeightfieldRaymarch2D.Render dispatches
// the injected IReliefRaymarchKernel instead of the CPU sphere trace when (and
// only when) the opt-in flag FractalParameters.Relief2DGpuRaymarch is set and a
// kernel is supplied. Uses a stub kernel so no GPU is required; the real GPU
// kernels are proven correct by the #160 (D3D/WARP) and #161 (Vulkan/lavapipe)
// device gates.
public class ReliefGpuSeamTests
{
    private sealed class StubReliefKernel : IReliefRaymarchKernel
    {
        public const uint Sentinel = 0xFF123456u;
        public int Calls;
        public int SeenW, SeenH, SeenHw, SeenHh;

        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst)
        {
            Calls++;
            SeenW = u.W; SeenH = u.H; SeenHw = u.Hw; SeenHh = u.Hh;
            for (int i = 0; i < dst.Length; i++) dst[i] = Sentinel;
        }

        public void Dispose() { }
    }

    private static (uint[] albedo, float[] height) Field(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    private static FractalParameters ReliefParams(bool gpu) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DGpuRaymarch = gpu,
    };

    private static bool AllSentinel(uint[] dst)
    {
        for (int i = 0; i < dst.Length; i++) if (dst[i] != StubReliefKernel.Sentinel) return false;
        return true;
    }

    [Fact]
    public void GpuSeam_FlagOff_RunsCpu_KernelUntouched()
    {
        int w = 320, h = 240;
        var (albedo, height) = Field(w, h);
        var stub = new StubReliefKernel();
        var dst = new uint[w * h];

        // Flag OFF but a kernel is supplied: the CPU trace must run, not the stub.
        HeightfieldRaymarch2D.Render(albedo, height, w, h, ReliefParams(gpu: false), dst, stub);

        Assert.Equal(0, stub.Calls);
        Assert.False(AllSentinel(dst));
    }

    [Fact]
    public void GpuSeam_FlagOn_DispatchesKernel_WithMatchingUniforms()
    {
        int w = 320, h = 240;
        var (albedo, height) = Field(w, h);
        var stub = new StubReliefKernel();
        var dst = new uint[w * h];

        HeightfieldRaymarch2D.Render(albedo, height, w, h, ReliefParams(gpu: true), dst, stub);

        Assert.Equal(1, stub.Calls);
        // The 6-arg overload maps the field grid to the output grid (hw=w, hh=h).
        Assert.Equal(w, stub.SeenW);
        Assert.Equal(h, stub.SeenH);
        Assert.Equal(w, stub.SeenHw);
        Assert.Equal(h, stub.SeenHh);
        Assert.True(AllSentinel(dst), "kernel output was not written to dst");
    }

    [Fact]
    public void GpuSeam_FlagOn_NullKernel_FallsBackToCpu()
    {
        int w = 320, h = 240;
        var (albedo, height) = Field(w, h);
        var dst = new uint[w * h];

        // Opt-in flag on but no kernel attached: the CPU raymarch must run — never
        // a no-op or a throw (the GPU path is opt-in AND kernel-gated).
        HeightfieldRaymarch2D.Render(albedo, height, w, h, ReliefParams(gpu: true), dst,
            (IReliefRaymarchKernel?)null);

        Assert.False(AllSentinel(dst));
        // A real 3D silhouette (some lit surface, not a flat copy of the albedo).
        bool differsFromAlbedo = false;
        for (int i = 0; i < dst.Length; i++) if (dst[i] != albedo[i]) { differsFromAlbedo = true; break; }
        Assert.True(differsFromAlbedo);
    }
}
