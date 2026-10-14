// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #437 — the slideshow's solid-frame skip. Two engine-free pieces are tested
// here (SlideshowEngine itself lives in UI.Avalonia, which this project does not
// reference):
//   • SolidFrame.IsEffectivelySolid — dominant colour ≥ 99.5 % reads as solid,
//     so a near-blank frame with a few stray pixels is caught too;
//   • HostColorThemeService.RescueRegionIterations — a Mandelbrot region saved
//     below its escape band (every theme renders one flat colour) gets a
//     session-only iteration count at which pixels escape, used by the
//     slideshow's thumbnail probe, offscreen fade source and ApplyRegion.

using System;
using System.Linq;
using FracturingFog.Hosting;
using FracturingFog.Models;
using FracturingFog.Slideshow;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SolidFrameTests
{
    [Fact]
    public void Single_Colour_Is_Solid()
        => Assert.True(SolidFrame.IsEffectivelySolid(Enumerable.Repeat(0xFF000000u, 2304).ToArray()));

    [Fact]
    public void A_Few_Stray_Pixels_Still_Read_As_Solid()
    {
        var buf = Enumerable.Repeat(0xFF000000u, 64 * 36).ToArray();
        for (int i = 0; i < 11; i++) buf[i * 97] = 0xFFFF0000u;   // 11 of 2304 = 0.48 %
        Assert.True(SolidFrame.IsEffectivelySolid(buf));
    }

    [Fact]
    public void Dominant_Colour_Need_Not_Be_The_First_Pixel()
    {
        var buf = Enumerable.Repeat(0xFF102030u, 1000).ToArray();
        buf[0] = 0xFFFFFFFFu;
        Assert.True(SolidFrame.IsEffectivelySolid(buf));
    }

    [Fact]
    public void A_Real_Image_Is_Not_Solid()
    {
        var buf = new uint[64 * 36];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF000000u | (uint)(i * 37 % 255);
        Assert.False(SolidFrame.IsEffectivelySolid(buf));
    }

    [Fact]
    public void One_Percent_Other_Colour_Is_Not_Solid()
    {
        var buf = Enumerable.Repeat(0xFF000000u, 1000).ToArray();
        for (int i = 0; i < 10; i++) buf[i] = 0xFFFFFFFFu;   // 99.0 % < 99.5 %
        Assert.False(SolidFrame.IsEffectivelySolid(buf));
    }

    [Fact]
    public void Empty_Or_Null_Is_Not_Solid()
    {
        Assert.False(SolidFrame.IsEffectivelySolid(Array.Empty<uint>()));
        Assert.False(SolidFrame.IsEffectivelySolid(null!));
    }
}

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class S437RegionIterationRescueTests
{
    // "Deep Lightning in Space" (zoom 7.58e26, --regionprobe): solid at 300 iterations.
    private static FractalRegion DeepLightning(string name, int iterations) => new()
    {
        Name = name,
        FractalType = FractalType.Mandelbrot,
        CenterX = -1.4181949444785762, CenterXLo = -7.4415882477902279E-17,
        CenterY = -0.12700786443815276, CenterYLo = -2.3429499355532375E-18,
        Zoom = 7.58348E+26,
        Iterations = iterations,
        QualityPreset = QualityPreset.Standard,
    };

    private static T WithRegion<T>(FractalRegion region, Func<T> body)
    {
        FractalRegionLibrary.Instance.UserRegions.Add(region);
        try { return body(); }
        finally { FractalRegionLibrary.Instance.UserRegions.Remove(region); }
    }

    private static string AnyTheme(HostColorThemeService svc)
        => svc.EnumerateThemeNames().First(n => !n.StartsWith("—"));

    [Fact]
    public void Solid_Region_Gets_A_Session_Count_That_Every_Path_Uses()
    {
        var region = DeepLightning("#437 rescue probe", 300);
        WithRegion(region, () =>
        {
            var svc = new HostColorThemeService(renderHost: null);
            string theme = AnyTheme(svc);

            // The reported symptom: one flat colour at the saved count.
            var before = svc.RenderRegionThumbnail(region.Name, theme, null, 64, 36)!;
            Assert.True(SolidFrame.IsEffectivelySolid(before));

            int raised = svc.RescueRegionIterations(region.Name);
            Assert.True(raised > 300, $"raised = {raised}");
            Assert.Equal(300, region.Iterations);   // the saved region is untouched

            // Thumbnail probe and offscreen fade source now show structure.
            var after = svc.RenderRegionThumbnail(region.Name, theme, null, 64, 36)!;
            Assert.False(SolidFrame.IsEffectivelySolid(after));
            var fade = svc.RenderRegionOffscreen(region.Name, theme, 64, 36)!;
            Assert.False(SolidFrame.IsEffectivelySolid(fade));

            // The live commit renders at the raised count.
            var state = new FractalViewState();
            Assert.True(svc.ApplyRegion(region.Name, state));
            Assert.Equal(raised, state.PreferredIterations);

            // Once per region per session.
            Assert.Equal(0, svc.RescueRegionIterations(region.Name));
            return 0;
        });
    }

    [Fact]
    public void Region_With_Escapes_Is_Left_Alone()
    {
        var region = DeepLightning("#437 healthy probe", 20000);
        WithRegion(region, () =>
        {
            var svc = new HostColorThemeService(renderHost: null);
            Assert.Equal(0, svc.RescueRegionIterations(region.Name));
            var state = new FractalViewState();
            svc.ApplyRegion(region.Name, state);
            Assert.Equal(20000, state.PreferredIterations);
            return 0;
        });
    }

    [Fact]
    public void Non_Mandelbrot_And_Unknown_Regions_Are_Not_Rescued()
    {
        var svc = new HostColorThemeService(renderHost: null);
        var julia = FractalRegionLibrary.Instance.All.First(r => r.FractalType == FractalType.Julia);
        Assert.Equal(0, svc.RescueRegionIterations(julia.Name));
        Assert.Equal(0, svc.RescueRegionIterations("no such region #437"));
    }
}
