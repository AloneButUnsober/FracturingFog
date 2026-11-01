// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1634 — the prime themes in the Color Theme Editor: the OrbitTrap kind's
// GaussianPrime shape and the PrimeBands kind. Parity anchors against the
// built-ins (#1486 / #1487 / #1608): the editor trap samples the IDENTICAL trap
// field, its hits interior partitions the set exactly as Gaussian Prime Hits does,
// and PrimeBands lights exactly the built-in's prime pixels in its colours. Plus the
// widened prime grid's exactness, the tunables, and the data / adapter / editor
// round trips.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PrimeThemesEditorTests
{
    private const int W = 160, H = 120;

    private static bool IsPrime(long n)
    {
        if (n < 2) return false;
        for (long d = 2; d * d <= n; d++) if (n % d == 0) return false;
        return true;
    }

    private static bool GaussianPrime(long a, long b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        return a != 0 && b != 0 ? IsPrime(a * a + b * b) : a + b != 0 && IsPrime(a + b) && (a + b) % 4 == 3;
    }

    private static readonly (int a, int b)[] Primes =
        (from a in Enumerable.Range(-40, 81) from b in Enumerable.Range(-40, 81) where GaussianPrime(a, b) select (a, b)).ToArray();

    private static double Brute(double wr, double wi)
        => Primes.Min(p => Math.Sqrt((wr - p.a) * (wr - p.a) + (wi - p.b) * (wi - p.b)));

    private static List<ColorStopData> Stops() => new()
    {
        new ColorStopData { Position = 0f, R = 255, G = 250, B = 230 },
        new ColorStopData { Position = 1f, R = 10, G = 14, B = 30 },
    };

    private static ColorThemeData TrapData(float scale = 6f, int block = 10, bool interior = true, bool hits = false) => new()
    {
        Name = "GP edit", Kind = ColorThemeKind.OrbitTrap, Stops = Stops(),
        TrapShape = OrbitTrapShape.GaussianPrime, ColorInterior = interior,
        GaussianLatticeScale = scale, GaussianTailBlock = block, GaussianInteriorHits = hits,
    };

    private static MandelbrotCalculator Render(IColorMap map, double cx = -0.6, double cy = 0, double zoom = 1, int maxIter = 300)
    {
        var c = new MandelbrotCalculator(W, H) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, ColorMap = map };
        c.Calculate(CancellationToken.None);
        return c;
    }

    // The grid now covers lattice scales to 16 (|w| ≤ 32.5): the ±4 search stays
    // exact only if every point there has a Gaussian prime within 3.5 (the worst gap
    // in the wider box is about 2.57, over the 2.5 a ±3 search would need).
    [Fact]
    public void WidenedGrid_IsExactToScale16()
    {
        double worst = 0;
        for (double wr = -32.5; wr <= 32.5; wr += 0.25)
            for (double wi = -32.5; wi <= 32.5; wi += 0.25)
            {
                double b = Brute(wr, wi);
                worst = Math.Max(worst, b);
                Assert.Equal(b, GaussianPrimeTrapMap.NearestDistance(wr, wi), 12);
            }
        Assert.True(worst <= 3.5, $"worst gap {worst:F3}");
        for (int a = -36; a <= 36; a++)
            for (int b = -36; b <= 36; b++)
                Assert.Equal(GaussianPrime(a, b), GaussianPrimeTrapMap.IsGaussianPrime(a, b));
    }

    [Theory]
    [InlineData(-0.6, 0.0, 1.0, 300)]
    [InlineData(-0.7453, 0.1127, 267.0, 800)]
    public void EditorTrap_SamplesTheBuiltInsField(double cx, double cy, double zoom, int it)
    {
        var editor = Render(DataDrivenColorThemes.Create(TrapData())!, cx, cy, zoom, it);
        var builtIn = Render(new GaussianPrimeTrapMap(), cx, cy, zoom, it);
        Assert.Equal(builtIn.IterationBuffer, editor.IterationBuffer);
        Assert.Equal(builtIn.TrapBuffer, editor.TrapBuffer);
        Assert.True(editor.TrapBuffer.Distinct().Count() > 100);
    }

    // Tunables reach the sampler: scale 10 over the whole orbit (block 0) matches an
    // independent orbit + brute-force oracle on every escaped pixel.
    [Fact]
    public void EditorTrap_HonoursScaleAndWholeOrbit()
    {
        var c = Render(DataDrivenColorThemes.Create(TrapData(scale: 10f, block: 0, interior: false))!);
        double scale = (3.5 / Math.Max(W, H)) / 1.0;
        int n = 0;
        for (int y = 0; y < H; y += 3)
            for (int x = 0; x < W; x += 3)
            {
                int i = y * W + x, esc = c.IterationBuffer[i];
                if (esc >= 300) continue;
                double cr = -0.6 + (x - W * 0.5) * scale, ci = (y - H * 0.5) * scale, zr = 0, zi = 0, best = double.MaxValue;
                for (int k = 1; k < esc; k++)
                {
                    double t = zr * zr - zi * zi + cr; zi = 2 * zr * zi + ci; zr = t;
                    if (Math.Abs(zr) <= 2 && Math.Abs(zi) <= 2) best = Math.Min(best, Brute(zr * 10, zi * 10));
                }
                float want = best == double.MaxValue ? 0f : (float)best;
                Assert.True(Math.Abs(c.TrapBuffer[i] - want) < 1e-5, $"({x},{y}): {c.TrapBuffer[i]} vs {want}");
                n++;
            }
        Assert.True(n > 300);
    }

    // The hits interior partitions the set exactly as the built-in Gaussian Prime Hits:
    // equal built-in colours ⇔ equal editor colours (a bijection over the interior).
    [Fact]
    public void EditorHitsInterior_PartitionsLikeTheBuiltIn()
    {
        var editor = Render(DataDrivenColorThemes.Create(TrapData(hits: true))!);
        var builtIn = Render(new GaussianPrimeHitsMap());
        var fwd = new Dictionary<uint, uint>(); var back = new Dictionary<uint, uint>();
        int inside = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (builtIn.IterationBuffer[i] < 300) continue;
            uint b = builtIn.ColorBuffer[i], e = editor.ColorBuffer[i];
            Assert.Equal(e, fwd.TryGetValue(b, out var fe) ? fe : fwd[b] = e);
            Assert.Equal(b, back.TryGetValue(e, out var be) ? be : back[e] = b);
            inside++;
        }
        Assert.True(inside > 1000 && fwd.Count > 3, $"interior {inside}, classes {fwd.Count}");
    }

    [Fact]
    public void PrimeBands_LightsTheBuiltInsPrimePixels()
    {
        var data = new ColorThemeData { Name = "PB", Kind = ColorThemeKind.PrimeBands, Stops = Stops() };
        var editor = Render(DataDrivenColorThemes.Create(data)!, maxIter: 400);
        var builtIn = Render(new PrimeEscapeGoldCyanMap(), maxIter: 400);
        var primeColours = new HashSet<uint> { PrimeEscapeGoldCyanMap.ColorFor(5, 5), PrimeEscapeGoldCyanMap.ColorFor(3, 3), PrimeEscapeGoldCyanMap.ColorFor(2, 2) };
        int lit = 0;
        for (int i = 0; i < W * H; i++)
        {
            int n = editor.IterationBuffer[i];
            if (n >= 400) { Assert.Equal(builtIn.ColorBuffer[i], editor.ColorBuffer[i]); continue; }   // in set: black
            if (IsPrime(n)) { Assert.Equal(builtIn.ColorBuffer[i], editor.ColorBuffer[i]); lit++; }
            else Assert.DoesNotContain(editor.ColorBuffer[i], primeColours);
        }
        Assert.True(lit > W * H / 50);
    }

    [Theory]
    [InlineData(PrimeBandModulus.Mod6)]
    [InlineData(PrimeBandModulus.Single)]
    public void PrimeBands_ClassesFollowTheModulus(PrimeBandModulus mod)
    {
        var data = new ColorThemeData
        {
            Name = "PB", Kind = ColorThemeKind.PrimeBands, Stops = Stops(), PrimeModulus = mod,
            PrimeColorA = new InSetColorData(200, 0, 0), PrimeColorB = new InSetColorData(0, 200, 0),
            PrimeColorSpecial = new InSetColorData(0, 0, 200), PrimeCompositeDim = 0f,
        };
        var c = Render(DataDrivenColorThemes.Create(data)!, maxIter: 400);
        for (int i = 0; i < W * H; i++)
        {
            int n = c.IterationBuffer[i];
            if (n >= 400) continue;
            uint want = !IsPrime(n) ? 0xFF000000u
                : mod == PrimeBandModulus.Single ? (n == 2 ? 0xFF0000C8u : 0xFFC80000u)
                : n is 2 or 3 ? 0xFF0000C8u : n % 6 == 1 ? 0xFFC80000u : 0xFF00C800u;
            Assert.Equal(want, c.ColorBuffer[i]);
        }
    }

    // Batch resolves --theme through the palette registry after LoadUserThemes
    // (BatchEntry): a saved editor theme of each new kind must come back by name as
    // the right runtime type with its settings. Runs under the isolated test data root.
    [Fact]
    public void SavedEditorThemes_ResolveByName_AsBatchDoes()
    {
        string trapName = $"GP-{Guid.NewGuid():N}", bandName = $"PB-{Guid.NewGuid():N}";
        var lib = UserColorThemeLibrary.Instance;
        lib.Load();
        var trap = TrapData(scale: 8f, block: 12, hits: true); trap.Name = trapName;
        lib.ReplaceOrAdd(trap);
        lib.ReplaceOrAdd(new ColorThemeData { Name = bandName, Kind = ColorThemeKind.PrimeBands, Stops = Stops(), PrimeModulus = PrimeBandModulus.Mod6 });
        lib.Save();
        try
        {
            ColorPalette.LoadUserThemes();
            var t = Assert.IsType<DataDrivenOrbitTrap>(ColorPalette.GetPaletteByName(trapName));
            Assert.Equal(OrbitTrapShape.GaussianPrime, t.Shape);
            Assert.Equal(8f, t.ExportGaussianLatticeScale);
            Assert.Equal(12, t.ExportGaussianTailBlock);
            Assert.True(t.ExportGaussianInteriorHits);
            var b = Assert.IsType<DataDrivenPrimeBands>(ColorPalette.GetPaletteByName(bandName));
            Assert.Equal(PrimeBandModulus.Mod6, b.ExportModulus);
        }
        finally
        {
            lib.Remove(trapName); lib.Remove(bandName); lib.Save();
            ColorPalette.RebuildUserPalettes();
        }
    }

    [Fact]
    public void Settings_RoundTrip_ThroughExport_Adapter_AndEditor()
    {
        var trap = TrapData(scale: 9.5f, block: 7, hits: true);
        var pb = new ColorThemeData
        {
            Name = "PB", Kind = ColorThemeKind.PrimeBands, Stops = Stops(), PrimeModulus = PrimeBandModulus.Mod6,
            PrimeColorA = new InSetColorData(1, 2, 3), PrimeColorSpecial = new InSetColorData(4, 5, 6), PrimeCompositeDim = 0.4f,
        };
        foreach (var d in new[] { trap, pb })
        {
            var exp = DataDrivenColorThemes.Export(DataDrivenColorThemes.Create(d)!)!;
            var adapter = typeof(FracturingFog.Hosting.AvaloniaShellBootstrap).Assembly.GetType("FracturingFog.Hosting.ColorThemeDefAdapter")!;
            var def = (ColorThemeDef)adapter.GetMethod("ToDef", new[] { typeof(ColorThemeData) })!.Invoke(null, new object[] { exp })!;

            var vm = new ColorThemeEditorViewModel(AnimationLifecycleTests.FakeService.Create(), null, null);
            typeof(ColorThemeEditorViewModel).GetMethod("LoadDef", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, new object[] { def });
            var built = (ColorThemeDef)typeof(ColorThemeEditorViewModel).GetMethod("BuildDef", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
            var back = (ColorThemeData)adapter.GetMethod("ToData", new[] { typeof(ColorThemeDef) })!.Invoke(null, new object[] { built })!;

            Assert.Equal(d.Kind, back.Kind);
            Assert.Equal(d.TrapShape, back.TrapShape);
            Assert.Equal(d.GaussianLatticeScale, back.GaussianLatticeScale);
            Assert.Equal(d.GaussianTailBlock, back.GaussianTailBlock);
            Assert.Equal(d.GaussianInteriorHits, back.GaussianInteriorHits);
            Assert.Equal(d.PrimeModulus, back.PrimeModulus);
            Assert.Equal(d.PrimeCompositeDim, back.PrimeCompositeDim, 5);
            Assert.Equal(d.PrimeColorA?.ToPackedArgb(), back.PrimeColorA?.ToPackedArgb());
            Assert.Equal(d.PrimeColorB?.ToPackedArgb(), back.PrimeColorB?.ToPackedArgb());   // null stays null (default colour)
            Assert.Equal(d.PrimeColorSpecial?.ToPackedArgb(), back.PrimeColorSpecial?.ToPackedArgb());
            if (d.Kind == ColorThemeKind.OrbitTrap) Assert.True(vm.ShowGaussianPrime);
            else Assert.True(vm.ShowPrimeBands);
        }
        // Legacy JSON (no new keys) keeps the built-in defaults.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ColorThemeData>("{\"Name\":\"x\"}")!;
        Assert.Equal(6f, legacy.GaussianLatticeScale);
        Assert.Equal(10, legacy.GaussianTailBlock);
        Assert.Equal(PrimeBandModulus.Mod4, legacy.PrimeModulus);
    }
}
