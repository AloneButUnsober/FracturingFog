// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Text.Json;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// #1441 — Wada mirror spheres photo mode. Oracles from optics, not from the renderer:
//   • Schlick's term is F0 head-on and 1 at grazing, and never below F0, so the light a
//     Fresnel path keeps is at least F0^bounces, and exactly F0^bounces with Fresnel off;
//   • lossless mirrors deliver a board's colour unchanged;
//   • with every opening open to a uniform sky, every ray that escapes shows that sky;
//   • the centre ray at the default camera returns through the viewing opening, which is dark.
public class MirrorSpheresPhotoTests
{
    private static FractalParameters Photo(Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters { MirrorSpheresMode = MirrorSpheresMode.Photo };
        tweak?.Invoke(p);
        return p;
    }

    private static MirrorSpheresCalculator Render(FractalParameters p, int n = 96)
    {
        var c = new MirrorSpheresCalculator(n, n) { FractalParameters = p };
        c.Calculate(default);
        return c;
    }

    [Fact]
    public void Schlick_Is_F0_HeadOn_One_At_Grazing_And_Never_Below_F0()
    {
        Assert.Equal(0.85, MirrorSpheresScene.Schlick(0.85, 1.0), 12);
        Assert.Equal(1.0, MirrorSpheresScene.Schlick(0.85, 0.0), 12);
        double prev = 0.85;
        for (int i = 99; i >= 0; i--)
        {
            double r = MirrorSpheresScene.Schlick(0.85, i / 100.0);
            Assert.True(r >= prev - 1e-15, "Schlick must rise toward grazing");
            prev = r;
        }
    }

    [Fact]
    public void Throughput_Is_F0_Power_Without_Fresnel_And_At_Least_That_With_It()
    {
        var plain = MirrorSpheresScene.Build(Photo(p => p.MirrorSpheresFresnel = false));
        var fres = MirrorSpheresScene.Build(Photo());
        var rng = new Random(1441);
        int multi = 0;
        for (int i = 0; i < 5000; i++)
        {
            double ox = rng.NextDouble() * 2 - 1, oy = rng.NextDouble() * 2 - 1, oz = rng.NextDouble() * 2 - 1;
            double len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            ox *= 6 / len; oy *= 6 / len; oz *= 6 / len;
            double dx = -ox + rng.NextDouble() - 0.5, dy = -oy + rng.NextDouble() - 0.5, dz = -oz + rng.NextDouble() - 0.5;
            double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= dl; dy /= dl; dz /= dl;
            var a = plain.Trace(ox, oy, oz, dx, dy, dz);
            var b = fres.Trace(ox, oy, oz, dx, dy, dz);
            Assert.Equal(Math.Pow(0.85, a.Bounces), a.Throughput, 12);
            if (b.Bounces != a.Bounces) continue;   // same path, so same bounce count unless the cap differs
            Assert.True(b.Throughput >= Math.Pow(0.85, b.Bounces) - 1e-12);
            if (a.Bounces >= 2) multi++;
        }
        Assert.True(multi > 100, $"precondition: only {multi} multi-bounce rays");
    }

    [Fact]
    public void Default_Centre_Ray_Looks_Back_Out_Through_The_Dark_Viewing_Opening()
    {
        var c = Render(Photo(), 101);
        Assert.Equal(0xFF000000u, c.ColorBuffer[50 * 101 + 50]);
        // With the viewer-black rule off, the viewing opening shows its own entry
        // (opening 3, white here) scaled by the one Fresnel bounce.
        var off = Render(Photo(p => { p.MirrorSpheresViewerBlack = false; p.MirrorSpheresOpeningColors = "#D55E00,#0072B2,#F2F2F2,#FFFFFF"; p.MirrorSpheresReflectance = 1.0; }), 101);
        Assert.Equal(0xFFFFFFFFu, off.ColorBuffer[50 * 101 + 50]);
    }

    [Fact]
    public void Lossless_Mirrors_Deliver_Board_Colours_Unchanged()
    {
        var c = Render(Photo(p => { p.MirrorSpheresReflectance = 1.0; p.MirrorSpheresViewerBlack = false; }));
        var paint = MirrorSpheresPalette.Parse(new FractalParameters().MirrorSpheresOpeningColors);
        var seen = new System.Collections.Generic.HashSet<uint>();
        foreach (uint col in c.ColorBuffer)
            for (int k = 0; k < 4; k++)
                if (col == paint[k].Color) seen.Add(col);
        Assert.Equal(4, seen.Count);   // all four boards appear, at full strength
    }

    [Fact]
    public void Open_Openings_Under_A_Uniform_Sky_Show_Only_That_Sky()
    {
        const uint sky = 0xFF336699u;
        var p = Photo(q =>
        {
            q.MirrorSpheresReflectance = 1.0;
            q.MirrorSpheresViewerBlack = false;
            q.MirrorSpheresOpeningColors = "env,env,env,env";
            var fx = q.Lighting;
            fx.SkyMode = SkyMode.Gradient;
            fx.BgTopColor = sky;
            fx.BgBottomColor = sky;
            fx.FogBackground = false;
            q.Lighting = fx;
        });
        var c = Render(p);
        int skyPixels = 0;
        // The gradient helper interpolates top/bottom in float, so a channel may land
        // one step low; anything further off is a real colour change.
        static bool NearSky(uint col) =>
            Math.Abs((int)((col >> 16) & 0xFF) - 0x33) <= 1 && Math.Abs((int)((col >> 8) & 0xFF) - 0x66) <= 1
            && Math.Abs((int)(col & 0xFF) - 0x99) <= 1;
        foreach (uint col in c.ColorBuffer)
        {
            Assert.True(NearSky(col) || col == 0xFF000000u, $"pixel {col:X8} is neither the sky nor trapped-black");
            if (NearSky(col)) skyPixels++;
        }
        Assert.True(skyPixels > c.ColorBuffer.Length / 2);
    }

    [Fact]
    public void Palette_Parses_Boards_Env_And_Falls_Back_Per_Entry()
    {
        var d = MirrorSpheresPalette.Parse(null);
        Assert.Equal(MirrorSpheresPalette.DefaultText, new FractalParameters().MirrorSpheresOpeningColors);
        var p = MirrorSpheresPalette.Parse(" #112233 , env, nonsense ");
        Assert.Equal(0xFF112233u, p[0].Color);
        Assert.False(p[0].Environment);
        Assert.True(p[1].Environment);
        Assert.Equal(d[2], p[2]);   // malformed → default
        Assert.Equal(d[3], p[3]);   // missing → default
    }

    [Fact]
    public void Basins_Mode_Is_Untouched_By_The_Photo_Settings()
    {
        var a = Render(new FractalParameters());
        var b = Render(new FractalParameters { MirrorSpheresOpeningColors = "env,env,env,env", MirrorSpheresFresnel = false, MirrorSpheresViewerBlack = false });
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void Photo_Settings_Round_Trip_Through_The_Region_Snapshot()
    {
        var src = Photo(p => { p.MirrorSpheresOpeningColors = "env,#00FF00,#0000FF,#FFFFFF"; p.MirrorSpheresViewerBlack = false; p.MirrorSpheresFresnel = false; });
        var json = JsonSerializer.Serialize(RegionFractalParams.Snapshot(FractalType.MirrorSpheres, src));
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(json)!.ApplyTo(dst);
        Assert.Equal(MirrorSpheresMode.Photo, dst.MirrorSpheresMode);
        Assert.Equal("env,#00FF00,#0000FF,#FFFFFF", dst.MirrorSpheresOpeningColors);
        Assert.False(dst.MirrorSpheresViewerBlack);
        Assert.False(dst.MirrorSpheresFresnel);
    }
}
