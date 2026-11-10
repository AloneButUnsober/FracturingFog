// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1603 — the vortex-well presets (Domain colouring #1592 + Relief 3D). The promise is
// "dark pits at the vortex cores": the vortices are found independently, as points
// round which the far-field amplitude's phase winds (SlitCascadeCalculator.Amplitude,
// not the render), and at each one the rendered height field must be a pit (lower than
// a ring round it) and the colour dark. The Talbot relief must keep the intensity
// height (Domain changes only the colour).
public sealed class VortexWellPresetTests
{
    private const string Wells = "Slit Cascade - Vortex Wells";
    private const string Constellation = "Slit Cascade - Vortex Constellation";
    private const string TalbotRelief = "Talbot Carpet - Relief";
    private const int W = 200, H = 130;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        region.ApplyRelief3DTo(p);
        return (region, p);
    }

    private static T Render<T>(T calc, FractalRegion region) where T : IFractalCalculator
    {
        calc.CenterX = region.CenterX; calc.CenterY = region.CenterY; calc.Zoom = region.Zoom;
        calc.MaxIterations = region.Iterations; calc.ColorMap = new RampMap();
        calc.Calculate(TestContext.Current.CancellationToken);
        return calc;
    }

    // Phase winding (turns) of the amplitude round the cell from pixel (x, y) to (x + 1, y + 1),
    // shifted by 0.37 px so a zero sitting exactly on a pixel (the centre core) falls inside
    // one cell, walked on a fine loop so no step exceeds half a turn.
    private static double Winding(in SlitCascadeCalculator.Cascade c, FractalRegion r, int x, int y)
    {
        double pitch = 4.0 / W / r.Zoom;
        double x0 = r.CenterX + (x + 0.37 - W * 0.5) * pitch, y0 = r.CenterY + (y + 0.37 - H * 0.5) * pitch;
        const int per = 16;
        var pts = new List<(double, double)>();
        for (int k = 0; k < per; k++) pts.Add((x0 + pitch * k / per, y0));
        for (int k = 0; k < per; k++) pts.Add((x0 + pitch, y0 + pitch * k / per));
        for (int k = 0; k < per; k++) pts.Add((x0 + pitch * (1 - (double)k / per), y0 + pitch));
        for (int k = 0; k < per; k++) pts.Add((x0, y0 + pitch * (1 - (double)k / per)));
        double total = 0, prev = SlitCascadeCalculator.Amplitude(c, pts[0].Item1, pts[0].Item2).Phase;
        for (int k = 1; k <= pts.Count; k++)
        {
            var (ux, uy) = pts[k % pts.Count];
            double ph = SlitCascadeCalculator.Amplitude(c, ux, uy).Phase;
            double d = (ph - prev) / (2 * Math.PI);
            total += d - Math.Round(d); prev = ph;
        }
        return total;
    }

    private static List<(int X, int Y, int Charge)> Vortices(FractalRegion region, FractalParameters p, int margin)
    {
        var c = SlitCascadeCalculator.Build(p);
        var found = new List<(int, int, int)>();
        for (int y = margin; y < H - margin - 1; y++)
            for (int x = margin; x < W - margin - 1; x++)
            {
                int q = (int)Math.Round(Winding(c, region, x, y));
                if (q != 0) found.Add((x, y, q));
            }
        return found;
    }

    private static double Luma(uint argb)
        => (0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF)) / 255.0;

    // A pit: walking out from the vortex cell, some ring (radius ≤ 25 px) is everywhere at
    // least as high as the cell and clearly higher on average — a charge-q core has
    // I ∝ r^(2q), so a high charge sits below the dynamic-range floor (height 0) for a few
    // pixels before the walls rise. And the cell's pixel is dark.
    private static void AssertPit(SlitCascadeCalculator calc, int x, int y, string what)
    {
        var s = calc.SmoothBuffer;
        float pit = new[] { s[y * W + x], s[y * W + x + 1], s[(y + 1) * W + x], s[(y + 1) * W + x + 1] }.Min();
        string best = "";
        bool walls = false;
        for (int r = 3; r <= 25 && !walls; r++)
        {
            var ring = new List<float>();
            for (int k = 0; k < 8 * r; k++)
            {
                double a = 2 * Math.PI * k / (8 * r);
                int rx = (int)Math.Round(x + 0.5 + r * Math.Cos(a)), ry = (int)Math.Round(y + 0.5 + r * Math.Sin(a));
                if (rx >= 0 && rx < W && ry >= 0 && ry < H) ring.Add(s[ry * W + rx]);
            }
            walls = ring.Min() >= pit && ring.Average() > pit + 0.05 * calc.MaxIterations;
            best = $"r={r}: min {ring.Min()}, mean {ring.Average()}";
        }
        Assert.True(walls, $"{what}: no wall round pit {pit} ({best})");
        int arg = new[] { y * W + x, y * W + x + 1, (y + 1) * W + x, (y + 1) * W + x + 1 }.MinBy(i => s[i]);
        Assert.True(Luma(calc.ColorBuffer[arg]) < 0.12, $"{what}: colour {calc.ColorBuffer[arg]:X8} not dark");
    }

    [Theory]
    [InlineData(Wells)]
    [InlineData(Constellation)]
    [InlineData(TalbotRelief)]
    public void Uses_Domain_Colouring_With_Raymarched_Relief(string name)
    {
        var (region, p) = Preset(name);
        Assert.True(region.Relief3D is { Enabled: true, Raymarch: true });
        Assert.True(p.Relief2DRaymarch);
        if (region.FractalType == FractalType.SlitCascade) Assert.Equal(SlitCascadeView.Domain, p.SlitCascadeView);
        else Assert.Equal(TalbotView.Domain, p.TalbotView);
        // Each preset owns its relief settings (a shared object would leak edits between presets).
        var others = FractalRegionLibrary.Instance.BuiltIns.Where(r => r.Name != name && r.Relief3D != null);
        Assert.DoesNotContain(others, r => ReferenceEquals(r.Relief3D, region.Relief3D));
    }

    [Fact]
    public void Wells_Has_One_Charge_Three_Pit_At_The_Centre()
    {
        var (region, p) = Preset(Wells);
        var calc = Render(new SlitCascadeCalculator(W, H) { FractalParameters = p }, region);
        var v = Vortices(region, p, 8);
        var core = Assert.Single(v, q => Math.Abs(q.Charge) >= 2);
        Assert.Equal(3, core.Charge);
        Assert.InRange(core.X, W / 2 - 2, W / 2 + 1);
        Assert.InRange(core.Y, H / 2 - 2, H / 2 + 1);
        AssertPit(calc, core.X, core.Y, "charge-3 core");
    }

    [Fact]
    public void Constellation_Splits_The_Core_Into_Single_Vortex_Pits()
    {
        var (region, p) = Preset(Constellation);
        var calc = Render(new SlitCascadeCalculator(W, H) { FractalParameters = p }, region);
        var v = Vortices(region, p, 8);
        Assert.All(v, q => Assert.Equal(1, Math.Abs(q.Charge)));   // no higher-charge core survives
        Assert.True(v.Count >= 3, $"only {v.Count} vortices");
        foreach (var q in v) AssertPit(calc, q.X, q.Y, $"vortex at ({q.X},{q.Y})");
    }

    [Fact]
    public void Talbot_Relief_Keeps_The_Intensity_Height()
    {
        var (region, p) = Preset(TalbotRelief);
        var domain = Render(new TalbotCarpetCalculator(W, H) { FractalParameters = p }, region);
        var q = p.Clone(); q.TalbotView = TalbotView.Intensity;
        var inten = Render(new TalbotCarpetCalculator(W, H) { FractalParameters = q }, region);
        Assert.Equal(inten.SmoothBuffer, domain.SmoothBuffer);      // same terrain
        Assert.NotEqual(inten.ColorBuffer, domain.ColorBuffer);     // coloured by phase
        // The self-image at ζ = 1 is a ridge line: the bright bars there stand above the
        // mean height of the carpet.
        double pitch = 4.0 / W / region.Zoom;
        int row = (int)Math.Round((1.0 - region.CenterY) / pitch + H * 0.5);
        Assert.InRange(row, 0, H - 1);
        float rowMax = Enumerable.Range(0, W).Max(x => domain.SmoothBuffer[row * W + x]);
        Assert.True(rowMax > domain.SmoothBuffer.Average(v => v) * 1.2, $"ridge {rowMax} vs mean {domain.SmoothBuffer.Average(v => v)}");
    }
}
