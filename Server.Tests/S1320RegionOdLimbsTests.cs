// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1320 — saved regions kept only the QD limbs (CenterX..CenterX3). Past zoom
// ~1e50 the OD limbs carry the position, so a recalled deep region landed up to
// a view-width off (the user's 2.1e65 "Minibrot Bug Region"), far more deeper.
// Regions now store all eight limbs per axis (the OD four only when non-zero).
// Checks, under the test data-root redirect:
//   • save → regions.json → reload → recall keeps all sixteen limbs bit-exactly;
//   • the recalled view renders the real minibrot there — its in-set mask
//     matches the main set through the atom-size map (independent of the store),
//     while the same view with the OD tail dropped (the old behaviour) does not;
//   • a shallow region serialises exactly as before, and an old 4-limb file loads.

using System;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Hosting;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class S1320RegionOdLimbsTests
{
    // The period-1808 minibrot near "Will this work" (#1319), |size| 1.83e-69.
    private static readonly NucleusResult Mini = new(
        NucleusStatus.Found,
        new DeepComplex(
            new OD(-1.9918151296901943, -7.82198368112659E-17, -5.6237669196087764E-33, 2.6876672348091084E-49,
                   8.344815422720153E-68, 5.62021278812849E-84, 3.758605147229201E-100, 3.74170214755604E-117),
            new OD(-5.524041575397243E-06, -2.9567164202839686E-22, -2.2585741366702907E-38, 6.439806134954378E-55,
                   1.8890258519870084E-71, 1.8268317099722925E-87, -9.199248922388955E-104, 5.069913980636591E-120)),
        1808, new Complex(1.2484338945256195E-69, 1.3350422957373403E-69), 0);

    private static double[] Limbs(FractalViewState s) => new[]
    {
        s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3, s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7,
        s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3, s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7,
    };

    private static FractalViewState DeepView()
    {
        var (c, zoom) = NucleusFinder.Frame(Mini);
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, Zoom = zoom };
        s.CenterX = c.Re.X0; s.CenterXLo = c.Re.X1; s.CenterX2 = c.Re.X2; s.CenterX3 = c.Re.X3;
        s.CenterX4 = c.Re.X4; s.CenterX5 = c.Re.X5; s.CenterX6 = c.Re.X6; s.CenterX7 = c.Re.X7;
        s.CenterY = c.Im.X0; s.CenterYLo = c.Im.X1; s.CenterY2 = c.Im.X2; s.CenterY3 = c.Im.X3;
        s.CenterY4 = c.Im.X4; s.CenterY5 = c.Im.X5; s.CenterY6 = c.Im.X6; s.CenterY7 = c.Im.X7;
        return s;
    }

    /// <summary>Fraction of pixels whose in-set state disagrees with the main
    /// set mapped through c = nucleus + size·c′, plus the in-set count.</summary>
    private static (double Mismatch, int InSet) MaskVsMainSet(FractalViewState s)
    {
        const int W = 96, H = 64;
        int it = NucleusFinder.SuggestedIterations(Mini.Period);
        var calc = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = s.CenterX, CenterXLo = s.CenterXLo, CenterX2 = s.CenterX2, CenterX3 = s.CenterX3,
            CenterX4 = s.CenterX4, CenterX5 = s.CenterX5, CenterX6 = s.CenterX6, CenterX7 = s.CenterX7,
            CenterY = s.CenterY, CenterYLo = s.CenterYLo, CenterY2 = s.CenterY2, CenterY3 = s.CenterY3,
            CenterY4 = s.CenterY4, CenterY5 = s.CenterY5, CenterY6 = s.CenterY6, CenterY7 = s.CenterY7,
            Zoom = s.Zoom, MaxIterations = it, ColorMap = new HsvPalette(),
        };
        calc.Calculate(CancellationToken.None);
        // The reference mask is centred on the framed view, so compare against
        // the frame centre (the true one), not the state's.
        double scale = (3.5 / Math.Max(W, H)) / s.Zoom;
        int mismatch = 0, inSet = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                bool a = calc.IterationBuffer[py * W + px] >= it;
                var w = new Complex((px - W * 0.5) * scale, (py - H * 0.5) * scale);
                if (a) inSet++;
                if (a != InMainSet(w / Mini.Size + NucleusFinder.HomeCenterX)) mismatch++;
            }
        return ((double)mismatch / (W * H), inSet);
    }

    private static bool InMainSet(Complex c)
    {
        double x = 0, y = 0;
        for (int i = 0; i < 2000; i++)
        {
            double x2 = x * x, y2 = y * y;
            if (x2 + y2 > 4) return false;
            y = 2 * x * y + c.Imaginary;
            x = x2 - y2 + c.Real;
        }
        return true;
    }

    [Fact]
    public void Deep_Region_Survives_Save_Reload_And_Recall()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-OdLimbs-{Guid.NewGuid():N}";
        var live = DeepView();
        try
        {
            Assert.True(svc.SaveCurrentAsRegion(name, live));
            lib.Load();   // from regions.json on disk, not the in-memory copy

            var recalled = new FractalViewState();
            Assert.True(svc.ApplyRegion(name, recalled));
            Assert.Equal(Limbs(live), Limbs(recalled));   // all sixteen, bit-exact
            Assert.Equal(live.Zoom, recalled.Zoom);

            // The recalled view renders the minibrot that is really there…
            var (ok, inSet) = MaskVsMainSet(recalled);
            Assert.True(inSet > 500 && ok < 0.03, $"recalled view: {inSet} in-set, {ok:P1} mismatch");

            // …and the OD tail is what makes it so: the old QD-only recall
            // (tail dropped) misses it entirely.
            recalled.CenterX4 = recalled.CenterX5 = recalled.CenterX6 = recalled.CenterX7 = 0;
            recalled.CenterY4 = recalled.CenterY5 = recalled.CenterY6 = recalled.CenterY7 = 0;
            var (bad, _) = MaskVsMainSet(recalled);
            Assert.True(bad > 0.10, $"precondition: dropping the OD tail should lose the minibrot ({bad:P1} mismatch)");
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void Shallow_Regions_Serialise_As_Before_And_Old_Files_Load()
    {
        var shallow = new FractalRegion { Name = "s", CenterX = -0.75, CenterXLo = 1e-17, CenterX2 = 2e-34, Zoom = 1e30 };
        string json = JsonSerializer.Serialize(shallow);
        Assert.DoesNotContain("CenterX4", json);
        Assert.DoesNotContain("CenterY7", json);
        Assert.Contains("CenterX3", json);   // the QD limbs keep their old form

        var deep = new FractalRegion { Centre = Mini.Nucleus };
        var back = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(deep))!;
        Assert.Equal(Mini.Nucleus.Re.X5, back.CenterX5);
        Assert.Equal(Mini.Nucleus.Im.X7, back.CenterY7);

        // A pre-#1320 file: four limbs per axis, no OD fields.
        const string old = "{\"Name\":\"old\",\"CenterX\":-1.99,\"CenterY\":-5.5e-6,\"CenterXLo\":-7.8e-17," +
                           "\"CenterYLo\":-2.9e-22,\"CenterX2\":-5.6e-33,\"CenterX3\":2.2e-49,\"CenterY2\":-1.5e-38," +
                           "\"CenterY3\":8.8e-55,\"Zoom\":2.1e65}";
        var o = JsonSerializer.Deserialize<FractalRegion>(old)!;
        Assert.Equal(2.2e-49, o.CenterX3);
        Assert.Equal(0.0, o.CenterX4);
        Assert.Equal(0.0, o.CenterY7);
    }
}
