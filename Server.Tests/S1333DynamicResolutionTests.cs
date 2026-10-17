// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1333 — the interactive progressive preview's first stage follows the measured
// frame cost (DynamicResolution) instead of always starting at ¼. Checked:
//   • levels: ½ and ¼ are exactly the pre-#1333 sidecar sizes; every axis ≥ 64;
//     pixel counts fall with the level; the ladder after a stage halves the
//     divisor down to the full-resolution final;
//   • the controller, driven by a fake view whose cost is proportional to the
//     pixels it renders, across six decades of cost: it settles within a few
//     frames on the finest level meeting the target (independently: that level's
//     true time is within the band and the next finer level's is not), and then
//     never moves; with ±10% frame-time noise around the target it doesn't move
//     either, where a chooser without hysteresis flips back and forth; a fixed
//     per-stage cost (the screen resample) is told apart from the per-pixel cost;
//     previews start at ¼ until one was measured (only a final cheap enough for no
//     preview changes that); a 10× cost jump is followed at once; nothing
//     measured / switched off = ¼;
//   • the live host: an expensive view starts at ⅛ and climbs ⅛ → ¼ → ½ → full, a
//     cheap one skips the preview, switched off it starts at ¼ again, and every
//     final frame is byte-identical to a single full render; the host's own
//     frames feed both estimates; a type change starts them over.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1333DynamicResolutionTests
{
    // ── Levels ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(320, 256)]
    [InlineData(301, 97)]
    public void Levels_Keep_The_Old_Half_And_Quarter_And_Shrink_Monotonically(int w, int h)
    {
        Assert.Equal((w, h), DynamicResolution.PreviewDims(w, h, 0));
        Assert.Equal((Math.Max(64, w / 2), Math.Max(64, h / 2)), DynamicResolution.PreviewDims(w, h, 2));
        Assert.Equal((Math.Max(64, w / 4), Math.Max(64, h / 4)), DynamicResolution.PreviewDims(w, h, 4));
        Assert.Equal((Math.Max(64, w / 8), Math.Max(64, h / 8)), DynamicResolution.PreviewDims(w, h, 6));
        long prev = long.MaxValue;
        for (int l = 0; l <= DynamicResolution.MaxLevel; l++)
        {
            var (pw, ph) = DynamicResolution.PreviewDims(w, h, l);
            Assert.True(pw >= Math.Min(w, 64) && ph >= Math.Min(h, 64), $"level {l}: {pw}x{ph}");
            Assert.True((long)pw * ph <= prev, $"level {l} has more pixels than level {l - 1}");
            prev = (long)pw * ph;
        }
        // Odd levels sit half an octave between their neighbours.
        var (w3, _) = DynamicResolution.PreviewDims(1920, 1080, 3);
        Assert.Equal((int)Math.Round(1920 / (2 * Math.Sqrt(2))), w3);
    }

    [Fact]
    public void Ladder_Halves_The_Divisor_Down_To_The_Final()
    {
        static int[] Chain(int l) { var c = new List<int> { l }; while (l > 0) c.Add(l = DynamicResolution.NextLevel(l)); return c.ToArray(); }
        Assert.Equal(new[] { 4, 2, 0 }, Chain(4));            // the pre-#1333 ¼ → ½ → full
        Assert.Equal(new[] { 6, 4, 2, 0 }, Chain(6));
        Assert.Equal(new[] { 5, 3, 1, 0 }, Chain(5));
        Assert.Equal(new[] { 2, 0 }, Chain(2));
        Assert.Equal(new[] { 1, 0 }, Chain(1));
        Assert.Equal(new[] { 0 }, Chain(0));
    }

    // ── Controller ─────────────────────────────────────────────────────────────

    private const int W = 1920, H = 1080;

    private static long Px(int level) { var (pw, ph) = DynamicResolution.PreviewDims(W, H, level); return (long)pw * ph; }

    /// <summary>One interactive frame of a fake view costing <paramref name="msPerPx"/>
    /// per rendered pixel (times <paramref name="noise"/>): choose, render, record.</summary>
    private static int Frame(DynamicResolution d, double msPerPx, double noise = 1.0)
    {
        int l = d.Choose(W, H);
        d.Record((int)Px(l), msPerPx * Px(l) * noise, final: l == 0);
        return l;
    }

    public static IEnumerable<object[]> Costs()
    {
        // Six decades: from "a full 1080p frame takes 2 ms" to "even ⅛ takes seconds".
        foreach (double c in new[] { 1e-6, 3e-6, 1e-5, 2.2e-5, 5e-5, 1e-4, 3e-4, 1e-3, 1e-2, 1e-1 })
            yield return new object[] { c };
    }

    [Theory]
    [MemberData(nameof(Costs))]
    public void Settles_On_The_Finest_Level_Meeting_The_Target_And_Stays(double msPerPx)
    {
        var d = new DynamicResolution { TargetMs = 33 };
        Assert.Equal(DynamicResolution.DefaultLevel, d.Choose(W, H));   // nothing measured: ¼
        var levels = Enumerable.Range(0, 60).Select(_ => Frame(d, msPerPx)).ToArray();
        int settled = levels[^1];
        int firstSettled = Array.FindIndex(levels, l => l == settled);
        Assert.True(firstSettled <= 3, $"took {firstSettled} frames: {string.Join(",", levels.Take(8))}");
        Assert.All(levels.Skip(firstSettled), l => Assert.Equal(settled, l));   // never moves again

        // Independent: judged by the TRUE cost, not the controller's estimate.
        double t = d.TargetMs, at = msPerPx * Px(settled);
        Assert.True(at <= t * DynamicResolution.CoarsenAbove || settled == DynamicResolution.MaxLevel,
            $"settled at level {settled}: {at:F1} ms against {t} ms");
        if (settled > 0)
            Assert.True(msPerPx * Px(settled - 1) > t * DynamicResolution.RefineAtOrBelow,
                $"level {settled - 1} would also have fitted ({msPerPx * Px(settled - 1):F1} ms)");
    }

    public static IEnumerable<object[]> FixedCosts()
    {
        // (fixed ms of drawing a preview to the screen, ms per preview pixel)
        foreach (double f in new[] { 2.0, 10.0, 15.0, 25.0 })
        foreach (double c in new[] { 2e-5, 1e-4, 3e-4 })
            yield return new object[] { f, c };
    }

    [Theory]
    [MemberData(nameof(FixedCosts))]
    public void A_Fixed_Per_Stage_Cost_Is_Not_Mistaken_For_Per_Pixel_Cost(double fixedMs, double msPerPx)
    {
        // The GT 710 GPU view: drawing any preview to the 1080p screen costs ~15 ms
        // whatever its size, so per-pixel-only bookkeeping drove it to ⅛ for nothing.
        double True(int l) => l == 0 ? msPerPx * Px(0) : fixedMs + msPerPx * Px(l);
        var d = new DynamicResolution { TargetMs = 33 };
        int l = 0;
        for (int i = 0; i < 30; i++)
        {
            l = d.Choose(W, H);
            if (l == 0) d.Record((int)Px(0), True(0), final: true);
            else d.RecordPreview((int)Px(l), msPerPx * Px(l), fixedMs);
        }
        // Independent: by the true cost, the settled level is within the band, and
        // the next finer one (if any) is not under the refine bound.
        Assert.True(True(l) <= 33 * DynamicResolution.CoarsenAbove || l == DynamicResolution.MaxLevel,
            $"level {l}: {True(l):F1} ms");
        if (l > 0) Assert.True(True(l - 1) > 33 * DynamicResolution.RefineAtOrBelow, $"level {l - 1} also fits: {True(l - 1):F1} ms");
        Assert.Equal(fixedMs, d.PreviewFixedMs, 9);
    }

    [Fact]
    public void Previews_Start_At_A_Quarter_Until_One_Is_Measured()
    {
        // A cheap final (deferred GPU readback, say) says nothing about a preview's
        // fixed part: previews start at ¼ ...
        var d = new DynamicResolution { TargetMs = 33 };
        d.Record((int)Px(0), 30.0, final: true);   // a full frame in 30 ms: over 0.75 × target
        Assert.Equal(DynamicResolution.DefaultLevel, d.Choose(W, H));
        // ... unless the final alone already fits with room to spare: no preview.
        d.Record((int)Px(0), 10.0, final: true);
        Assert.Equal(0, d.Choose(W, H));
        // A dearer final from there goes back to ¼, not to a guessed level.
        d.Record((int)Px(0), 200.0, final: true);
        Assert.Equal(DynamicResolution.DefaultLevel, d.Choose(W, H));
    }

    [Fact]
    public void Extremes_Skip_The_Preview_Or_Use_An_Eighth()
    {
        var cheap = new DynamicResolution();
        for (int i = 0; i < 5; i++) Frame(cheap, 1e-6);   // a full 1080p frame in 2 ms
        Assert.Equal(0, cheap.Level);
        var dear = new DynamicResolution();
        for (int i = 0; i < 5; i++) Frame(dear, 1e-1);
        Assert.Equal(DynamicResolution.MaxLevel, dear.Level);
    }

    [Fact]
    public void Noise_Around_The_Target_Does_Not_Flip_The_Level_Without_Hysteresis_It_Would()
    {
        // A view whose ¼ frame costs exactly the target, ±10% per frame.
        double c = 33.0 / Px(4);
        var rng = new Random(1333);
        var noise = Enumerable.Range(0, 300).Select(_ => 0.9 + 0.2 * rng.NextDouble()).ToArray();

        var d = new DynamicResolution { TargetMs = 33 };
        var levels = noise.Select(n => Frame(d, c, n)).ToArray();
        int changes = levels.Zip(levels.Skip(1)).Count(p => p.First != p.Second);
        Assert.Equal(0, changes);
        Assert.Equal(4, levels[^1]);

        // The same frames through a chooser with no dead band (finest level whose
        // smoothed estimate is under the target) do flip.
        double est = double.NaN;
        int naiveChanges = 0, last = -1;
        foreach (double n in noise)
        {
            int l = double.IsNaN(est) ? 4 : Enumerable.Range(0, 7).FirstOrDefault(k => est * Px(k) <= 33.0, 6);
            double sample = c * n;
            est = double.IsNaN(est) ? sample : est + 0.5 * (sample - est);
            if (last >= 0 && l != last) naiveChanges++;
            last = l;
        }
        Assert.True(naiveChanges >= 10, $"precondition: the naive chooser flipped only {naiveChanges} times");
    }

    [Fact]
    public void A_Cost_Jump_Is_Followed_At_Once()
    {
        var d = new DynamicResolution { TargetMs = 33 };
        for (int i = 0; i < 6; i++) Frame(d, 1e-5);
        int before = d.Level;
        Frame(d, 1e-4);   // this frame is 10× dearer (a deep-zoom tier, relief switched on…)
        int next = d.Choose(W, H);
        Assert.True(next >= before + 2, $"level {before} → {next} after a 10× jump");
        Assert.True(1e-4 * Px(next) <= 33 * DynamicResolution.CoarsenAbove);
        // No overshoot: the move lands on the finest level that fits, not further.
        Assert.True(next == DynamicResolution.MaxLevel || 1e-4 * Px(next) <= 33, $"level {next} misses the target");
        Assert.True(1e-4 * Px(next - 1) > 33, $"overshot to level {next}: level {next - 1} fits ({1e-4 * Px(next - 1):F1} ms)");
    }

    [Fact]
    public void Off_Or_Unmeasured_Is_The_Old_Quarter_And_Reset_Forgets()
    {
        var d = new DynamicResolution();
        for (int i = 0; i < 5; i++) Frame(d, 1e-6);
        Assert.Equal(0, d.Level);
        d.Enabled = false;
        Assert.Equal(DynamicResolution.DefaultLevel, d.Choose(W, H));
        d.Enabled = true;
        d.Reset();
        Assert.True(double.IsNaN(d.PreviewMsPerPixel) && double.IsNaN(d.FinalMsPerPixel));
        Assert.Equal(DynamicResolution.DefaultLevel, d.Choose(W, H));
        d.TargetMs = 0.5; Assert.Equal(4.0, d.TargetMs);
        d.TargetMs = 1e6; Assert.Equal(500.0, d.TargetMs);
    }

    // ── Live host ──────────────────────────────────────────────────────────────

    private const int HW = 320, HH = 256;

    private sealed class RecordingRenderer : IFractalRenderer
    {
        public readonly List<(int W, int H)> Uploads = new();
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { lock (Uploads) Uploads.Add((width, height)); }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "record";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static FractalViewState DualOrbitState() => new()
    {
        FractalType = FractalType.JulibrotPair, CenterX = -0.6, CenterY = 0, Zoom = 1.0,
        FractalParameters = new FractalParameters { DualOrbitField = DualOrbitField.BasinEntropy, DualOrbitEnsembleN = 4 },
    };

    private static uint[] Frame(FractalRenderHost host, bool progressive)
    {
        using var done = new ManualResetEventSlim();
        EventHandler<FracturingFog.Render.RenderFrameInfo> h = (_, _) => done.Set();
        host.FrameCompleted += h;
        try
        {
            host.Trigger(progressive: progressive);
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");
        }
        finally { host.FrameCompleted -= h; }
        var snap = host.SnapshotFrame(out int sw, out int sh);
        return snap.Take(sw * sh).ToArray();
    }

    /// <summary>The preview uploads of one progressive trigger, after a first
    /// single frame (which sets the measurement key) and <paramref name="seed"/>.</summary>
    private static (List<(int W, int H)> previews, uint[] final, uint[] single) Drag(Action<DynamicResolution> seed, bool dynamic = true)
    {
        var renderer = new RecordingRenderer();
        using var host = new FractalRenderHost(renderer, DualOrbitState(), HW, HH, ColorPalette.BuiltIns[0]);
        host.DynamicPreviewResolution = dynamic;
        var single = Frame(host, progressive: false);
        seed(host.DynamicResolution);
        lock (renderer.Uploads) renderer.Uploads.Clear();
        var final = Frame(host, progressive: true);
        lock (renderer.Uploads) return (renderer.Uploads.Where(u => u != (HW, HH)).ToList(), final, single);
    }

    private static (int, int) Dims(int level) => DynamicResolution.PreviewDims(HW, HH, level);

    [Fact]
    public void Live_Expensive_View_Climbs_From_An_Eighth_And_Ends_On_The_Same_Final()
    {
        var (previews, final, single) = Drag(d => { d.Record(1, 1e4, final: false); d.Record(1, 1e4, final: true); });
        Assert.Equal(new[] { Dims(6), Dims(4), Dims(2) }, previews);
        Assert.Equal(single, final);
    }

    [Fact]
    public void Live_Cheap_View_Skips_The_Preview()
    {
        var (previews, final, single) = Drag(d => { d.Record(1_000_000, 0.1, final: false); d.Record(1_000_000, 0.1, final: true); });
        Assert.Empty(previews);
        Assert.Equal(single, final);
    }

    [Fact]
    public void Live_Switched_Off_Starts_At_A_Quarter()
    {
        var (previews, final, single) = Drag(d => { d.Record(1_000_000, 0.1, final: false); d.Record(1_000_000, 0.1, final: true); }, dynamic: false);
        Assert.Equal(new[] { Dims(4), Dims(2) }, previews);
        Assert.Equal(single, final);
    }

    [Fact]
    public void Live_Frames_Feed_Both_Estimates_And_A_Type_Change_Resets_Them()
    {
        var renderer = new RecordingRenderer();
        var state = DualOrbitState();
        using var host = new FractalRenderHost(renderer, state, HW, HH, ColorPalette.BuiltIns[0]);
        Frame(host, progressive: true);   // ¼ → ½ → full
        var d = host.DynamicResolution;
        Assert.True(d.PreviewMsPerPixel > 0, "the preview stages were not measured");
        Assert.True(d.FinalMsPerPixel > 0, "the final frame was not measured");

        state.FractalType = FractalType.Mandelbrot;
        state.FractalParameters = new FractalParameters();
        Frame(host, progressive: false);
        Assert.True(d.FinalMsPerPixel > 0);              // the new type's own final
        Assert.True(double.IsNaN(d.PreviewMsPerPixel));  // the old type's previews are gone
    }
}
