// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;

using FracturingFog.Hosting;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #989 — the slideshow's solid-theme peek must render non-Mandelbrot regions
/// WITHOUT the live render host (the old path applied the region to the live
/// view state and borrowed the live alt calculator). With no host attached at
/// all, a Julia thumbnail still renders — so it can't depend on live state —
/// while source-compiled types, which need the live compiler, decline.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class RegionThumbnailIsolationTests
{
    private static string AnyTheme(HostColorThemeService svc)
        => svc.EnumerateThemeNames().First(n => !n.StartsWith("—"));

    [Fact]
    public void Julia_thumbnail_renders_without_a_live_host()
    {
        var svc = new HostColorThemeService(renderHost: null);
        var julia = FractalRegionLibrary.Instance.All.First(r => r.FractalType == FractalType.Julia);

        var buf = svc.RenderRegionThumbnail(julia.Name, AnyTheme(svc), null, 64, 36);

        Assert.NotNull(buf);
        Assert.Equal(64 * 36, buf!.Length);
    }

    [Fact]
    public void Source_compiled_region_declines_instead_of_touching_the_live_compiler()
    {
        var svc = new HostColorThemeService(renderHost: null);
        var region = FractalRegionLibrary.Instance.All
            .FirstOrDefault(r => r.FractalType is FractalType.UserEquation or FractalType.Sandbox or FractalType.UserBulb);
        if (region == null) return;   // library ships none — nothing to check

        Assert.Null(svc.RenderRegionThumbnail(region.Name, AnyTheme(svc), null, 64, 36));
    }

    [Fact]
    public void Unknown_region_returns_null()
    {
        var svc = new HostColorThemeService(renderHost: null);
        Assert.Null(svc.RenderRegionThumbnail("no such region #989", AnyTheme(svc), null, 64, 36));
    }
}
