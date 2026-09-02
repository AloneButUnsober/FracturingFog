// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #589 (F13) — data-driven Orbit Trap theme kind. The Color Theme Editor / JSON
// can now author orbit-trap themes (previously only ~30 hardcoded C# classes).
// The runtime delegates the per-iteration distance to a built-in shape sampler
// and maps acc.TrapMin through the theme's own gradient.

using System;
using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DataDrivenOrbitTrapTests
{
    private const int W = 48, H = 36;

    private static ColorThemeData TrapData(OrbitTrapShape shape) => new()
    {
        Name = $"Test Trap {shape}",
        Category = "Test",
        Kind = ColorThemeKind.OrbitTrap,
        TrapShape = shape,
        TrapScale = 2f,
        TrapPower = 0.35f,
        Stops =
        {
            new ColorStopData { Position = 0.0f, R = 0,   G = 0,   B = 0   },
            new ColorStopData { Position = 0.5f, R = 255, G = 128, B = 0   },
            new ColorStopData { Position = 1.0f, R = 255, G = 255, B = 255 },
        },
    };

    [Fact]
    public void Create_OrbitTrap_IsOrbitAware()
    {
        IColorMap? map = DataDrivenColorThemes.Create(TrapData(OrbitTrapShape.Hexagon));
        Assert.NotNull(map);
        Assert.IsAssignableFrom<IOrbitAwareColorMap>(map);
    }

    [Fact]
    public void EveryShape_Resolves()
    {
        foreach (OrbitTrapShape shape in Enum.GetValues<OrbitTrapShape>())
        {
            IColorMap? map = DataDrivenColorThemes.Create(TrapData(shape));
            Assert.NotNull(map);
            Assert.IsAssignableFrom<IOrbitAwareColorMap>(map);
        }
    }

    // Rendered through the DSL path (which has an orbit-sampling loop), a
    // data-driven trap paints escaped pixels with real orbit lace — non-uniform
    // and opaque.
    [Fact]
    public void Render_OrbitTrap_IsNonUniformAndOpaque()
    {
        IColorMap map = DataDrivenColorThemes.Create(TrapData(OrbitTrapShape.Ring))!;
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            ColorMap = map,
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "z*z + c",
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);

        var distinct = new HashSet<uint>(calc.ColorBuffer);
        Assert.True(distinct.Count >= 3, $"trap should be non-uniform, saw {distinct.Count}");

        // At least one escaped (non-inset) pixel is opaque trap colour.
        bool anyOpaqueNonBlack = false;
        foreach (uint px in calc.ColorBuffer)
            if ((px & 0xFF000000u) == 0xFF000000u && (px & 0x00FFFFFFu) != 0u)
            { anyOpaqueNonBlack = true; break; }
        Assert.True(anyOpaqueNonBlack);
    }

    // Two different shapes on the same map/scene must differ (the shape actually
    // drives the distance measurement).
    [Fact]
    public void DifferentShapes_ProduceDifferentImages()
    {
        static uint[] Render(OrbitTrapShape shape)
        {
            var calc = new UserEquationCalculator(W, H)
            {
                CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
                ColorMap = DataDrivenColorThemes.Create(TrapData(shape))!,
                FractalParameters = new FractalParameters
                {
                    UserEquationSource = "z*z + c",
                    UserCodeOrigin = UserCodeOrigin.Interactive,
                },
            };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }

        Assert.NotEqual(Render(OrbitTrapShape.Hexagon), Render(OrbitTrapShape.Hyperbola));
    }

    // ── F14 (#590) — theme-driven interior orbit colouring ──────────────────

    private const int CenterIdx = (H / 2) * W + (W / 2);   // c = (-0.5, 0), in-set

    [Fact]
    public void ColorInterior_Capability_ReflectsData()
    {
        var off = (IOrbitAwareColorMap)DataDrivenColorThemes.Create(TrapData(OrbitTrapShape.Ring))!;
        Assert.False(off.WantsInteriorColor);   // default

        var data = TrapData(OrbitTrapShape.Ring);
        data.ColorInterior = true;
        var on = (IOrbitAwareColorMap)DataDrivenColorThemes.Create(data)!;
        Assert.True(on.WantsInteriorColor);
    }

    [Fact]
    public void DslPath_ColorInterior_FillsInteriorFromTheme()
    {
        static uint[] Render(bool colorInterior)
        {
            var data = TrapData(OrbitTrapShape.Ring);
            data.ColorInterior = colorInterior;
            var calc = new UserEquationCalculator(W, H)
            {
                CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
                ColorMap = DataDrivenColorThemes.Create(data)!,
                FractalParameters = new FractalParameters
                {
                    UserEquationSource = "z*z + c",
                    UserCodeOrigin = UserCodeOrigin.Interactive,
                    // NB: the User-Equation flag stays OFF — the theme itself
                    // requests interior colouring (F14).
                },
            };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }

        uint[] off = Render(false);
        uint[] on = Render(true);

        Assert.Equal(0xFF000000u, off[CenterIdx]);          // flat interior when off
        Assert.NotEqual(0xFF000000u, on[CenterIdx]);        // theme coloured the interior
        Assert.Equal(0xFFu, (on[CenterIdx] >> 24) & 0xFFu); // opaque
        Assert.NotEqual(off, on);
    }

    // The native Mandelbrot path must dispatch a data-driven orbit trap through
    // the orbit-aware path (interface fallback) — otherwise Sample never runs
    // and it renders as a plain iteration gradient.
    [Fact]
    public void NativePath_OrbitTrap_Samples_AndIsNonUniform()
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            ColorMap = DataDrivenColorThemes.Create(TrapData(OrbitTrapShape.Ring))!,
        };
        calc.Calculate(default);

        var distinct = new HashSet<uint>(calc.ColorBuffer);
        Assert.True(distinct.Count >= 3, $"native orbit trap should sample + vary, saw {distinct.Count}");
    }

    [Fact]
    public void RoundTrip_ColorInterior()
    {
        var data = TrapData(OrbitTrapShape.Hexagon);
        data.ColorInterior = true;
        ColorThemeData? back = DataDrivenColorThemes.Export(DataDrivenColorThemes.Create(data)!);
        Assert.NotNull(back);
        Assert.True(back!.ColorInterior);
    }

    [Fact]
    public void RoundTrip_Export_PreservesShapeAndKind()
    {
        var data = TrapData(OrbitTrapShape.Hexagon);
        data.TrapScale = 1.75f;
        data.TrapPower = 0.5f;
        IColorMap map = DataDrivenColorThemes.Create(data)!;

        ColorThemeData? back = DataDrivenColorThemes.Export(map);
        Assert.NotNull(back);
        Assert.Equal(ColorThemeKind.OrbitTrap, back!.Kind);
        Assert.Equal(OrbitTrapShape.Hexagon, back.TrapShape);
        Assert.Equal(1.75f, back.TrapScale);
        Assert.Equal(0.5f, back.TrapPower);
        Assert.Equal(3, back.Stops.Count);

        // Re-create from the exported data → still orbit-aware, same shape.
        var map2 = DataDrivenColorThemes.Create(back) as DataDrivenOrbitTrap;
        Assert.NotNull(map2);
        Assert.Equal(OrbitTrapShape.Hexagon, map2!.Shape);
    }
}
