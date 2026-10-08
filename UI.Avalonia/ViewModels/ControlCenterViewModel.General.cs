// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.General.cs
//
// Explore ▸ Find for the families beyond z² + c — finder S7 (#1191).
// Multibrot (and the Generated z³/z⁴/z⁵), Burning Ship, Tricorn and
// autonomous User Equations get Zoom to minibrot, Snap to spiral and Julia
// morph through the double-precision R² Newton engine (GeneralFinder over an
// IOrbitMap). Detect period and external angles stay z² + c only. Like the
// Mandelbrot finders these only move centre / zoom / quality / first-render
// iterations, which the --batch Command builder already captures.

using System.Threading.Tasks;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    private FinderTrack TrackOf(FractalType t) => OrbitMaps.TrackFor(t);

    /// <summary>The current family's map for the general finders, built on the
    /// UI thread from a snapshot of its parameters. Null (with the reason) when
    /// the family has none, the User Equation does not compile or depends on
    /// n / prev, or the view is past double precision.</summary>
    private IOrbitMap? BuildOrbitMap(out string why)
    {
        var vs = Shell.Main.ViewState;
        why = "";
        if (vs.Zoom > AutoExplorer.DoubleMaxZoom)
        {
            why = $"{vs.FractalType} renders in double precision; the finders stop at zoom 1e13.";
            return null;
        }
        var fp = vs.FractalParameters;
        if (vs.FractalType != FractalType.UserEquation)
        {
            var map = OrbitMaps.For(vs.FractalType, fp);
            if (map == null) why = $"No finder for {vs.FractalType} — use Auto-explore.";
            return map;
        }

        string src = fp.UserEquationSource ?? "";
        if (string.IsNullOrWhiteSpace(src)) { why = "The User Equation has no source."; return null; }
        try
        {
            // The same translation the calculator compiles with.
            string dsl = FracturingFog.CalculatorGen.EquationPreprocessor.Preprocess(src, out FracturingFog.CalculatorGen.PreprocessDiagnostic? diag);
            if (diag != null) { why = $"The equation does not compile: {diag.Message}"; return null; }
            var step = SandboxExpression.Parse(dsl);
            if (!UserEquationMap.IsAutonomous(step))
            {
                why = "This equation depends on n / iter / prev, so it has no fixed minibrots — use Auto-explore.";
                return null;
            }
            string seedSrc = fp.UserEquationSeed?.Trim() ?? "";
            var seed = seedSrc.Length > 0 ? SandboxExpression.Parse(seedSrc) : null;
            return new UserEquationMap(step, seed, fp.EscapeRadius, src.Trim());
        }
        catch (System.Exception ex)
        {
            why = $"The equation does not compile: {ex.Message}";
            return null;
        }
    }

    private static V2 ToV2(in DeepComplex c) => new(c.Re.X0, c.Im.X0);
    private static DeepComplex ToDeep(V2 v) => new(v.X, v.Y);

    // S2 for the general families.
    private Task GeneralZoomToMinibrotAsync()
    {
        var map = BuildOrbitMap(out string why);
        if (map == null) { FinderStatus = why; return Task.CompletedTask; }
        var vs = Shell.Main.ViewState;
        var (w, h) = Shell.Main.RenderHost.LastPresentedSize;
        var centre = ToV2(vs.GetCenter());
        double radius = PeriodDetector.ViewDiskRadius(vs.Zoom, w, h);
        FinderStatus = $"Searching for a minibrot ({map.Name})…";
        return RunFinderAsync(async ct =>
        {
            int lastShown = 0;
            void OnPeriod(int p)
            {
                if (p - lastShown < 16 && lastShown != 0) return;
                lastShown = p;
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsFinderBusy) FinderStatus = $"Searching… trying period {p}"; });
            }
            var g = await Task.Run(() => GeneralFinder.FindMinibrot(map, centre, radius, onPeriod: OnPeriod, ct: ct), ct);
            if (!g.Found || !(g.Zoom > 0))
            {
                FinderStatus = $"No minibrot found in view (zoom {Fmt(vs.Zoom)}). Pan or zoom out and retry.";
                return;
            }
            if (g.Zoom > AutoExplorer.DoubleMaxZoom)
            {
                FinderStatus = $"Found a period-{g.Period} minibrot, but framing it needs zoom {Fmt(g.Zoom)} — past double precision (1e13).";
                return;
            }
            var plan = MinibrotJump.At(ToDeep(g.Point), g.Zoom, g.Period, vs.Quality, vs.IterLocked, vs.LockedIterations);
            if (plan is not MinibrotJumpPlan p) return;
            string note = ApplyPlan(p);
            FinderStatus = $"Period {g.Period} minibrot ({map.Name}) → zoom {Fmt(p.Zoom)}.{note}";
        });
    }

    // S3 for the general families: recentre on the Misiurewicz point.
    private void GeneralSnap(IOrbitMap map, V2 click, double reach)
    {
        var vs = Shell.Main.ViewState;
        FinderStatus = $"Searching for a spiral / branch point ({map.Name})…";
        _ = RunFinderAsync(async ct =>
        {
            var m = await Task.Run(() => GeneralFinder.FindNearMisiurewicz(map, click, reach, ct: ct), ct);
            if (!m.Found)
            {
                FinderStatus = $"No spiral / branch point within {SnapPickRadiusPx} px of the click. Click nearer its centre, or zoom in.";
                return;
            }
            Shell.RecordNavChange();
            vs.SetCenter(ToDeep(m.Point));
            Shell.Main.RenderHost.Trigger();
            string turn = m.TurnDegrees is double t ? $", turning {t:0.#}°" : "";
            FinderStatus = $"Misiurewicz point M({m.Preperiod},{m.Period}): the pattern repeats every ×{Fmt(m.Multiplier)} zoom{turn}.";
        });
    }
}
