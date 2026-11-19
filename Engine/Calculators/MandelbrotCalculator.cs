// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbrotCalculator.cs  — v5  (surface normal output)
//
// Changes over v4
//   • Two new output buffers: NormalXBuffer and NormalYBuffer (float[]).
//   • FillNormal() computes the outward normal to the escape-potential level
//     curve using the Inigo Quilez derivative technique.
//   • The HP (double-double) path now also tracks the complex derivative as
//     double (sufficient precision for smooth surface normals), so 3D colour
//     maps work at all zoom depths.
//   • BuildColorBuffer calls the five-parameter Map overload so 3D themes
//     receive normal data; flat themes use the default no-op override.
//
// Normal computation algorithm
// ──────────────────────────────────────────────────────────────────────────
//   Given:
//     z_n = (zr, zi)   — z value at escape
//     d_n = (dr, di)   — dz/dc derivative at escape (tracked in inner loop)
//
//   The outward normal direction is proportional to the complex expression:
//
//       z_n · conj(d_n)  =  (zr·dr + zi·di)  +  (zi·dr − zr·di)·i
//                            ──────────────────   ────────────────────
//                                  u (nx)                v (ny)
//
//   Reference: Inigo Quilez — "Rendering the Mandelbrot Set"
//              https://iquilezles.org/articles/mandelbrot/
//
//   Normalise by m = sqrt(u²+v²) to obtain nx ∈ [−1,1], ny ∈ [−1,1].
//   For in-set pixels, (nx, ny) = (0, 0).

// MandelbrotCalculator.cs  — v6
//
// Changes over v5
//   • HIGH IMPACT 1: 4-wide SIMD DD path (DD4, AVX2+FMA) replaces scalar
//     CalculateHighPrecision.  ~3–3.5× speedup on the HP path.
//   • HIGH IMPACT 2: IColorMap virtual dispatch eliminated for the live
//     render path via a generic Calculate<TMap> overload + concrete-type
//     dispatch in the public Calculate() entry point.
//   • HIGH IMPACT 3: Color computed inline — the live render path fills
//     ColorBuffer directly inside the per-pixel helpers, eliminating the
//     separate BuildColorBuffer pass and its cache-thrashing float[] reads.
//   • Intermediate float[] buffers (Smooth/Distance/NormalX/NormalY) are
//     still filled for the screenshot/poster path that needs them.
//     A bool parameter skips filling them on the live render path.

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using FracturingFog.Interefaces;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Calculators.Gpu;
using System.Diagnostics;
using GpuRoute = FracturingFog.Render.GpuRoute;
using GpuWavefront = FracturingFog.Rendering.GpuWavefront;

namespace FracturingFog;

/// <summary>#1331 — the GPU deep-zoom kernel precision (see
/// MandelbrotCalculator.GpuDeepPrecisionMode).</summary>
public enum GpuDeepPrecision { Auto, Double, Fp32 }

public sealed partial class MandelbrotCalculator : Interefaces.IHeightFieldSource, Interefaces.ITrapFieldSource, Interefaces.ISupportsHistogramEq,
    Interefaces.IDistanceFieldSource, Interefaces.IIterationFieldSource,   // #1489
    FracturingFog.Render.IGpuRouteSource
{
    // ── Public state ──────────────────────────────────────────────────────────

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>#1656 — guards the view (centre limbs, zoom, iteration cap) between the
    /// UI thread's FractalRenderHost.ApplyView and the calc thread's jitter / restore
    /// passes (CentreJitter.TryApply / RestoreIfOwned), so a pass that finishes after
    /// the user moved the view never writes the old centre back.</summary>
    public object ViewSync { get; } = new();

    public double CenterX { get; set; } = -0.5;
    public double CenterXLo { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double CenterYLo { get; set; } = 0.0;

    // QD limbs 2 and 3 — populated only at zoom > ~5e27 to support deeper
    // zoom (up to ~5e58). Default 0 → behaves as DD. Set by MainForm pan/zoom
    // when QD-aware navigation is engaged.
    public double CenterX2 { get; set; } = 0.0;
    public double CenterX3 { get; set; } = 0.0;
    public double CenterY2 { get; set; } = 0.0;
    public double CenterY3 { get; set; } = 0.0;

    // OD limbs 4..7 — populated only at zoom > 1e50 (Wave 2.11). Default 0
    // → behaves as QD. Set by Avalonia pan/zoom when OD-aware navigation
    // is engaged; until that lands, deep-zoom state arrives via API.
    public double CenterX4 { get; set; } = 0.0;
    public double CenterX5 { get; set; } = 0.0;
    public double CenterX6 { get; set; } = 0.0;
    public double CenterX7 { get; set; } = 0.0;
    public double CenterY4 { get; set; } = 0.0;
    public double CenterY5 { get; set; } = 0.0;
    public double CenterY6 { get; set; } = 0.0;
    public double CenterY7 { get; set; } = 0.0;

    public double Zoom { get; set; } = 1.0;

    // ── #1840 (epic #1428, C4) — depth past OD: exact centre + log zoom ──────
    // The limbs and Zoom stay the render contract. Past ApReferenceZoomLog10 the
    // reference orbit is built in BigFloat at the exact centre, when one is set and the
    // limbs are still its OD split (a jitter / pan / old writer of the limbs drops it, as
    // in FractalViewState, #1832), else at the limbs exactly.
    private ExactCentre? _centerExact;
    private DeepComplex _centerExactLimbs;
    private double _zoomLog10 = double.NaN, _zoomLog10Stamp;

    /// <summary>#1840 — the exact view centre, or null when none was set or the limbs
    /// have moved since.</summary>
    public ExactCentre? CenterExact
    {
        get => _centerExact is { } e && ExactCentre.SameLimbs(_centerExactLimbs, CurrentCentreLimbs()) ? e : null;
        set { _centerExact = value; if (value is { } v) _centerExactLimbs = v.ToDeep(); }
    }

    /// <summary>#1840 — log10 zoom at any depth: SetZoomLog10's while Zoom is unchanged
    /// since, else log10(Zoom).</summary>
    public double ZoomLog10
        => !double.IsNaN(_zoomLog10) && _zoomLog10Stamp.Equals(Zoom) ? _zoomLog10 : Math.Log10(Zoom);

    /// <summary>#1840 — set the zoom from its log10 (Zoom saturates past double).</summary>
    public void SetZoomLog10(double log10)
    {
        Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(log10);
        _zoomLog10 = log10;
        _zoomLog10Stamp = Zoom;
    }

    /// <summary>#1840 — the view's depth past OD: its exact centre (null: none) and, past
    /// double, its log zoom. Call after the limbs and Zoom are set.</summary>
    public void SetDepth(ExactCentre? exact, double zoomLog10 = double.NaN)
    {
        CenterExact = exact;
        if (double.IsFinite(zoomLog10) && FracturingFog.ViewState.ZoomLog.BeyondDouble(zoomLog10)) SetZoomLog10(zoomLog10);
    }

    /// <summary>#1840 — log10 zoom past which the reference orbit is built in BigFloat
    /// (tier 3, "AP-PT"). 110 = past Extreme's 1e100 (every view the Extreme tier reaches
    /// keeps the OD orbit, byte-identical; the Unbounded tier, #1861, goes past it); within OD's measured
    /// clean floor (~1e120, --qdfloorsweep).</summary>
    public double ApReferenceZoomLog10 { get; set; } = DefaultApReferenceZoomLog10;   // #1884: the cluster mirrors it

    private DeepComplex CurrentCentreLimbs() => DeepComplex.FromLimbs(
        CenterX, CenterXLo, CenterX2, CenterX3, CenterX4, CenterX5, CenterX6, CenterX7,
        CenterY, CenterYLo, CenterY2, CenterY3, CenterY4, CenterY5, CenterY6, CenterY7);

    private bool UseApReference => ZoomLog10 > ApReferenceZoomLog10;

    /// <summary>#1851 (epic #1428, C5e) — a sub-pixel AA jitter in pixels, added to every
    /// pixel's dc by the deep paths (the recycle shift for the HP rows, pixel units in the
    /// floatexp rows). <see cref="FracturingFog.Rendering.CentreJitter"/> sets it instead of
    /// moving the centre limbs where <see cref="JittersByPixel"/>: there a limb write would
    /// drop the exact centre (and its orbit), and past double the double jitter is lost.
    /// (0, 0) = none (the default: x + 0.0, byte-identical).</summary>
    public (double X, double Y) PixelJitter { get; set; }

    /// <summary>#1851 — AA jitters by pixel (<see cref="PixelJitter"/>) rather than by the
    /// centre limbs: past <see cref="ApReferenceZoomLog10"/>.</summary>
    public bool JittersByPixel => UseApReference;

    /// <summary>#1842 (epic #1428, C5a) — log10 zoom past which each pixel's perturbation
    /// runs in FloatExp (δ, dc and the derivative): past ~1e300 they underflow double. 290 =
    /// the double path still measured exact at 1e290 (S1842, against direct BigFloat
    /// iteration), so it keeps every zoom it handles; reached in the Unbounded tier (#1861).</summary>
    public double FloatExpZoomLog10 { get; set; } = 290;

    private bool UseFloatExpPixels => ZoomLog10 > FloatExpZoomLog10;
    private bool _lastFrameUsedFx;   // #1842: " FX" on the precision label

    // ── D-6b — sub-rect rendering ────────────────────────────────────────────
    //
    // When the calculator renders a sub-rect of a larger image (one tile of a
    // master-planned cluster job), <see cref="ImageWidth"/>/<see cref="ImageHeight"/>
    // give the full image dims and <see cref="SubRectOffsetX"/>/<see cref="SubRectOffsetY"/>
    // give the tile's top-left pixel position within that image. The scale +
    // per-pixel dc derive from the IMAGE coordinate system so every tile of
    // the same image shares one master-computed reference orbit (the orbit is
    // valid for the whole image, not just one tile).
    //
    // 0 = inactive (legacy single-render or per-tile-centre rendering). The
    // calculator falls back to Width/Height for both buffer dims and dc
    // geometry, identical to pre-D-6b behaviour.
    //
    // Engaging sub-rect mode forces the PT scalar inner loop (SIMD paths
    // assume per-pixel dc derives from the local Width/halfW pair; the
    // scalar path is the single source of truth for the dc-origin shift in
    // v1. SIMD support lands in a later slice).

    public int ImageWidth     { get; set; }
    public int ImageHeight    { get; set; }
    public int SubRectOffsetX { get; set; }
    public int SubRectOffsetY { get; set; }

    private int EffectiveImageWidth  => ImageWidth  > 0 ? ImageWidth  : Width;
    private int EffectiveImageHeight => ImageHeight > 0 ? ImageHeight : Height;

    // ── #1330 — pan pixel reuse on an anchored lattice ───────────────────────
    //
    // A deep-zoom pan normally re-renders the whole frame. On the anchored
    // lattice the centre fields stay at a fixed anchor (the reference centre)
    // and the frame's position is the integer pixel offset SubRectOffsetX/Y (the
    // D-6b sub-rect geometry, ImageWidth/Height = Width/Height): a pixel's dc is
    // (offset + x − W/2)·scale, so after a whole-pixel pan the pixels still on
    // screen keep exactly the same dc, and CalculatePanned shifts the buffers
    // and computes only the exposed rows / columns.
    //
    // Exactness also needs the SIMD perturbation rows to batch pixels by their
    // LATTICE column ((offset + x) mod lanes), not their screen column: SA picks
    // one skip per batch (the minimum over lanes), BLA one step (the largest
    // |δ|), and a glitch falls the whole batch back, so a pixel's result depends
    // on its batch. With LatticeAligned the batches start on lattice multiples
    // and the lanes off the frame (or outside the computed range) are computed
    // and discarded. The BLA validity radius is sized for offsets up to
    // LatticeMaxOffset so the table is the same at every offset of one anchor.
    // The result: a panned frame is byte-identical to a full LatticeAligned
    // render at the new offset (S1330PanReuseTests).

    /// <summary>#1330 — batch the SIMD perturbation rows on the global pixel lattice
    /// (see above). Deep CPU frames only; also keeps the frame off the GPU
    /// perturbation path. Off by default: output unchanged.</summary>
    public bool LatticeAligned { get; set; }

    /// <summary>#1330 — the largest |SubRectOffsetX/Y| (pixels) the BLA radius
    /// covers on the lattice; 0 = Max(Width, Height). Past it the caller should
    /// re-anchor (CalculatePanned falls back to a full render).</summary>
    public int LatticeMaxOffset { get; set; }

    private int EffectiveLatticeMaxOffset => LatticeMaxOffset > 0 ? LatticeMaxOffset : Math.Max(Width, Height);

    /// <summary>#1330 — pixels the last Calculate / CalculatePanned computed (deep
    /// CPU path; 0 elsewhere). A panned frame computes only the exposed strips.</summary>
    public long LastFrameComputedPixels { get; private set; }

    // The exposed region of a panned frame (null on a full frame), read by
    // CalculateHighPrecision: rows with (y + dy) outside the old frame are
    // computed whole, the other rows only their exposed columns.
    private (int Dx, int Dy)? _panRegion;
    // Set after a complete LatticeAligned deep CPU frame: what it rendered, so
    // CalculatePanned can check the next frame differs only by the offset.
    private PanState? _panState;

    private sealed record PanState(
        double CX0, double CX1, double CX2, double CX3, double CX4, double CX5, double CX6, double CX7,
        double CY0, double CY1, double CY2, double CY3, double CY4, double CY5, double CY6, double CY7,
        double Zoom, int Width, int Height, int MaxIterations, IColorMap ColorMap, QualityPreset Quality,
        double BlaTolerance, int InteriorAlpha, int OffX, int OffY, int MaxOffset);

    private PanState CapturePanState() => new(
        CenterX, CenterXLo, CenterX2, CenterX3, CenterX4, CenterX5, CenterX6, CenterX7,
        CenterY, CenterYLo, CenterY2, CenterY3, CenterY4, CenterY5, CenterY6, CenterY7,
        Zoom, Width, Height, MaxIterations, ColorMap, Quality, BlaTolerance, InteriorAlpha,
        SubRectOffsetX, SubRectOffsetY, EffectiveLatticeMaxOffset);

    /// <summary>#1330 — pan the anchored frame by whole pixels: the offset grows by
    /// (<paramref name="dx"/>, <paramref name="dy"/>), so new pixel (x, y) is old
    /// pixel (x + dx, y + dy). When the last frame was a complete LatticeAligned
    /// deep CPU frame of the same view (same anchor, zoom, size, iterations,
    /// palette, quality) and the new offset stays within
    /// <see cref="LatticeMaxOffset"/>, the buffers are shifted and only the
    /// exposed strips are computed; otherwise this is a full
    /// <see cref="Calculate"/>. The caller owns the other settings: change
    /// nothing else between the frames. Returns true when pixels were reused.</summary>
    public bool CalculatePanned(int dx, int dy, CancellationToken ct = default)
    {
        bool reuse = CanReusePan(dx, dy);
        SubRectOffsetX += dx;
        SubRectOffsetY += dy;
        if (!reuse) { Calculate(ct); return false; }
        ShiftPanBuffers(dx, dy);
        _panRegion = (dx, dy);
        try { Calculate(ct); }
        finally { _panRegion = null; }
        return true;
    }

    private bool CanReusePan(int dx, int dy)
    {
        var st = _panState;
        if (st is null || !LatticeAligned) return false;
        if (Math.Abs(dx) >= Width || Math.Abs(dy) >= Height) return false;
        int maxOff = EffectiveLatticeMaxOffset;
        if (Math.Abs((long)SubRectOffsetX + dx) > maxOff || Math.Abs((long)SubRectOffsetY + dy) > maxOff) return false;
        // Neighbourhood / whole-frame passes after the core would need the whole frame.
        if (ColorMap is IOrbitAwareColorMap or IInteriorAwareColorMap or IPostProcessColorMap) return false;
        if (InteriorAlpha < 255 || PerRowMaxIter != null) return false;
        // The last frame's offset is the current one (dx / dy not applied yet).
        return st == CapturePanState();
    }

    // The deep path's outputs (the orbit / interior buffers belong to colour maps
    // CanReusePan refuses).
    private void ShiftPanBuffers(int dx, int dy)
    {
        int w = Width, h = Height;
        ShiftBuffer(IterationBuffer, w, h, dx, dy);
        ShiftBuffer(SmoothBuffer, w, h, dx, dy);
        ShiftBuffer(DistanceBuffer, w, h, dx, dy);
        ShiftBuffer(NormalXBuffer, w, h, dx, dy);
        ShiftBuffer(NormalYBuffer, w, h, dx, dy);
        ShiftBuffer(ColorBuffer, w, h, dx, dy);
        ShiftBuffer(FinalZrBuffer, w, h, dx, dy);
        ShiftBuffer(FinalZiBuffer, w, h, dx, dy);
        ShiftBuffer(FinalDrBuffer, w, h, dx, dy);
        ShiftBuffer(FinalDiBuffer, w, h, dx, dy);
    }

    /// <summary>In place: new (x, y) = old (x + dx, y + dy) where that is inside the
    /// frame; the exposed pixels keep stale values (they are recomputed).</summary>
    public static void ShiftBuffer<T>(T[] buf, int w, int h, int dx, int dy)
    {
        if (buf.Length < w * h || (dx == 0 && dy == 0)) return;
        int x0 = Math.Max(0, -dx), x1 = Math.Min(w, w - dx);   // destination columns with a source
        int len = x1 - x0;
        if (len <= 0) return;
        // Rows in the order that never reads a row already overwritten.
        if (dy >= 0)
            for (int y = 0; y + dy < h; y++)
                Array.Copy(buf, (y + dy) * w + x0 + dx, buf, y * w + x0, len);
        else
            for (int y = h - 1; y + dy >= 0; y--)
                Array.Copy(buf, (y + dy) * w + x0 + dx, buf, y * w + x0, len);
    }

    private static int LatticeMod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    /// <summary>Zoom threshold above which QD ref orbit is used (else DD).</summary>
    private const double QDZoomThreshold = 1e25;

    /// <summary>Zoom threshold above which BLA table builds in DD precision
    /// (Wave 2.10). Below that the legacy single-precision merge runs ~7×
    /// faster and the precision loss isn't ULP-visible (banding only appears
    /// past ~1e15 due to 14+ merge-level ULP accumulation). Tunable via
    /// `DisableDdBla` toggle for diagnostic A/B.</summary>
    private const double DdBlaZoomThreshold = 1e15;

    /// <summary>Zoom threshold above which OD ref orbit is used (else QD).
    /// Wave 2.11 — 8-limb octuple-double centre + reference orbit.
    /// QD ceiling is ~1×10⁶⁴ (coordinate-separation floor, --qdfloorsweep:
    /// 128/128 distinct at 1e64, collapses at 1e66); OD pushes to ~10¹¹⁶.
    /// Engaged just below QD's wall so a single zoom step doesn't bounce
    /// off the precision floor.
    /// Verified by `OctupleDoubleTests.RefOrbit_ModerateZoom_OdMatchesQd`:
    /// OD matches QD to better than 1e-55 through the first 200 orbit
    /// iterations on a representative coord.</summary>
    private const double ODZoomThreshold = 1e50;

    public int MaxIterations { get; set; } = 512;

    /// <summary>T3.1 phase 1: zoom ceiling above which GPU dispatch falls
    /// back to CPU. The shader uses FP32 with a split-centre + split-scale
    /// approximation that lifts the practical zoom floor a few digits past
    /// raw float32 but isn't true double-double. Past ~1e4 the per-pixel
    /// `cx = cxHi + fx*scaleHi + cxLo + fx*scaleLo` reconstruction suffers
    /// catastrophic cancellation against the FP32 ULP, producing pixelation
    /// + wrong cardioid/bulb early-out predicates that collapse whole
    /// regions to the in-set colour. CPU (double precision) handles past
    /// 1e12 cleanly; HP (DD/QD) takes over from there. Conservatively
    /// gates GPU off at 1e4 so user-visible zooms never enter the
    /// FP32-broken band.</summary>
    public const double MaxGpuZoom = 1e4;

    /// <summary>#1611 — the fp32 shallow kernel also needs the pixel spacing to be at
    /// least this many fp32 ulps of the frame's largest |c| coordinate. The zoom
    /// ceiling alone let it draw visibly moved features near 1e4 (8000x: 1.3 ulps a
    /// pixel at 4K, 2.6 at 1080p). --gpuprecisionprobe (GT 710, four views at 1080p
    /// and 4K): at K ≥ 8 the picture moves by at most ~0.07 px (p95), at K ≈ 1 by up
    /// to 0.55 px with a third of the smooth pixels past 0.25 px. 0 turns the check
    /// off (probes); FF_GPU_FP32_MIN_ULPS sets the process default.</summary>
    public static double Fp32MinPixelUlps { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("FF_GPU_FP32_MIN_ULPS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double u) && u >= 0
            ? u : 8;

    /// <summary>#1611 — whether the fp32 shallow GPU kernel resolves a view: zoom
    /// within <see cref="MaxGpuZoom"/> and pixels at least
    /// <see cref="Fp32MinPixelUlps"/> fp32 ulps apart at the frame's largest |c|.</summary>
    public static bool Fp32GpuResolves(double cx, double cy, double zoom, int imageW, int imageH)
    {
        if (zoom > MaxGpuZoom) return false;
        double scale = 3.5 / Math.Max(imageW, imageH) / zoom;
        double m = Math.Max(Math.Abs(cx) + imageW * 0.5 * scale, Math.Abs(cy) + imageH * 0.5 * scale);
        return scale >= Fp32MinPixelUlps * Math.Max(m, 1.0) * (1.0 / (1 << 23));
    }

    private bool Fp32GpuResolvesView
        => Fp32GpuResolves(CenterX, CenterY, Zoom, EffectiveImageWidth, EffectiveImageHeight);

    /// <summary>#1640 — why this view renders on the CPU whatever the device (the
    /// route's reasons that need no kernel), or null when a GPU could draw it. Lets a
    /// headless render skip opening a device it would not use.</summary>
    public string? CpuOnlyView()
    {
        if (Zoom > MaxGpuZoom)
        {
            if (!Quality.NeedsHighPrecision(Zoom))
                return "past the FP32 GPU zoom limit (1e4); deeper zooms render on the CPU";
            // #1665: a device measured faster may take it (its key isn't known before it opens).
            return UseGpuPerturbation || (UseDeepRouteCalibration && GpuPerturbEnvSwitch != false && DeepRouteStore.AnyGpuDeepFor()) ? null
                : "deep zoom renders on the CPU unless GPU perturbation is enabled (--gpu-deep on in --batch; live: Control Center \u2192 Advanced \u2192 GPU deep zoom; or FF_GPU_PERTURB=1)";
        }
        return Fp32GpuResolvesView ? null
            : "pixels this small are below the FP32 GPU kernel's precision at this size; it renders on the CPU";
    }

    /// <summary>V6 (#82): upper zoom for the GPU deep-zoom PERTURBATION path
    /// (<see cref="RunPerturb"/> on an FP64-capable kernel). Above
    /// <see cref="MaxGpuZoom"/> the FP32 escape-time kernel is unusable, but the
    /// double δ-rebased perturbation kernel is not: it consumes the same Hi-limb
    /// double reference orbit + single-double dc the CPU <c>ComputePixelPTRebased</c>
    /// uses, so it is bit-faithful to the CPU deep path wherever that path runs
    /// (verified at the precision noise floor by --vulkanpturbprobe / dev-plan
    /// §14). The δ loop is depth-independent (rebasing, SM-2); this ceiling
    /// (= <see cref="ODZoomThreshold"/>, 1e50) is caution, not a math limit —
    /// the deep-dc precision re-check (#82 checkbox 2) gates lifting it further.</summary>
    public const double MaxGpuPerturbZoom = ODZoomThreshold;

    /// <summary>#1331 — which kernel the plain GPU deep-zoom frame runs:
    /// <c>Auto</c> = the fp32 kernels (scaled fp32 / float-exp) only on a device
    /// without fp64, the double kernel otherwise; <c>Fp32</c> = the fp32 kernels
    /// wherever available (3–8× faster on weak-fp64 cards, but ~0.1–0.4% of
    /// pixels wrong at hard views: a 24-bit mantissa); <c>Double</c> = never fp32.
    /// Process-wide; from FF_GPU_PERTURB_PRECISION (auto / double / fp32).</summary>
    public static GpuDeepPrecision GpuDeepPrecisionMode { get; set; } =
        ParseGpuDeepPrecision(Environment.GetEnvironmentVariable("FF_GPU_PERTURB_PRECISION"));

    /// <summary>#1331 — FF_GPU_PERTURB_PRECISION → mode (unknown / unset = Auto).</summary>
    public static GpuDeepPrecision ParseGpuDeepPrecision(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "double" or "fp64" => GpuDeepPrecision.Double,
        "fp32" or "float" or "fast" => GpuDeepPrecision.Fp32,
        _ => GpuDeepPrecision.Auto,
    };

    /// <summary>#1331 — compatibility switch for the spike / probes: true forces
    /// <see cref="GpuDeepPrecision.Fp32"/>, false restores the mode it replaced.</summary>
    public static bool UseGpuFloatExpPerturbation
    {
        get => GpuDeepPrecisionMode == GpuDeepPrecision.Fp32;
        set => GpuDeepPrecisionMode = value ? GpuDeepPrecision.Fp32 : GpuDeepPrecision.Auto;
    }

    /// <summary>#1331 — the last frame's GPU perturbation ran an fp32 kernel.</summary>
    public bool LastFrameUsedGpuFloatExp { get; private set; }

    /// <summary>#1340 — true when the last GPU perturbation frame ran the wavefront
    /// schedule (<see cref="GpuWavefront"/>). Reset at the start of every frame.</summary>
    public bool LastFrameUsedGpuWavefront { get; private set; }

    /// <summary>#1340 — the last GPU perturbation frame's <see cref="GpuWavefront.WarpWaste"/>
    /// (measured under <see cref="GpuWavefront.Auto"/>), or 0 when not measured.</summary>
    public double LastGpuWarpWaste { get; private set; }

    // #1340 — Auto: run the next GPU perturbation frame in the wavefront schedule
    // (the last one's warp waste reached GpuWavefront.PerturbWasteThreshold).
    private bool _gpuWavefrontNext;

    // #1386 — the view the hint was measured for (this calculator's own frame, a
    // preview's seed, or the seed dispatch); null until one is.
    private WavefrontView? _gpuWavefrontHintView;
    private int[]? _seedIt;
    private float[]? _seedSm, _seedZr, _seedZi, _seedDr, _seedDi;

    /// <summary>#1386 — the last seeded warp-waste estimate (<see cref="SeedGpuWavefront"/>
    /// or the seed dispatch), or 0 when none.</summary>
    public double LastGpuWavefrontSeed { get; private set; }

    /// <summary>#1386 — the last frame's seed dispatch iterations (<see cref="LastWavefrontSeedSize"/>),
    /// or null when it ran none (probes / tests).</summary>
    public int[]? LastWavefrontSeedIterations => LastFrameRanWavefrontSeed ? _seedIt : null;

    /// <summary>#1386 — the size of <see cref="LastWavefrontSeedIterations"/>.</summary>
    public (int W, int H) LastWavefrontSeedSize { get; private set; }

    /// <summary>#1386 — whether this calculator may run the seed dispatch at all (the
    /// host's preview sidecars don't: they are fast stand-ins, and seed the
    /// full-resolution calculator themselves).</summary>
    public bool WavefrontSeedAllowed { get; set; } = true;

    /// <summary>#1386 — the last frame ran the ¼-resolution seed dispatch
    /// (<see cref="GpuWavefront.Seed"/>). Reset at the start of every frame.</summary>
    public bool LastFrameRanWavefrontSeed { get; private set; }

    private sealed class WavefrontView(double[] x, double[] y, double zoom, int maxIter)
    {
        public readonly double[] X = x, Y = y;
        public readonly double Zoom = zoom;
        public readonly int MaxIter = maxIter;
    }

    private WavefrontView CurrentWavefrontView() => new(
        [CenterX, CenterXLo, CenterX2, CenterX3, CenterX4, CenterX5, CenterX6, CenterX7],
        [CenterY, CenterYLo, CenterY2, CenterY3, CenterY4, CenterY5, CenterY6, CenterY7],
        Zoom, MaxIterations);

    /// <summary>#1386 — the hint is for a view near this one: the same iteration
    /// cap, a zoom within 2×, and a centre within one frame width (limb by limb,
    /// so the difference is exact enough at any depth).</summary>
    private bool WavefrontHintNear()
    {
        var h = _gpuWavefrontHintView;
        if (h == null || h.MaxIter != MaxIterations || !(Zoom > 0) || !(h.Zoom > 0)) return false;
        double ratio = Zoom / h.Zoom;
        if (!(ratio >= 0.5 && ratio <= 2)) return false;
        var v = CurrentWavefrontView();
        double dx = 0, dy = 0;
        for (int k = 0; k < 8; k++) { dx += v.X[k] - h.X[k]; dy += v.Y[k] - h.Y[k]; }
        double frame = 3.5 / Zoom;
        return Math.Abs(dx) <= frame && Math.Abs(dy) <= frame;
    }

    private void SetWavefrontHint(double waste)
    {
        _gpuWavefrontNext = waste >= GpuWavefront.PerturbWasteThreshold;
        _gpuWavefrontHintView = CurrentWavefrontView();
    }

    /// <summary>#1386 — seed the next GPU deep frame's <see cref="GpuWavefront.Auto"/>
    /// choice with <paramref name="estimatedWaste"/> for the current view: the host
    /// passes <see cref="GpuWavefront.EstimateFullWaste"/> of a lower-resolution
    /// preview of this view. The frame itself measures its own waste and replaces it.</summary>
    public void SeedGpuWavefront(double estimatedWaste)
    {
        LastGpuWavefrontSeed = estimatedWaste;
        SetWavefrontHint(estimatedWaste);
    }

    // #1386 — opt-in (GpuWavefront.Seed): with no hint for a nearby view, run the
    // frame's deep kernel at 1/SeedDivisor resolution per axis (same orbit, SA and
    // BLA; one thread per pixel) and seed the hint from its estimate. A pixel (x', y')
    // of the seed frame sits at the full frame's (x' f, y' f). A failure only skips
    // the seed: the frame's own dispatch reports any real fault.
    private void SeedWavefrontIfAsked(Action<int, int, double, double, double, int[], float[], float[], float[], float[], float[]> dispatch,
        double scale, double offX0, double offY0)
    {
        if (GpuWavefront.Mode != GpuWavefront.On || GpuWavefront.PerturbMode != GpuWavefront.Auto
            || !GpuWavefront.Seed || !WavefrontSeedAllowed || WavefrontHintNear()) return;
        int sw = Math.Max(8, Width / GpuWavefront.SeedDivisor);
        double f = Width / (double)sw;
        int sh = Math.Max(8, (int)Math.Round(Height / f));
        int n = sw * sh;
        if (_seedIt == null || _seedIt.Length != n)
        {
            _seedIt = new int[n];
            _seedSm = new float[n]; _seedZr = new float[n]; _seedZi = new float[n]; _seedDr = new float[n]; _seedDi = new float[n];
        }
        bool hint = GpuWavefront.PerturbHint;
        GpuWavefront.PerturbHint = false;
        try { dispatch(sw, sh, scale * f, offX0 / f, offY0 / f, _seedIt, _seedSm!, _seedZr!, _seedZi!, _seedDr!, _seedDi!); }
        catch { return; }
        finally { GpuWavefront.PerturbHint = hint; }
        double est = GpuWavefront.EstimateFullWaste(_seedIt, sw, sh, f);
        LastGpuWavefrontSeed = est;
        LastFrameRanWavefrontSeed = true;
        LastWavefrontSeedSize = (sw, sh);
        SetWavefrontHint(est);
    }

    /// <summary>#1331 — the fp32 kernels for this device under the current mode.</summary>
    public static bool UseFloatExpKernel(FracturingFog.Rendering.IGpuKernel k) => k.SupportsPerturbationFloatExp && GpuDeepPrecisionMode switch
    {
        GpuDeepPrecision.Fp32 => true,
        GpuDeepPrecision.Auto => AutoPicksFloatExp(k),   // #1665: the measured choice, else fp32 only without fp64
        _ => false,
    };

    /// <summary>V6 (#82): master toggle for the GPU deep-zoom perturbation path.
    /// Off by default — the deep path stays CPU unless explicitly enabled AND a
    /// perturbation-capable kernel (<c>IGpuKernel.SupportsPerturbation</c>) is
    /// attached. Independent of <see cref="UseGpuCompute"/> (the shallow FP32
    /// path); a session may run either, both, or neither. The <c>--vulkanpturbprobe</c>
    /// gate exercises the kernel directly and does not need this flag.</summary>
    public static bool UseGpuPerturbation { get; set; } = false;

    // #86 diagnostic — opt-in via FF_GPU_PERTURB_DEBUG=1, writes to the same
    // %TEMP%/ff_gpu_perturb_86.log the render host uses (WinExe has no console).
    // Explains why a deep frame did or did not take the GPU perturbation path.
    private static readonly bool s_dbg86 =
        Environment.GetEnvironmentVariable("FF_GPU_PERTURB_DEBUG") is "1" or "true" or "yes" or "on";
    private static readonly string s_dbg86Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff_gpu_perturb_86.log");
    private static readonly object s_dbg86Gate = new();
    private static void Dbg86(string msg)
    {
        if (!s_dbg86) return;
        try
        {
            lock (s_dbg86Gate)
                System.IO.File.AppendAllText(s_dbg86Path,
                    $"{DateTime.Now:HH:mm:ss.fff} [#86] {msg}{Environment.NewLine}");
        }
        catch { /* diagnostic must never break the calc path */ }
    }

    /// <summary>T3.1: when true and a GPU kernel is attached via
    /// <see cref="SetGpuKernel"/>, the SP path
    /// (<see cref="CalculateDoublePrecision{TMap}"/>) dispatches the
    /// iteration loop to a D3D11 compute shader instead of the CPU
    /// Parallel.ForEach SIMD path. Palette evaluation stays CPU (phase
    /// 1); the kernel writes back iter + smooth, the existing color
    /// stage fills aux buffers + emits ColorBuffer. Falls back to CPU
    /// when no kernel is set, when HP precision is active, when an
    /// orbit-aware ColorMap is selected (the kernel doesn't sample z
    /// per iteration), or when PerRowMaxIter is non-null (per-row caps
    /// not yet plumbed to the shader).</summary>
    public bool UseGpuCompute { get; set; }

    /// <summary>T3.1: GPU compute kernel attached by the host when the
    /// active renderer is D3D11. Null = CPU-only; UseGpuCompute has
    /// no effect.</summary>
    public FracturingFog.Rendering.IGpuKernel? GpuKernel { get; set; }

    /// <summary>Optional per-row iteration cap. When non-null and sized to
    /// <see cref="Height"/>, row y uses <c>PerRowMaxIter[y]</c> instead of
    /// the global <see cref="MaxIterations"/>. Used by the video pipeline's
    /// PerTile cap mode to keep boundary-rich rows at full quality while
    /// capping interior-dominated rows where the extra iterations don't
    /// produce visible detail. Currently honoured by the SP path only; HP
    /// (DD/QD perturbation) paths fall back to <see cref="MaxIterations"/>.</summary>
    public int[]? PerRowMaxIter { get; set; }

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;

    /// <summary>True when the last Calculate() used double-double arithmetic.</summary>
    public bool IsHighPrecisionActive { get; private set; }

    /// <summary>#1321 — the precision path the last frame actually ran: "SP"
    /// (plain double), or the tier of the reference orbit it built + perturbation:
    /// "DD-PT" (to 1e25), "QD-PT" (to 1e50), "OD-PT" (beyond), with " LA" appended when
    /// the LA stage evaluator rendered it (#1629, e.g. "QD-PT LA"). Read by the status
    /// bar and perf HUD, which used to collapse every deep frame to "DD".</summary>
    public string LastPrecisionLabel { get; private set; } = "SP";

    // #1321 — label for the reference-orbit tier just built (_refCachedTier).
    private string PerturbationLabel() => (_refCachedTier switch
    {
        3 => "AP-PT",   // #1840
        2 => "OD-PT",
        1 => "QD-PT",
        _ => "DD-PT",
    }) + (_lastFrameUsedLa ? " LA" : "") + (_lastFrameUsedFx ? " FX" : "");   // #1629 LA; #1842 floatexp pixels

    /// <summary>V6 (#82) — true when the last high-precision frame actually ran
    /// the GPU perturbation kernel (not a CPU fallback). Read by the render host
    /// to surface a "(GPU)" marker in the perf HUD so the user can see the deep-
    /// zoom GPU path is engaged. Reset at the start of every HP frame.</summary>
    public bool LastFrameUsedGpuPerturbation { get; private set; }

    /// <summary>#88 / G4.5 — true when the last GPU perturbation frame ran the
    /// Series-Approximation kernel (<see cref="IGpuKernel.RunPerturbSA"/>) rather than
    /// the plain rebased loop. Reset at the start of every HP frame.</summary>
    public bool LastFrameUsedGpuSeriesApproximation { get; private set; }

    /// <summary>#88 / G4.5b — true when the last GPU perturbation frame also skipped
    /// with the BLA table. Reset at the start of every HP frame.</summary>
    public bool LastFrameUsedGpuBla { get; private set; }

    /// <summary>#1630 — true when the last GPU perturbation frame ran the LA stage kernel
    /// (<see cref="IGpuKernel.RunPerturbLa"/>). Reset at the start of every HP frame.</summary>
    public bool LastFrameUsedGpuLa { get; private set; }

    /// <summary>#1630 — run the LA stages in the GPU perturbation kernel when the frame
    /// wants LA (<see cref="LaMode"/>) and the kernel has it. Off by default: on the only
    /// GPU measured (GT 710, Vulkan) it was slower than the SA / BLA kernel (design doc
    /// §4.6). FF_GPU_LA=1 turns it on.</summary>
    public static bool UseGpuLa { get; set; } = Environment.GetEnvironmentVariable("FF_GPU_LA") == "1";

    /// <summary>#1664 — run the LA stages in the float-exp GPU kernel
    /// (<see cref="IGpuKernel.RunPerturbLaFloatExp"/>) where the frame wants LA and the
    /// float-exp kernel is this device's deep kernel (<see cref="UseFloatExpKernel"/>). Off
    /// by default until measured on a GPU it pays on; FF_GPU_LA_FX=1 turns it on. Takes
    /// precedence over the fp64 LA kernel when both apply.</summary>
    public static bool UseGpuLaFloatExp { get; set; } = Environment.GetEnvironmentVariable("FF_GPU_LA_FX") == "1";

    /// <summary>#1664 — true when the last GPU perturbation frame ran the float-exp LA kernel.</summary>
    public bool LastFrameUsedGpuLaFloatExp { get; private set; }

    /// <summary>#1880 — true when that kernel ran the float-exp LA table (C5c,
    /// <see cref="LaStageTableFx"/>) because the double table's numbers overflow.</summary>
    public bool LastFrameUsedGpuLaTableFx { get; private set; }

    /// <summary>#1664 — the float-exp LA route's zoom limit. Its numbers have no range
    /// limit at depth (float-exp); the pixel scale goes up as float-exp from the double
    /// scale, and the pixel offset δc is a double, as on the CPU LA rows. Past
    /// <see cref="MaxGpuPerturbZoom"/> only this route runs on the GPU (checked against the
    /// CPU LA frame on the bench scenes, S1664GpuLaFxTests). #1880 (epic #1428, C6a) — 1e100
    /// → 1e290: the CPU keeps the double rows (and their scale) up to
    /// <see cref="FloatExpZoomLog10"/>, and so does this route, on the AP reference orbit
    /// past 1e110 (C4); past that the frame takes the CPU float-exp rows (S1880).</summary>
    public const double MaxGpuLaFloatExpZoom = 1e290;

    // #1664 — this frame would take the float-exp LA kernel (before the table is built).
    private bool LaFxRouteHere(FracturingFog.Rendering.IGpuKernel? k)
        => k != null && UseGpuLaFloatExp && LaWanted && k.SupportsPerturbationLaFloatExp && FloatExpHere(k)
           && !DisableAcceleration && LaClimbOnRebase && !LaTailBla && AllowPtRebasing && !UseDdRebaseReference;

    // The deep GPU kernels' zoom limit for this frame.
    private double GpuPerturbZoomLimit => LaFxRouteHere(GpuKernel) ? MaxGpuLaFloatExpZoom : MaxGpuPerturbZoom;

    /// <summary>#1166 — true when the last frame (or <see cref="BuildReferenceOrbit"/>)
    /// built its QD reference orbit on the GPU (<see cref="UseGpuReferenceOrbit"/>),
    /// false when it was built on the CPU, recycled or served from the cache.</summary>
    public bool LastFrameBuiltGpuReferenceOrbit { get; private set; }

    /// <summary>#1166 — the accelerator the GPU reference orbit runs on ("Cuda — …",
    /// or "CPU — …" for ILGPU's CPU accelerator), or empty before its first use.</summary>
    public string GpuReferenceOrbitDevice => _refOrbitGpu?.SelectedDeviceLabel ?? string.Empty;

    /// <summary>#607 / G4.6 — true when the last frame was an orbit theme rendered on
    /// the GPU deep-zoom orbit kernel (<see cref="IGpuKernel.RunPerturbOrbit"/>); it
    /// also sets <see cref="LastFrameUsedGpuPerturbation"/>.</summary>
    public bool LastFrameUsedGpuOrbitPerturbation { get; private set; }

    /// <summary>#607 / G4.6 — let a deep-zoom orbit ColorGen theme (a GPU orbit
    /// palette, exterior colouring) accumulate its orbit inputs on the GPU
    /// perturbation kernel. On by default — the GPU twin of the CPU deep orbit path
    /// (#609), parity-checked on both backends. Still needs
    /// <see cref="UseGpuPerturbation"/>. <c>FF_GPU_DEEP_ORBIT=0</c> turns it off.</summary>
    public static bool UseGpuDeepOrbit { get; set; } =
        Environment.GetEnvironmentVariable("FF_GPU_DEEP_ORBIT") is not ("0" or "false" or "off" or "no");

    /// <summary>#88 / G4.5b — let GPU perturbation skip with BLA (the CPU's table,
    /// applied in-kernel) when the kernel supports the SA kernel. On by default (the
    /// CPU deep path always applies BLA); honours <see cref="DisableAcceleration"/>.
    /// <c>FF_GPU_BLA=0</c> turns it off.</summary>
    public static bool UseGpuBla { get; set; } =
        Environment.GetEnvironmentVariable("FF_GPU_BLA") is not ("0" or "false" or "off" or "no");

    /// <summary>#88 / G4.5 — let GPU perturbation skip with Series Approximation
    /// when the kernel supports it. On by default (the CPU deep path always applies
    /// SA, so the GPU frame now shares its truncation); it also honours
    /// <see cref="DisableAcceleration"/> / <see cref="DisableSeriesApproximation"/>
    /// like the CPU. <c>FF_GPU_SA=0</c> turns it off.</summary>
    public static bool UseGpuSeriesApproximation { get; set; } =
        Environment.GetEnvironmentVariable("FF_GPU_SA") is not ("0" or "false" or "off" or "no");

    /// <summary>#1162 — true when the last single-precision frame ran its
    /// iteration loop on the <see cref="GpuKernel"/> (<see cref="UseGpuCompute"/>
    /// path) rather than falling back to the CPU SIMD loop. Reset at the start
    /// of every frame. Lets the GPU benchmark refuse to time a CPU fallback.</summary>
    public bool LastFrameUsedGpuCompute { get; private set; }

    /// <summary>#1173-M — GPU route of the last frame (GPU, or CPU and why),
    /// resolved at the end of <see cref="Calculate"/>.</summary>
    public GpuRoute LastGpuRoute { get; private set; }

    // #1173-M — a specific reason recorded by a GPU gate or dispatch this frame
    // (orbit theme, deep-zoom gate, kernel error). Null = derive it from state.
    private GpuRoute? _gpuSkip;

    // #1173-M — why GPU perturbation was switched off for the session (the
    // TOO-SLOW / device-lost latches clear the static UseGpuPerturbation).
    private static GpuRoute? s_gpuPerturbDisabled;

    private GpuRoute ResolveGpuRoute()
    {
        var kernel = GpuKernel;
        string label = kernel?.BackendLabel ?? "GPU";
        bool hp = IsHighPrecisionActive;
        if (!hp && LastFrameUsedGpuCompute) return GpuRoute.OnGpu(label);
        if (hp && LastFrameUsedGpuOrbitPerturbation)
            return GpuRoute.OnGpu(label, $"{label}: deep-zoom orbit perturbation"   // #607
                + (LastFrameUsedGpuWavefront ? " + wavefront" : ""));   // #1384
        if (hp && LastFrameUsedGpuPerturbation && LastFrameUsedGpuFloatExp)   // #1331
            return GpuRoute.OnGpu(label, $"{label}: deep-zoom perturbation in fp32"
                + (LastFrameUsedGpuLaFloatExp ? " + LA stages" : "")   // #1664
                + (LastFrameUsedGpuWavefront ? " + wavefront" : "")   // #1384
                + " (24-bit mantissa: a few pixels near deep minibrots can differ; FF_GPU_PERTURB_PRECISION=double for the double kernel)")
                with { Mode = "fp32" };
        if (hp && LastFrameUsedGpuPerturbation)
            return GpuRoute.OnGpu(label, $"{label}: deep-zoom perturbation"
                + (LastFrameUsedGpuLa ? " + LA stages" : "")   // #1630
                + (LastFrameUsedGpuSeriesApproximation ? " + series approximation" : "")
                + (LastFrameUsedGpuBla ? " + BLA" : "")
                + (LastFrameUsedGpuWavefront ? " + wavefront" : ""));
        if (!UseGpuCompute && !GpuDeepHere && s_gpuPerturbDisabled == null)   // #1665 / #1786
            return GpuRoute.NotRequested;
        if (_gpuSkip is { } skip) return skip;
        if (kernel == null)
            return GpuRoute.Cpu("no GPU kernel", "the active renderer has no GPU compute kernel");
        if (hp)
        {
            if (s_gpuPerturbDisabled is { } off) return off;
            if (!GpuDeepHere)   // #1665 / #1786
                return GpuRoute.Cpu("deep zoom", DeepZoomOffDetail(kernel));   // #1493 / #1787: how to turn it on
            if (!kernel.SupportsPerturbation && !FloatExpHere(kernel))
                return GpuRoute.Cpu("no fp64 kernel", kernel.SupportsPerturbationFloatExp
                    ? $"{label} has no fp64, and FF_GPU_PERTURB_PRECISION=double rules out the fp32 deep-zoom kernel (#1331)"
                    : $"{label} has no deep-zoom perturbation kernel (no fp64)");
            return GpuRoute.Cpu("deep-zoom path", "this deep-zoom render path runs on the CPU only");
        }
        if (!UseGpuCompute)
            return GpuRoute.Cpu("GPU compute off", "shallow zooms render on the CPU unless GPU compute is enabled");
        if (Zoom > MaxGpuZoom)
            return GpuRoute.Cpu("zoom > 1e4", "past the FP32 GPU zoom limit (1e4); deeper zooms render on the CPU");
        if (!Fp32GpuResolvesView)   // #1611
            return GpuRoute.Cpu("pixels below fp32", "pixels this small are below the FP32 GPU kernel's precision at this size; it renders on the CPU");
        return GpuRoute.Cpu("CPU-only path", "this render path runs on the CPU only");
    }

    /// <summary>Estimated deepest zoom (as log₁₀) at which the CURRENT view
    /// centre still resolves detail, set by the last reference-orbit build.
    ///
    /// Perturbation resolves a pixel only while its offset δ (∝ 1/zoom),
    /// amplified by ∏|2·Zₙ| over the reference orbit, reaches O(1). If the
    /// orbit ESCAPES at iteration N the amplification is finite —
    /// Σ log₁₀|2·Zₙ| decades — so zoom beyond ~10^that collapses the whole
    /// viewport to a single escape value (flat frame). A centre that stays
    /// bounded to maxIter has effectively unbounded depth ⇒
    /// <see cref="double.PositiveInfinity"/>. This is a property of the POINT,
    /// not a precision limit: to go deeper the user must recentre on a point
    /// with a longer-lived orbit. The host surfaces it as a "detail limit"
    /// notice so a collapsed deep frame doesn't read as broken navigation.</summary>
    public double MaxUsefulZoomLog10 { get; private set; } = double.PositiveInfinity;

    /// <summary>Iteration count of the last reference orbit (escape index, or
    /// maxIter when the centre stays bounded). Diagnostic — surfaced in the
    /// render-context overlay.</summary>
    public int ReferenceOrbitLength => _refOrbitLen;

    /// <summary>True when the last reference orbit escaped before maxIter (a
    /// finite <see cref="MaxUsefulZoomLog10"/>); false when it stayed bounded.</summary>
    public bool ReferenceOrbitEscaped => _refCachedEscaped;

    /// <summary>
    /// When true the HP path runs the perturbation loop without SA prelude
    /// or BLA skip — used by the benchmark harness to measure raw AVX2/AVX-512
    /// loop cost as a baseline against the accelerated path.
    /// </summary>
    public bool DisableAcceleration { get; set; } = false;

    /// <summary>Wave 2.12 — opt in to GPU QD reference orbit. Default off:
    /// the per-iteration QD chain is sequential, so the win is GPU FP64
    /// throughput + CPU offload, not parallelism, and integrated GPUs
    /// generally lose to CPU here. Toggled by the host (or --gpurefprobe)
    /// once the GPU is confirmed faster on the active hardware. Failure
    /// silently falls back to <see cref="ComputeReferenceOrbitQD"/>.</summary>
    public static bool UseGpuReferenceOrbit { get; set; } = false;

    /// <summary>
    /// Disable SA prelude only (BLA still applies). Use this to isolate
    /// SA-induced visual artefacts at problem zoom levels.
    /// </summary>
    public bool DisableSeriesApproximation { get; set; } = false;

    /// <summary>#1275 — BLA linearisation tolerance ε (FractalParameters.BlaTolerance):
    /// a skip may drop a δ² term up to ε of the linear term. Smaller = fewer skips,
    /// closer to the un-skipped render near deep minibrots; the default 1e-6 is the
    /// fast setting. Non-positive or non-finite values use the default.</summary>
    public double BlaTolerance { get; set; } = BlaTable.DefaultEpsilon;

    /// <summary>
    /// Force the legacy single-precision BLA table (pre-Wave-2.10) instead
    /// of the DD-precision merge. Used to isolate suspected Wave 2.10
    /// regressions at extreme zoom — DD-BLA stores A's Lo limb but the
    /// per-pixel Apply collapses Hi+Lo to double, so the precision win
    /// is theoretical until Apply itself runs in DD math.
    /// Setter invalidates the BLA cache so the next render rebuilds.
    /// </summary>
    private bool _disableDdBla;
    public bool DisableDdBla
    {
        get => _disableDdBla;
        set
        {
            if (_disableDdBla == value) return;
            _disableDdBla = value;
            _blaTable = null;
        }
    }

    /// <summary>
    /// Complex-plane units per pixel for the most recent <see cref="Calculate"/>
    /// invocation.  Set at entry so colour maps (notably distance-estimation
    /// themes) can normalise the raw exterior distance value to pixel units.
    /// </summary>
    public static double LastPixelScale { get; private set; } = 1.0;

    /// <summary>#1783 — <see cref="DistanceBuffer"/> and <see cref="DistancePixelScale"/>
    /// are in units of 2^-DistanceUnitExp2 complex-plane units. 0 (plain complex-plane
    /// units) until the pixel is smaller than 2^-100 (zoom ~1e27); past that, the exponent
    /// that puts the pixel scale in [1, 2). A float distance in complex-plane units went
    /// subnormal past zoom ~1e38 and flushed to 0 past ~1e45, which flattened every
    /// consumer of distance / pixel scale. The colour maps get this unit only if they opt
    /// in (<see cref="IColorMapDistanceUnit"/>); the others, and
    /// <see cref="LastPixelScale"/>, keep complex-plane units, bit for bit as before.</summary>
    public int DistanceUnitExp2 => _distExp2;
    private int _distExp2;
    private double _distPixelScale;   // DistancePixelScale in that unit; 0 before the first frame
    private double _worldPixelScale;  // the complex-plane pixel width of the last frame
    private int _mapDistExp2;         // the frame's map takes buffer values · 2^-this (0: as stored)

    // #1783 — the pixel scale a colour map gets, and the shift from the buffer's unit to
    // the distance it gets: the distance unit if it opts in, complex-plane units if not.
    private (double Scale, int Exp2) MapDistanceUnit(IColorMap map)
    {
        double world = _distPixelScale > 0 ? _worldPixelScale : (3.5 / Math.Max(Width, Height)) / Zoom;
        return _distExp2 != 0 && map is IColorMapDistanceUnit u && u.TakesDistanceUnit
            ? (_distPixelScale, 0) : (world, _distExp2);
    }

    // #1843 — dz/dc into the distance unit: · 2^-DistanceUnitExp2 (a length's unit is
    // 2^-e complex-plane units, so a derivative's is 2^e). O(1 / pixel) there, so |dz/dc|²
    // (the normals, the distance) and the float FinalDr/Di buffers stay finite at any depth.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ToDistanceUnit(ref double dr, ref double di)
    {
        int e = _distExp2;
        if (e != 0) { dr = Math.ScaleB(dr, -e); di = Math.ScaleB(di, -e); }
    }

    // #1843 — the same from a FloatExp derivative, finite past double's range (~1e308),
    // where FloatExp.ToDouble of the plain derivative gives ±∞.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (double Dr, double Di) DistanceUnitOf(FloatExp dr, FloatExp di)
        => (dr.ScaleB(-_distExp2).ToDouble(), di.ScaleB(-_distExp2).ToDouble());

    // #1843 — a buffer dz/dc (distance unit) as a map with shift <paramref name="exp2"/>
    // takes it: complex-plane units (· 2^exp2) unless it opts in (exp2 = 0).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float MapDz(double unitDz, int exp2)
        => exp2 == 0 ? (float)unitDz : (float)Math.ScaleB(unitDz, exp2);

    // #1783 — a buffer distance as a map with shift <paramref name="exp2"/> takes it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float MapDist(float bufferDist, int exp2)
        => exp2 == 0 ? bufferDist : (float)Math.ScaleB((double)bufferDist, -exp2);
    public const int DistanceUnitFromExp2 = -100;

    /// <summary>#1783 — the pixel scale in the distance unit, and the unit's exponent.
    /// <paramref name="pixelScale"/> is the complex-plane pixel width as a double (0 or
    /// subnormal past zoom ~1e308); <paramref name="pxLog10"/> its log10, used there.</summary>
    public static (double Scale, int Exp2) DistanceUnit(double pixelScale, double pxLog10)
    {
        if (pixelScale >= Math.ScaleB(1.0, DistanceUnitFromExp2) || double.IsNaN(pixelScale) || double.IsNaN(pxLog10))
            return (pixelScale, 0);
        if (double.IsNormal(pixelScale))   // exact
        {
            int e = -Math.ILogB(pixelScale);
            return (Math.ScaleB(pixelScale, e), e);
        }
        var fx = FloatExp.FromLog10(pxLog10);
        if (fx.IsZero || fx.E < -1_000_000_000) return (pixelScale, 0);
        return (fx.M, (int)-fx.E);
    }

    // #1783 — the exterior distance estimate |z| ln|z| / |dz/dc| in the distance unit.
    // dz/dc · 2^-e is O(1 / pixel) in that unit, so the quotient stays a normal float; it
    // is scaled before it is squared, since |dz/dc|² overflowed a double past |dz/dc|
    // ~1e154 (zoom ~1e150). At e = 0 it is the plain formula, bit for bit.
    // <paramref name="mapDist"/>: what this frame's colour map gets (complex-plane units
    // unless it opts in), from the same double, so it is exactly the old value.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DeDist(double mag, double dr, double di, out float mapDist)
    {
        ToDistanceUnit(ref dr, ref di);
        return DeDistUnit(mag, dr, di, out mapDist);
    }

    // #1843 — DeDist for a dz/dc already in the distance unit (dz/dc · 2^-e).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DeDistUnit(double mag, double dr, double di, out float mapDist)
    {
        int e = _distExp2;
        double dMag = Math.Sqrt(dr * dr + di * di);
        if (!(dMag > (e == 0 ? 1e-10 : Math.ScaleB(1e-10, -e)))) { mapDist = 0f; return 0f; }
        double q = mag * Math.Log(mag) / dMag;
        mapDist = _mapDistExp2 == 0 ? (float)q : (float)Math.ScaleB(q, -_mapDistExp2);
        return (float)q;
    }

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    /// <summary>Global opacity of the in-set (interior) region, 0..255 (issue
    /// #96). 255 = opaque (legacy, no-op). Below 255, a post-colour pass scales
    /// the alpha byte of every in-set pixel so the present path can composite
    /// the interior over a chosen 2D background. Set from
    /// <c>FractalParameters.InteriorAlpha</c> by the render host.</summary>
    public int InteriorAlpha { get; set; } = 255;

    // ── Output buffers ────────────────────────────────────────────────────────

    private int[] _iterationBuffer = Array.Empty<int>();
    public int[] IterationBuffer { get { _gpuAux.Need(GpuDeferredAux.Iterations); return _iterationBuffer; } private set => _iterationBuffer = value; }
    private float[] _smoothBuffer = Array.Empty<float>();
    public float[] SmoothBuffer { get { _gpuAux.Need(GpuDeferredAux.Smooth); return _smoothBuffer; } private set => _smoothBuffer = value; }
    private float[] _distanceBuffer = Array.Empty<float>();
    public float[] DistanceBuffer { get { _gpuAux.Need(GpuDeferredAux.Distance); return _distanceBuffer; } private set => _distanceBuffer = value; }

    /// <summary>#1029 — complex-plane width of one pixel (the same scale the DE
    /// themes use), for the Distance relief height source.</summary>
    public double DistancePixelScale => _distPixelScale > 0 ? _distPixelScale : (3.5 / Math.Max(Width, Height)) / Zoom;   // #1783: in the distance unit

    /// <summary>#1029 — the distance estimate |z|·log|z| / |dz| from the final z / dz
    /// buffers (0 for in-set pixels). The GPU-palette path colours on the GPU and skips
    /// the CPU writeback, but the Distance relief height source still needs this, so
    /// it is filled from the kernel's final z / dz (always read back).</summary>
    private void FillDistanceFromFinalZ(int maxIt, CancellationToken ct)
    {
        _po.CancellationToken = ct;
        FillDistanceFromFinalZ(maxIt, Width, Height, _po, _gpuPassMask != null ? _mask : null);
    }

    // #1332 — on the backing arrays: the deferred fill runs inside
    // GpuDeferredAux.Materialize (after it read the iterations and final z / dz),
    // where the getters would re-enter it. #1394 — <paramref name="mask"/>: only its
    // marked pixels (a solid-region pass; the others are another pass's).
    private void FillDistanceFromFinalZ(int maxIt, int w, int h, ParallelOptions po, byte[]? mask)
    {
        int[] iter = _iterationBuffer;
        float[] dist = _distanceBuffer;
        float[] zrB = _finalZrBuffer, ziB = _finalZiBuffer, drB = _finalDrBuffer, diB = _finalDiBuffer;
        ParallelForRows(0, h, po, y =>
        {
            int rb = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rb + x;
                if (mask != null && mask[idx] == 0) continue;
                if (iter[idx] >= maxIt) { dist[idx] = 0f; continue; }
                float fzr = zrB[idx], fzi = ziB[idx];
                float fdr = drB[idx], fdi = diB[idx];
                double mag = Math.Sqrt(fzr * fzr + fzi * fzi);
                dist[idx] = DeDist(mag, fdr, fdi, out _);
            }
        });
    }

    // #1332 — the GPU-coloured frame's iteration / smooth / final z / distance,
    // read back when a CPU reader first asks (see GpuDeferredAux).
    private readonly GpuDeferredAux _gpuAux = new();

    /// <summary>#1332 — the deferred outputs not yet read back (GpuAuxOutputs bits,
    /// plus 8 for the distance buffer); 0 when everything is on the CPU. For diagnostics
    /// and tests.</summary>
    public int GpuAuxPending => _gpuAux.Pending;

    /// <summary>#1332 — for a deferred GPU frame, whether any pixel escaped, counted on
    /// the GPU (<see cref="IterationRescue.IsAllInSet"/> uses it instead of reading the
    /// iterations back); null when this frame wasn't counted. Reset every frame.</summary>
    public bool? GpuFrameAnyEscaped { get; private set; }

    /// <summary>#1332 — a GPU SP dispatch into this calculator's buffers: deferred
    /// (the iteration / smooth / final-z stay on the GPU, distance filled on demand
    /// when <paramref name="fillDistance"/>) when the kernel and the switch allow,
    /// else the eager <see cref="IGpuKernel.Run"/>. Returns true when it deferred.</summary>
    private bool RunGpuSp(FracturingFog.Rendering.IGpuKernel kernel, double scale, int maxIt, double bailout2, int[]? perRow,
        FracturingFog.Rendering.FractalKind kind, float p0, float p1, uint[]? colorDst, float[]? trapDst,
        FracturingFog.Rendering.GpuDomainWarp warp, bool fillDistance)
    {
        // This dispatch overwrites every deferred buffer, so a pending frame is moot.
        _gpuAux.Discard();
        // #1394 — a solid-region pass (kernel mask set) reads its marked pixels back now.
        if (_gpuPassMask != null) _gpuPassMasked = true;
        if (colorDst != null && kernel.SupportsDeferredReadback && FracturingFog.Rendering.GpuDeferredReadback.Enabled
            && _gpuPassMask == null)
        {
            var pending = kernel.RunDeferred(Width, Height, CenterX, CenterY, scale, maxIt, bailout2,
                _iterationBuffer, _smoothBuffer, _finalZrBuffer, _finalZiBuffer, _finalDrBuffer, _finalDiBuffer,
                perRow, kind, p0, p1, colorDst, trapDst, warp);
            if (pending != FracturingFog.Rendering.GpuAuxOutputs.None)
            {
                int w = Width, h = Height;
                Action? fill = null;
                if (fillDistance)
                {
                    var po = new ParallelOptions { MaxDegreeOfParallelism = _po.MaxDegreeOfParallelism };
                    fill = () => FillDistanceFromFinalZ(maxIt, w, h, po, null);
                }
                _gpuAux.Defer(kernel, _iterationBuffer, pending, fill);
                GpuFrameAnyEscaped = kernel.LastDeferredAnyEscaped;
                return true;
            }
            return false;
        }
        kernel.Run(Width, Height, CenterX, CenterY, scale, maxIt, bailout2,
            _iterationBuffer, _smoothBuffer, _finalZrBuffer, _finalZiBuffer, _finalDrBuffer, _finalDiBuffer,
            perRow, kind, p0, p1, colorDst, trapDst, warp);
        return false;
    }

    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Orbit-aware auxiliary buffers — populated only when an
    // IOrbitAwareColorMap is active.  Zero-filled otherwise.
    public float[] TrapBuffer { get; private set; } = Array.Empty<float>();
    public float[] StripeBuffer { get; private set; } = Array.Empty<float>();
    public float[] TiaBuffer { get; private set; } = Array.Empty<float>();

    // Final-state buffers — z and dz/dc at escape.  Populated on every
    // Calculate() so the recolor / histogram paths can pass them to the
    // 9-parameter Map() overload used by final-state-aware themes
    // (binary decomp, angle decomp, potential, field lines, domain coloring,
    // derivative bailout).  0 for in-set pixels.
    private float[] _finalZrBuffer = Array.Empty<float>();
    public float[] FinalZrBuffer { get { _gpuAux.Need(GpuDeferredAux.FinalZ); return _finalZrBuffer; } private set => _finalZrBuffer = value; }
    private float[] _finalZiBuffer = Array.Empty<float>();
    public float[] FinalZiBuffer { get { _gpuAux.Need(GpuDeferredAux.FinalZ); return _finalZiBuffer; } private set => _finalZiBuffer = value; }
    private float[] _finalDrBuffer = Array.Empty<float>();
    public float[] FinalDrBuffer { get { _gpuAux.Need(GpuDeferredAux.FinalZ); return _finalDrBuffer; } private set => _finalDrBuffer = value; }
    private float[] _finalDiBuffer = Array.Empty<float>();
    public float[] FinalDiBuffer { get { _gpuAux.Need(GpuDeferredAux.FinalZ); return _finalDiBuffer; } private set => _finalDiBuffer = value; }

    // Interior-cycle buffers — populated only when an IInteriorAwareColorMap
    // is active.  Phase 4 infrastructure (Atom Domains, Argument, Multiplier,
    // Cycle Period, Fake DE themes consume these).  Zero-filled otherwise.
    public int[] InteriorPeriodBuffer { get; private set; } = Array.Empty<int>();
    public float[] AttractorZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] AttractorZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] MultiplierMagBuffer { get; private set; } = Array.Empty<float>();

    // ── Constants ─────────────────────────────────────────────────────────────

    private const double EscapeRadius = 512.0;
    private const double EscapeRadius2 = EscapeRadius * EscapeRadius;
    private static readonly int VecLen = Vector<double>.Count;  // SIMD width (SP path)

    // ── In-set early-out helpers ──────────────────────────────────────────────
    // Two cheap closed-form tests + a per-pixel periodicity check that together
    // collapse the cost of "large black region" pixels from O(MaxIter) to
    // O(detection delay). Fidelity is preserved bit-exact: in-set pixels still
    // end with iter == MaxIter and route through FillAuxAndColor's in-set
    // branch, producing the same InSetColor. The exterior path is untouched.
    //
    // Main cardioid:  q(q + (x − 1/4)) ≤ y²/4    where q = (x − 1/4)² + y²
    // Period-2 bulb:  (x + 1)² + y² ≤ 1/16
    //
    // Both regions are mathematically guaranteed in-set; loop would always run
    // to MaxIter for these points. Test cost ≈ 5 mul + 4 add per pixel — net
    // win whenever a non-trivial number of pixels would otherwise iterate past
    // ~10. Only fires for shallow zooms still showing the parent set, so the
    // overhead at deep zoom is negligible (returns false on the first compare).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInMainCardioidOrBulb(double cx, double cy)
    {
        double bx = cx + 1.0;
        if (bx * bx + cy * cy <= 0.0625) return true;
        double xm = cx - 0.25;
        double q = xm * xm + cy * cy;
        return q * (q + xm) <= 0.25 * cy * cy;
    }

    // Snapshot interval for the scalar periodicity check. Once a pixel reaches
    // its attracting cycle, z snaps to bit-exact-equal values within a few
    // hundred iterations (doubles round onto the attractor). A 16-iter snapshot
    // gives detection latency ≤ 32 iterations for any cycle of period ≤ 16,
    // and the snapshot interval doubles up to 1024 to cover longer cycles —
    // standard Brent-style schedule.
    private const int PeriodicitySnapshotStart = 16;
    private const int PeriodicitySnapshotMax = 1024;

    /// <summary>#1368 — the deep orbit path's periodicity early-out (an exact repeat of
    /// the perturbation state (δ, m)). On by default; off for tests that compare against
    /// the loop without it. Process-wide (the pixels run on pool threads).</summary>
    public static bool DeepOrbitPeriodicity = true;

    /// <summary>#1800 / #1802 — the main deep path's periodicity early-out
    /// (PeriodicAtWrap). On by default (FF_DEEP_PERIODICITY=0 turns it off); off for tests
    /// that compare against the loop without it. Process-wide (the rows run on pool threads).
    /// <para>PeriodicAtWrap: with a periodic reference (#1614) the rows wrap at the
    /// period, resetting the reference index m to 0, so every cycle of the loop's state
    /// passes through a wrap. The state is compared there only: once per period, and
    /// Brent snapshots at power-of-two step counts would never line up with, say, a
    /// period-3 cycle once m is part of the state. Snapshots are taken at doubling wrap
    /// counts. The next step is a function of (δ, m, the escaped lanes) while BLA's
    /// lookup limit, min(period, m + (maxIter − iter)), is the period: the check runs
    /// only while maxIter − iter ≥ period. In the last period before the cap the limit
    /// shortens the skips, so the loop without the check takes slightly different
    /// (shorter-skip) steps there; a lane escaping in that stretch from a cycle it kept
    /// below the escape radius was not seen on any test view (on == off byte for byte,
    /// S1802).</para></summary>
    public static bool DeepPeriodicity = Environment.GetEnvironmentVariable("FF_DEEP_PERIODICITY") != "0";

    /// <summary>#1802 — test hook: run the 8-wide PT rows (ComputeRowPT8) even without
    /// AVX-512, where Vector512 is emulated (slow, same results), so tests cover them.</summary>
    public static bool ForcePt8Rows;

    /// <summary>#1803 — test hook: the AVX2 rows (ComputeRowPT4) hand every batch to their
    /// fallback before its first step, as a glitch would: the resumed loop (QD band, BLA on),
    /// the 4-lane rebased loop, or the scalar rebased loop / DD twin. So tests drive those
    /// loops over whole frames.</summary>
    public static bool ForceRebasedLoops;

    /// <summary>#1802 — test hook: when set, the rows' periodicity check adds the index of
    /// every pixel it catches (lanes off the frame or the row range included).</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<int>? DeepPeriodicityCaught;

    /// <summary>#1802 — whether a row batch's state at a wrap repeats a snapshot bit for
    /// bit: every lane's δ (real and imaginary; −0 ≠ +0 never matters as both come from
    /// the same arithmetic, NaN never matches) and the same escaped lanes.</summary>
    public static bool SameBatchState(Vector256<double> dr, Vector256<double> di, int esc,
        Vector256<double> snapDr, Vector256<double> snapDi, int snapEsc)
        => esc == snapEsc
           && Vector256.EqualsAll(dr, snapDr) && Vector256.EqualsAll(di, snapDi);

    /// <summary>#1802 — <see cref="SameBatchState(Vector256{double},Vector256{double},int,Vector256{double},Vector256{double},int)"/>
    /// for the 8-wide rows.</summary>
    public static bool SameBatchState(Vector512<double> dr, Vector512<double> di, int esc,
        Vector512<double> snapDr, Vector512<double> snapDi, int snapEsc)
        => esc == snapEsc
           && Vector512.EqualsAll(dr, snapDr) && Vector512.EqualsAll(di, snapDi);

    /// <summary>#1803 — whether the state right after a rebase (δ := z, m = 0) repeats a
    /// snapshot bit for bit (real and imaginary δ; NaN never matches).</summary>
    public static bool SameRebaseState(double dr, double di, double snapR, double snapI)
        => dr == snapR && di == snapI;

    /// <summary>#1803 — <see cref="SameRebaseState(double,double,double,double)"/> on the DD
    /// twin's full double-double δ (both limbs of both parts).</summary>
    public static bool SameRebaseState(DD dr, DD di, DD snapR, DD snapI)
        => dr.Hi == snapR.Hi && dr.Lo == snapR.Lo && di.Hi == snapI.Hi && di.Lo == snapI.Lo;

    private static void LogCaught(int firstIdx, int lanes, int width)
    {
        var log = DeepPeriodicityCaught;
        if (log == null) return;
        for (int k = 0; k < width; k++) if ((lanes >> k & 1) != 0) log.Enqueue(firstIdx + k);
    }

    /// <summary>#1368 — test hook: a pixel the deep orbit path's periodicity check caught
    /// writes smooth = −(the iteration it was caught at), as the GPU's
    /// GpuPeriodicity.Diagnostic does.</summary>
    public static bool DeepOrbitPeriodicityDiagnostic;

    private int _deepPeriodicPixels;

    /// <summary>#1368 — pixels of the last frame the deep periodicity checks classified
    /// in set: the orbit path's and (#1802) the main path's rows. CPU only; reset at each
    /// Calculate.</summary>
    public int LastDeepPeriodicPixels => Volatile.Read(ref _deepPeriodicPixels);

    // ── Perturbation theory reference orbit ───────────────────────────────────

    private double[] _refZr = Array.Empty<double>();
    private double[] _refZi = Array.Empty<double>();
    // Low limbs of the reference orbit. Populated by ComputeReferenceOrbit (DD:
    // Lo only, X2/X3 = 0) and ComputeReferenceOrbitQD (all three). Consumed by
    // the high-precision per-pixel SA seed in ComputePixelHP / ComputePixelQD
    // to avoid the precision loss that otherwise paints the image-centre as a
    // single colour at extreme zoom (SIMD PT path keeps using only _refZr/_refZi
    // — perturbation by design tracks Z at double precision).
    private double[] _refZrLo = Array.Empty<double>();
    private double[] _refZiLo = Array.Empty<double>();
    private double[] _refZrX2 = Array.Empty<double>();
    private double[] _refZiX2 = Array.Empty<double>();
    private double[] _refZrX3 = Array.Empty<double>();
    private double[] _refZiX3 = Array.Empty<double>();
    // Wave 2.11 — OD limbs 4..7 of the reference orbit. Populated only by
    // ComputeReferenceOrbitOD (Zoom > 1e50). DD/QD paths leave these zero.
    private double[] _refZrX4 = Array.Empty<double>();
    private double[] _refZiX4 = Array.Empty<double>();
    private double[] _refZrX5 = Array.Empty<double>();
    private double[] _refZiX5 = Array.Empty<double>();
    private double[] _refZrX6 = Array.Empty<double>();
    private double[] _refZiX6 = Array.Empty<double>();
    private double[] _refZrX7 = Array.Empty<double>();
    private double[] _refZiX7 = Array.Empty<double>();
    private int _refOrbitLen;
    // Bumped on every reference-orbit rebuild. BLA/SA cache derived
    // coefficients from a specific orbit snapshot; recentre that keeps
    // orbit length identical would otherwise reuse stale coefficients
    // (visible as geometric distortion after small double-click recentres).
    private int _refOrbitGen;

    // Reference-orbit cache: zoom-only / theme-only / pan-stable redraws skip
    // recomputation entirely. Key = (center limbs, maxIter at compute time).
    private double _refCxHi = double.NaN, _refCxLo, _refCx2, _refCx3;
    private double _refCyHi, _refCyLo, _refCy2, _refCy3;
    // Wave 2.11 — OD center limbs 4..7 of the cached reference orbit.
    // Both QD and DD paths leave these zero; centerSame in those paths
    // must also verify these are zero to avoid a stale OD orbit reuse.
    private double _refCx4, _refCx5, _refCx6, _refCx7;
    private double _refCy4, _refCy5, _refCy6, _refCy7;
    private int _refCachedMaxIter = -1;
    private bool _refCachedEscaped;  // true when orbit terminated by escape

    // #1614 — periodic reference orbit. When the centre sits on a period-p nucleus
    // the orbit returns to |Z_p| below the frame's pixel size and then repeats, so
    // the CPU builders stop at Z_{p+1} (_refOrbitLen = p + 1) instead of running to
    // maxIter: 30x fewer reference iterations on a period-121,984 minibrot at 1.7e66
    // with a 3.66M cap. The rebased loops rebase at m + 1 >= refLen, i.e. at m = p,
    // where z = Z_p + δ ≈ δ is as small as δ: the same exact step (δ := z, Z_0 = 0)
    // as any rebase (Zhuoran 2021, see Docs/Resources-Bibliography.md; whether this
    // one-period use is published is still being traced there). 0 = not periodic
    // (escaped, the cap reached, detection off, or a frame the GPU deep path may take).
    private int _refPeriod;
    private double _refPeriodZ2;     // |Z_p|² of that orbit
    private double _refPeriodTol2;   // the tolerance the next build uses ((pixel size)²; 0 = off)
    // #1854 (epic #1428, C5f) — the same tolerance as a FloatExp where (pixel size)²
    // underflows double (past ~1e154): Zero = not needed (the double test stands) or off.
    // The AP builder and PeriodicOrbitStillValid compare |Z_p|² against it in FloatExp.
    private FloatExp _refPeriodTolFx;
    private FloatExp _refPeriodZ2Fx;   // |Z_p|² of the cached orbit, in FloatExp

    /// <summary>#1614 — the reference orbit's period (0 when it is not periodic).
    /// Diagnostic / probe use.</summary>
    public int ReferencePeriod => _refPeriod;

    /// <summary>#1854 — the reference orbit's generation (bumped by every build and every
    /// invalidation): unchanged across two frames = the cached orbit was reused. Diagnostic.</summary>
    public int ReferenceOrbitGeneration => _refOrbitGen;

    /// <summary>#1614 — detect a periodic reference orbit and stop it at the period
    /// (default on; FF_PERIODIC_REFERENCE=0 turns it off). Off: every reference runs to
    /// the iteration cap, as before.</summary>
    public static bool PeriodicReferenceOrbit { get; set; } =
        Environment.GetEnvironmentVariable("FF_PERIODIC_REFERENCE") != "0";

    // A cached periodic orbit serves any iteration cap...
    private bool PeriodicOrbitFits() => _refPeriod > 0;
    // ...while its |Z_p| is still below this frame's tolerance (a deeper frame has
    // smaller pixels; detection off wants the full orbit).
    private bool PeriodicOrbitStillValid() => _refPeriod == 0 || _refPeriodZ2 < _refPeriodTol2
        || (!_refPeriodTolFx.IsZero && FloatExp.AbsLess(_refPeriodZ2Fx, _refPeriodTolFx));   // #1854

    // #1854 — the period tolerance (pixel size)² from the log zoom, as a FloatExp, where the
    // double square underflows (pixel < 1e-150); Zero where the double tolerance holds it.
    private FloatExp PeriodTolFx(bool on)
    {
        double pxLog10 = Math.Log10(3.5 / Math.Max(EffectiveImageWidth, EffectiveImageHeight)) - ZoomLog10;
        return on && pxLog10 < -150 ? FloatExp.FromLog10(2 * pxLog10) : FloatExp.Zero;
    }

    // Wave 3.5 — reference-orbit recycling across frames. When the view centre
    // moves only slightly (pan/zoom video, interactive nudges), the cached
    // orbit at the PREVIOUS centre stays valid for the new frame provided every
    // new-frame pixel's |δ_0| = |pixelOffset·scale + Δc| stays inside the cached
    // BLA validity radius. Δc = (newCentre − cachedCentre) is injected into the
    // SIMD PT dc; the DD/QD/OD glitch fallbacks already derive
    // δc = absoluteWorldCoord − storedRefCentre, so they stay correct with no
    // change. Off by default — enabling shifts per-frame pixels within
    // perturbation tolerance (measured near-identical by --reforbitrecycle),
    // pending video-flicker sign-off before it drives production renders.
    public static bool AllowRefOrbitRecycle { get; set; } = false;

    /// <summary>SM-11b — per-render opt-in for reference-orbit recycling, set by
    /// the host on the progressive pan/zoom PREVIEW calculators so a drag reuses
    /// one reference across preview frames (no per-move recompute → no deep-zoom
    /// "jumping"). Instance flag (not the global static) so it applies only to
    /// the sidecar preview calcs; the committed full-res calc keeps it false and
    /// renders a fresh, exact reference. See Docs/Deep-Zoom-Perturbation.md §6.</summary>
    public bool AllowRecycleThisRender { get; set; }

    /// <summary>#1427 — the anti-aliasing samples (MSAA, offline and live TAA) reuse the
    /// reference orbit of the unjittered centre: each sub-pixel jitter is a recycle shift
    /// (Δc added to every pixel's dc, CPU and GPU) instead of a fresh orbit at the
    /// jittered centre. Up to 15 MSAA + 31 TAA orbit builds per settled deep view were
    /// spent on the same orbit. FF_AA_REUSE_ORBIT=0 rebuilds per sample (before #1427).</summary>
    public static bool AaReuseReferenceOrbit { get; set; } = Environment.GetEnvironmentVariable("FF_AA_REUSE_ORBIT") != "0";
    // Per-frame centre shift from the cached reference centre, in world units,
    // rounded to double (the SIMD PT dc is double regardless of tier). Zero on
    // any non-recycled frame — `x + 0.0 == x`, so the default path is
    // bit-identical to pre-3.5.
    private double _refRecycleDx, _refRecycleDy;
    // Precision tier the cached orbit was built at (0=DD 1=QD 2=OD; -1 none).
    // Recycling refuses to cross tiers — a tier change alters the centre limbs
    // and the fallback precision, so a fresh orbit is required.
    private int _refCachedTier = -1;
    // Diagnostics — reset per Calculate is not needed; these accumulate for
    // the --reforbitrecycle probe and status reporting.
    private long _refRecycleHits, _refRecycleMisses;
    /// <summary>Wave 3.5 — count of frames served by a recycled reference orbit
    /// (centre moved but the cached orbit was reused). Diagnostic / probe use.</summary>
    public long RefRecycleHits => _refRecycleHits;
    /// <summary>Wave 3.5 — count of recycle attempts that fell through to a
    /// fresh orbit compute. Diagnostic / probe use.</summary>
    public long RefRecycleMisses => _refRecycleMisses;

    // SM-2 — perturbation rebasing (Zhuoran). The double-precision PT δ-loop
    // (ComputePixelPT and the SIMD PT4/PT8 lanes) bails as a "glitch" the moment
    // δ is absorbed by Z in double (|δ| < ULP(Z)), which past ~1e30 is iteration
    // ~1 for every pixel — so deep frames fall to the per-pixel direct-QD/OD
    // path (4-limb math, no shared reference orbit) and run for minutes.
    // Rebasing keeps a single reference orbit valid to arbitrary depth: track
    // the reference index m separately, reconstruct z = Z[m] + δ, and when the
    // full value is smaller than the perturbation (|z| < |δ|) or the reference
    // is exhausted, restart the reference (Z[0]=0) with δ := z. The δ-chain
    // stays in double (fast) and never needs the glitch fallback. Off by default
    // pending A/B parity + perf sign-off against the QD/OD truth via
    // --rebaseprobe; when on it replaces the QD/OD/HP glitch fallback with the
    // rebased scalar path. Default ON (2026-07-05) — probe-verified equal to the
    // QD render at ~100× the speed; the "Bypass Rebasing" debug toggle flips it
    // off to A/B against the legacy per-pixel QD/OD path.
    public static bool AllowPtRebasing { get; set; } = true;

    // SM-11a — use a DOUBLE-DOUBLE reference orbit in the rebased δ-loop instead
    // of the single-double Z_n. The full reference orbit is already stored to 8
    // limbs (_refZr/_refZrLo/..X7); the rebased loop historically consumed only
    // the top limb (_refZr[m] = X0), so two nearby view centres' double-rounded
    // Z chains diverged over thousands of iterations. That made the deep-zoom
    // render REFERENCE-DEPENDENT: recentring (any pan / focus / box-zoom) redrew
    // the same world region with slightly different iteration counts, which the
    // palette amplified into apparent navigation drift + live pan "jumping" —
    // even though the input math places the centre exactly (proved 9.5e-15 px at
    // 4.65e64 via --navrepro). Feeding DD Z_n (Hi=_refZr, Lo=_refZrLo) halves the
    // per-step reference rounding (~1e-16 → ~1e-32), restoring centre-invariance.
    // A/B via --navrepro ddref (target: focus-err → 0 on the user's 4.65e64
    // coord). Gate to deep zoom once verified — DD in the hot loop is ~4-8× the
    // double cost, wasted where the single-double reference already suffices.
    // See Docs/Deep-Zoom-Perturbation.md for the full model and history.
    public static bool UseDdRebaseReference { get; set; } = false;

    // SM-11a diagnostic only — force the scalar PT path (bypass SIMD) so every
    // pixel routes through ComputePixelPTRebased. Slow; used by --navrepro to A/B
    // the DD reference across ALL pixels, not just the ~2 % glitch fallback.
    public static bool ForceScalarPtPath { get; set; } = false;

    // Diagnostics — pixels resolved by the rebased scalar fallback.
    private long _ptRebasedPixels;
    /// <summary>SM-2 — count of pixels resolved by the rebased PT fallback
    /// (instead of per-pixel QD/OD/HP). Diagnostic / probe use.</summary>
    public long PtRebasedPixels => _ptRebasedPixels;

    // BLA (Bilinear Approximation) cache — skip thousands of perturbation
    // iterations per pixel when |δ| stays inside the validity radius. Built
    // lazily after reference orbit is ready, invalidated on orbit change
    // or significant dcMaxAbs drift.
    private BlaTable? _blaTable;
    private double _blaDcMaxAbs;
    private int _blaForRefMaxIter = -1;
    private int _blaForRefOrbitLen = -1;
    private int _blaForRefOrbitGen = -1;

    // #1625 (epic #1623) — the LA stage table (FFMath.LaStageTable) over the same
    // reference, keyed on the orbit generation and the tolerance (its thresholds do
    // not depend on the δc radius). Built only when BuildLaStages is on until the
    // evaluator (S3, #1626) reads it.
    private LaStageTable? _laTable;
    private int _laForRefOrbitGen = -1;

    /// <summary>#1625 — build the LA stages with the reference orbit on the CPU deep
    /// path (off by default; FF_LA_STAGES=1). Nothing renders from them yet: S3 (#1626)
    /// adds the evaluator.</summary>
    public bool BuildLaStages { get; set; } = Environment.GetEnvironmentVariable("FF_LA_STAGES") == "1";

    /// <summary>#1625 — the LA stage table of the last deep frame (null when not built).
    /// Diagnostic / test use.</summary>
    public LaStageTable? LaTable => _laTable;

    /// <summary>#1613 — the BLA tolerance ε ceiling in the QD band (1e25..1e50) when
    /// its rows resume rebased pixels. Before #1613 the absorption test sent nearly
    /// every QD-band pixel to the rebased loop without BLA, so those frames were as
    /// accurate as no BLA. Resuming with BLA at the default 1e-6 left ~2.5 % of a
    /// period-448 minibrot's pixels a few iterations off no-BLA rebasing (which matched
    /// exact iteration on every sampled pixel); 1e-10 leaves ~0.2 %, at 4.5x the speed
    /// of no BLA. A tighter BlaTolerance still wins. #1791: the GPU SA / BLA kernel
    /// rebases with BLA too, so it takes the same ceiling in the same band (it kept 1e-6
    /// before and was off ~10x as often as the CPU rows past 1e27).</summary>
    public const double QdBandBlaTolerance = 1e-10;

    // #1613 — the QD-band rows use the rebase test and resume (ComputeRowPT4 / PT8).
    private bool ResumesInQdBand =>
        ResumeRebased && AllowPtRebasing && !UseDdRebaseReference && InQdBand;

    // #1791 — the GPU SA / BLA kernel (CSPerturbSA) always rebases with BLA.
    private bool InQdBand => Zoom > QDZoomThreshold && Zoom <= ODZoomThreshold;

    /// <summary>#1791 — ε of the BLA table the last deep frame built or kept (0 when
    /// none). Diagnostic / test use.</summary>
    public double LastBlaEpsilon => _blaTable?.Epsilon ?? 0;

    /// <summary>#1614 — wall time of the last CPU deep (perturbation) frame's stages:
    /// the reference orbit, the BLA table, the series approximation and the pixel
    /// rows. A stage the frame skipped (a cached orbit or table) reads near 0.
    /// Diagnostic / probe use.</summary>
    public readonly record struct HpStageMs(double RefOrbit, double Bla, double Sa, double Pixels);

    /// <summary>#1614 — see <see cref="HpStageMs"/>.</summary>
    public HpStageMs LastHpStageMs { get; private set; }

    private static double Ms(long t0, long t1) => (t1 - t0) * 1000.0 / Stopwatch.Frequency;

    // Diagnostic counters — reset per Calculate, totalled after Parallel.For
    private long _blaSkipsTotal;
    private long _ptStepsTotal;   // #1614 — plain vector steps of the PT4 / PT8 rows

    /// <summary>#1614 — the last CPU deep frame's PT4 / PT8 row work: BLA skips,
    /// iterations those skips saved, and plain (non-BLA) vector steps (each a step of
    /// 4 or 8 lanes). Diagnostic / probe use.</summary>
    public (long BlaSkips, long BlaIterSaved, long PlainSteps) LastPtWork
        => (_blaSkipsTotal, _blaIterSkippedTotal, _ptStepsTotal);
    private long _blaIterSkippedTotal;

    // Series approximation prelude — third-order polynomial in dc, used to
    // skip the early perturbation iterations from z=0 where BLA validity
    // radius is tiny (Z near origin). SA picks up the loose slack BLA leaves.
    private SeriesApproximation? _sa;
    private int _saForRefOrbitLen = -1;
    private int _saForRefMaxIter = -1;
    private int _saForRefOrbitGen = -1;
    // Tolerance for truncation bound. 1e-3 is the classical Zhuoran / KF
    // default. Tested visually stable when paired with BLA Epsilon=1e-6.
    // (Was preemptively tightened to 1e-9 during banding investigation;
    // root cause turned out to be BLA's Epsilon at 1e-4, not SA tolerance.)
    private const double SaTolerance = 1e-3;
    private long _saAppliedTotal;
    private long _saIterSkippedTotal;
    private bool _loggedSimdPath;

    // Cached ParallelOptions reused across every Parallel.For inside a
    // single Calculate. Cheaper than `new ParallelOptions { ct }` per row
    // band (one alloc per band × 6 paths = 6 allocs per Calculate). The
    // calculator is single-instance per host and Calculate is serialised
    // by the host's _calcLock, so swapping CancellationToken in-place is
    // safe.
    private readonly ParallelOptions _po = new();

    // T2.5: chunked range partitioner. Default `Parallel.For(0, h, body)`
    // schedules one work item per row, so a Calculate over `h` rows pays
    // `h` enqueue/dispatch overheads. With Partitioner.Create(0, h, chunk)
    // each worker grabs a contiguous block of rows in a single dispatch,
    // collapsing scheduling cost to `procCount * 4` work items regardless
    // of height. Largest win at low maxIter / small frames where the
    // scheduling overhead dominated the actual row work.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RowChunk(int count)
    {
        int chunk = count / (Environment.ProcessorCount * 4);
        return chunk < 1 ? 1 : chunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParallelForRows(int from, int to, ParallelOptions po, Action<int> body)
    {
        int count = to - from;
        if (count <= 0) return;
        Parallel.ForEach(Partitioner.Create(from, to, RowChunk(count)), po, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
                body(y);
        });
    }

    // ── Constructor / resize ──────────────────────────────────────────────────

    public MandelbrotCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");
        _gpuAux.Discard();   // #1332 — a deferred frame belongs to the old arrays
        Width = width;
        Height = height;
        int n = width * height;
        // Pinned LOH allocation: these buffers are large (8 MB+ at 1080p),
        // long-lived (resize is rare), and consumed by GPU upload / native
        // memcpy. Pinned avoids the GCHandle.Alloc/Free pair every frame
        // the upload path used to need and removes the buffers from the GC
        // mark-and-compact scan. Alignment is 8 byte on the LOH which is
        // sufficient for our Vector256/AVX2 SIMD writes.
        IterationBuffer = GC.AllocateUninitializedArray<int>(n, pinned: true);
        SmoothBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        DistanceBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalXBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalYBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        ColorBuffer = GC.AllocateUninitializedArray<uint>(n, pinned: true);
        TrapBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        StripeBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        TiaBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalZrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalZiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        InteriorPeriodBuffer = GC.AllocateUninitializedArray<int>(n, pinned: true);
        AttractorZrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        AttractorZiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        MultiplierMagBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public entry point — HIGH IMPACT 2: devirtualized dispatch
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills all output buffers.  Dispatches to a concrete-type generic
    /// overload so the JIT can devirtualize and inline IColorMap.Map().
    /// </summary>
    public void Calculate(CancellationToken ct = default)
    {
        // #615 Phase 1 — the dispatch below early-returns per render path
        // (orbit-aware, GPU palette, DD/QD/OD deep, …). Run the render, then a
        // single path-agnostic post-pass paints the beyond-escape-radius
        // surround, so every path is covered from one place.
        LastFrameUsedGpuCompute = false;   // set true only after a successful GpuKernel.Run (SP path)
        _lastFrameUsedLa = false; _lastFrameUsedLaSimd = false;   // #1629: set by the LA rows only
        GpuFrameAnyEscaped = null;         // #1332: set by a counted deferred GPU frame
        // #607 — the GPU perturbation flags are also set by the deep orbit path, which
        // bypasses CalculateHighPrecision's reset, so clear them for every frame.
        LastFrameUsedGpuPerturbation = false;
        LastFrameUsedGpuSeriesApproximation = false;
        LastFrameUsedGpuBla = false;
        LastFrameUsedGpuOrbitPerturbation = false;
        LastFrameUsedGpuFloatExp = false;          // #1331
        LastFrameUsedGpuWavefront = false;         // #1340
        LastFrameRanWavefrontSeed = false;         // #1386
        LastFrameBuiltGpuReferenceOrbit = false;   // #1166
        _gpuSkip = null;
        _panState = null;                  // #1330: set again by a complete lattice frame
        _panFrameComplete = false;
        LastFrameComputedPixels = 0;
        LastFrameFilledPixels = 0;   // #1343
        Volatile.Write(ref _deepPeriodicPixels, 0);   // #1368
        LastFrameSolidPasses = 0;
        LastFilledMask = null;
        PrepareMask();   // #1344
        if (SolidRegionsApply()) CalculateSolidRegions(ct);   // #1343 opt-in
        else CalculateInternal(ct);
        ApplyOutOfBoundsSurround(ct);
        LastGpuRoute = ResolveGpuRoute();   // #1173-M
        // #1330 / #1344 — a masked frame's buffers are partly another frame's: never a pan source.
        if (_panFrameComplete && _mask == null && !ct.IsCancellationRequested) _panState = CapturePanState();
    }

    private bool _panFrameComplete;   // #1330: this frame was a complete lattice deep CPU frame

    // ── #1344 — pixel mask (adaptive anti-aliasing) ─────────────────────────
    //
    // A supersample pass only needs the pixels whose neighbourhood changes.
    // With a mask set, the CPU iteration loops skip unmarked pixels: a scalar
    // loop pixel by pixel, a SIMD batch only when none of its lanes is marked (a
    // batch couples its lanes' SA skip, BLA steps and glitch decision, so a
    // marked pixel's result is exactly what a full frame computes), and a row with
    // no marked pixel altogether. Unmarked pixels keep whatever their buffers
    // held. The GPU paths compute every pixel, as without a mask.

    /// <summary>#1344 — when non-null (Width × Height bytes), the CPU paths of the
    /// next <see cref="Calculate"/> compute only the pixels whose byte is non-zero;
    /// the others keep their buffer contents. A marked pixel gets exactly the bytes
    /// a frame without the mask gives it. Ignored when its length doesn't match.</summary>
    public byte[]? PixelMask { get; set; }

    private byte[]? _mask;      // PixelMask for this frame (null = every pixel)
    private bool[]? _maskRows;  // rows with at least one marked pixel
    private byte[]? _maskDone;  // #1394 — solid-region passes: pixels a batch wrote
    private byte[]? _batchCount; // #1394 — diagnostics: times a batch computed each pixel

    private void PrepareMask()
    {
        var m = PixelMask;
        int w = Width, h = Height;
        if (m == null || m.Length != w * h) { _mask = null; _maskRows = null; return; }
        var rows = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int rb = y * w;
            for (int x = 0; x < w; x++) if (m[rb + x] != 0) { rows[y] = true; break; }
        }
        _mask = m;
        _maskRows = rows;
    }

    /// <summary>#1394 — a GPU dispatch of this pass wrote pixel <paramref name="idx"/>: it
    /// took the solid-region pass's mask, or there is none (a #1344 mask alone leaves
    /// the GPU computing every pixel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GpuMarked(int idx) => _gpuPassMask == null || _mask![idx] != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GpuRowMarked(int y) => _gpuPassMask == null || _maskRows![y];

    /// <summary>#1344 — compute pixel <paramref name="idx"/> this frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Marked(int idx) => _mask == null || _mask[idx] != 0;

    /// <summary>#1344 — compute any pixel of row <paramref name="y"/> this frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RowMarked(int y) => _maskRows == null || _maskRows[y];

    /// <summary>#1344 — any lane of the batch at columns [x, x + lanes) that lies in
    /// [xBegin, xEnd) and the frame is marked.</summary>
    private bool BatchMarked(int rowBase, int x, int lanes, int xBegin, int xEnd)
    {
        var m = _mask;
        if (m == null) return true;
        int from = Math.Max(Math.Max(x, xBegin), 0), to = Math.Min(Math.Min(x + lanes, xEnd), Width);
        for (int c = from; c < to; c++) if (m[rowBase + c] != 0) return true;
        return false;
    }

    /// <summary>#1394 — <see cref="BatchMarked"/>; a batch it takes writes every lane in
    /// [xBegin, xEnd), marked or not, so during solid-region passes those lanes are
    /// recorded as computed (their bytes are the full frame's: same batch, same lanes)
    /// and later passes skip them.</summary>
    private bool TakeBatch(int rowBase, int x, int lanes, int xBegin, int xEnd)
    {
        if (!BatchMarked(rowBase, x, lanes, xBegin, xEnd)) return false;
        if (_maskDone is { } d)
        {
            int from = Math.Max(Math.Max(x, xBegin), 0), to = Math.Min(Math.Min(x + lanes, xEnd), Width);
            for (int c = from; c < to; c++) d[rowBase + c] = 1;
            if (_batchCount is { } bc)
                for (int c = from; c < to; c++) bc[rowBase + c]++;
        }
        return true;
    }

    // #615 Phase 1 — paint the flat "out-of-bounds" surround: the large disk
    // seen when zoomed out, where the pixel's plane coordinate already lies
    // outside the escape disk (|c| ≥ EscapeRadius) and the orbit escapes on the
    // first step. Opt-in via IColorMap.OutOfBoundsColor; null ⇒ no-op (byte-
    // identical). Self-gating: zoomed in, |c| ≈ CenterX/Y ≪ EscapeRadius, so the
    // test never fires and the pass is a cheap read-only scan. This calculator
    // applies no view rotation (cx = CenterX + (x−W/2)·scale), so the simple
    // mapping here matches every shallow render path exactly.
    private void ApplyOutOfBoundsSurround(CancellationToken ct)
    {
        if (ColorMap.OutOfBoundsColor is not uint oob) return;
        int w = Width, h = Height;
        if (w <= 0 || h <= 0 || ColorBuffer.Length < w * h) return;
        bool haveNormals = NormalXBuffer.Length >= w * h && NormalYBuffer.Length >= w * h;

        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double r2 = EscapeRadius2;
        double cx0 = CenterX, cy0 = CenterY;

        _po.CancellationToken = ct;
        ParallelForRows(0, h, _po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = cy0 + (y - h * 0.5) * scale;
            int rb = y * w;
            for (int x = 0; x < w; x++)
            {
                double cx = cx0 + (x - w * 0.5) * scale;
                if (cx * cx + cy * cy >= r2)
                {
                    ColorBuffer[rb + x] = oob;
                    if (haveNormals)
                    {
                        NormalXBuffer[rb + x] = 0f;
                        NormalYBuffer[rb + x] = 0f;
                    }
                }
            }
        });
    }

    private void CalculateInternal(CancellationToken ct)
    {
#if DEBUG
        // Stacktrace alloc + reflection. Skipped in Release so the hot
        // video/preview path does not pay for the diagnostic.
        var callingMethod = new StackTrace().GetFrame(1)?.GetMethod();
        Debug.WriteLine($"Calculate() called from {callingMethod?.DeclaringType?.Name}.{callingMethod?.Name}{Environment.NewLine} with ColorMap={ColorMap.GetType().Name}, MaxIterations={MaxIterations}");
#endif
        ColorMap.MaxIterations = MaxIterations;

        // #189 — honour the process-global CPU cap. -1 (default) is the
        // System.Threading unlimited default, so an unthrottled render is
        // byte- and perf-identical; the shell sets a cap only around heavy
        // offscreen poster / wallpaper renders.
        _po.MaxDegreeOfParallelism = FracturingFog.Rendering.RenderThrottle.MaxDegreeOfParallelism;

        // Update pixel scale so DE-style themes can normalise raw distance
        // (complex-plane units) into pixel units for stable rendering at any zoom.
        LastPixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;
        // #1783: the distance buffer's unit, and what this frame's map gets.
        _worldPixelScale = LastPixelScale;
        (_distPixelScale, _distExp2) = DistanceUnit(LastPixelScale,
            Math.Log10(3.5 / Math.Max(Width, Height)) - ZoomLog10);
        (double mapScale, _mapDistExp2) = MapDistanceUnit(ColorMap);
        if (ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = mapScale;

        // #1173-M — orbit-aware themes run on the scalar CPU path (only the opt-in
        // GPU orbit kernel below can take them; it sets LastFrameUsedGpuCompute).
        if (ColorMap is IOrbitAwareColorMap)
            _gpuSkip = GpuRoute.Cpu("orbit theme", "orbit-aware themes (traps, stripe, TIA, ...) colour on the CPU unless the GPU orbit kernel is enabled for them");

        // ── Orbit-aware dispatch ─────────────────────────────────────────────
        // Orbit traps, stripe average and triangle-inequality average themes
        // need per-iteration z samples and run on a dedicated scalar SP path.
        // Each concrete orbit theme is enumerated so the generic dispatch
        // CalculateOrbitAware<TMap> JIT-specialises and inlines Sample().
        switch (ColorMap)
        {
            case OrbitTrapPointMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCrossMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCircleMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLineMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapStarMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPickoverStalksMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapBiomorphMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapImageRainbowMap m: CalculateOrbitAware(m, ct); return;
            // Additional orbit-trap shapes (OrbitTrapExtraThemes.cs)
            case OrbitTrapSquareMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapRingMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHyperbolaMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLemniscateMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCardioidMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapDiagonalCrossMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapTriangleMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHexagonMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHeartMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSineWaveMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapConcentricMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapGridMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPinwheelMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPolarRoseMap m: CalculateOrbitAware(m, ct); return;
            // 3D-lit orbit-trap variants (OrbitTrap3DThemes.cs)
            case OrbitTrapPointPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCrossPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCirclePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLinePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapStarPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSquarePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapRingPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHyperbolaPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLemniscatePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCardioidPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapDiagonalCrossPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapTrianglePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHexagonPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHeartPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSineWavePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapConcentricPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapGridPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPinwheelPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPolarRosePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case StripeAverageClassicMap m: CalculateOrbitAware(m, ct); return;
            case TriangleInequalityMap m: CalculateOrbitAware(m, ct); return;
            case StripeTiaBlendMap m: CalculateOrbitAware(m, ct); return;
            case CurvatureAverageMap m: CalculateOrbitAware(m, ct); return;
            case LyapunovExponentMap m: CalculateOrbitAware(m, ct); return;
            case GaussianIntegerMap m: CalculateOrbitAware(m, ct); return;
            case ExponentialSmoothingMap m: CalculateOrbitAware(m, ct); return;

            // #589 (F13) — data-driven / user orbit-trap themes are not one of
            // the enumerated concrete types, so catch any remaining orbit-aware
            // map by interface. Not devirtualised (one virtual Sample call per
            // iteration), but correctness beats speed for user themes — without
            // this they would fall through to the non-orbit path and render as a
            // plain iteration gradient (Sample never called).
            case IOrbitAwareColorMap m:
                // F16 (#603) — try the GPU orbit kernel first (opt-in + shallow +
                // exterior); otherwise the scalar CPU orbit path.
                if (TryRunGpuOrbit(m, ct)) return;
                CalculateOrbitAware(m, ct);
                return;
        }

        // Pattern-match to the concrete type so the JIT sees a non-virtual call
        // inside Calculate<TMap> and can inline Map() completely.
        // Add new palette types here as the library grows.
        switch (ColorMap)
        {
            case PhongStoneMap m: CalculateCore(m, ct); break;
            case MoltenMetalMap m: CalculateCore(m, ct); break;
            case CrystalCaveMap m: CalculateCore(m, ct); break;
            case GoldReliefMap m: CalculateCore(m, ct); break;
            case MarbleReliefMap m: CalculateCore(m, ct); break;
            case VolcanicRockMap m: CalculateCore(m, ct); break;
            case LunarSurfaceMap m: CalculateCore(m, ct); break;
            case AncientBronzeMap m: CalculateCore(m, ct); break;
            case NeonReliefMap m: CalculateCore(m, ct); break;
            case PolarNight3DMap m: CalculateCore(m, ct); break;
            case Inferno3DMap m: CalculateCore(m, ct); break;
            case Blackbody3DMap m: CalculateCore(m, ct); break;
            case CosmicLatte3DMap m: CalculateCore(m, ct); break;
            case Aurora3DMap m: CalculateCore(m, ct); break;
            case DeepSpaceBlue3DMap m: CalculateCore(m, ct); break;
            case EarthTone3DMap m: CalculateCore(m, ct); break;
            case Icefire3DMap m: CalculateCore(m, ct); break;
            case LavaLamp3DMap m: CalculateCore(m, ct); break;
            case Plasma3DMap m: CalculateCore(m, ct); break;
            case Purplebody3DMap m: CalculateCore(m, ct); break;
            case TriColor3DMap m: CalculateCore(m, ct); break;
            case Tropical3DMap m: CalculateCore(m, ct); break;
            case OceanDepth3DMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumPhong3D m: CalculateCore(m, ct); break;
            case WoodGrainPhong3D m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D_Realistic m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D_UltraGlow m: CalculateCore(m, ct); break;
            case RadioInterferencePhong3D m: CalculateCore(m, ct); break;
            case RadioInterferencePbr3D m: CalculateCore(m, ct); break;

            // ── Algorithmic 3D — Phong ────────────────────────────────────────
            case BernsteinPhong3D m: CalculateCore(m, ct); break;
            case CopperSheenPhong3D m: CalculateCore(m, ct); break;
            case DigitalMatrixPhong3D m: CalculateCore(m, ct); break;
            case DistanceGlowPhong3D m: CalculateCore(m, ct); break;
            case FirePhong3D m: CalculateCore(m, ct); break;
            case GoldenRatioPhong3D m: CalculateCore(m, ct); break;
            case GrayscalePhong3D m: CalculateCore(m, ct); break;
            case HsvPhong3D m: CalculateCore(m, ct); break;
            case MonoBandPhong3D m: CalculateCore(m, ct); break;
            case NebulaDustPhong3D m: CalculateCore(m, ct); break;
            case PaintedPhong3D m: CalculateCore(m, ct); break;
            case PaintedReversedPhong3D m: CalculateCore(m, ct); break;
            case PastellyPhong3D m: CalculateCore(m, ct); break;
            case PsychedelicPhong3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalPhong3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalBluePhong3D m: CalculateCore(m, ct); break;
            case RainbowPhong3D m: CalculateCore(m, ct); break;
            case SolarWindPhong3D m: CalculateCore(m, ct); break;
            case SolarWindModPhong3D m: CalculateCore(m, ct); break;
            case TwilightCyclicPhong3D m: CalculateCore(m, ct); break;
            case VintageSepiaPhong3D m: CalculateCore(m, ct); break;
            case WarpedHsvPhong3D m: CalculateCore(m, ct); break;

            // ── Algorithmic 3D — PBR ──────────────────────────────────────────
            case BernsteinPbr3D m: CalculateCore(m, ct); break;
            case CopperSheenPbr3D m: CalculateCore(m, ct); break;
            case DigitalMatrixPbr3D m: CalculateCore(m, ct); break;
            case DistanceGlowPbr3D m: CalculateCore(m, ct); break;
            case FirePbr3D m: CalculateCore(m, ct); break;
            case GoldenRatioPbr3D m: CalculateCore(m, ct); break;
            case GrayscalePbr3D m: CalculateCore(m, ct); break;
            case HsvPbr3D m: CalculateCore(m, ct); break;
            case MonoBandPbr3D m: CalculateCore(m, ct); break;
            case NebulaDustPbr3D m: CalculateCore(m, ct); break;
            case PaintedPbr3D m: CalculateCore(m, ct); break;
            case PaintedReversedPbr3D m: CalculateCore(m, ct); break;
            case PastellyPbr3D m: CalculateCore(m, ct); break;
            case PsychedelicPbr3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalPbr3D m: CalculateCore(m, ct); break;
            case RainbowPbr3D m: CalculateCore(m, ct); break;
            case SolarWindPbr3D m: CalculateCore(m, ct); break;
            case SolarWindModPbr3D m: CalculateCore(m, ct); break;
            case TwilightCyclicPbr3D m: CalculateCore(m, ct); break;
            case VintageSepiaPbr3D m: CalculateCore(m, ct); break;
            case WarpedHsvPbr3D m: CalculateCore(m, ct); break;

            case HsvPalette m: CalculateCore(m, ct); break;
            case Painted m: CalculateCore(m, ct); break;
            case PaintedReversed m: CalculateCore(m, ct); break;
            case Pastelly m: CalculateCore(m, ct); break;
            case WarpedHsvMap m: CalculateCore(m, ct); break;
            case RainbowColorMap m: CalculateCore(m, ct); break;
            case GoldenRatioMap m: CalculateCore(m, ct); break;
            case MonoBandMap m: CalculateCore(m, ct); break;
            case BernsteinMap m: CalculateCore(m, ct); break;
            case RedAndBlack m: CalculateCore(m, ct); break;
            case BlackbodyColorMap m: CalculateCore(m, ct); break;
            case PurplebodyColorMap m: CalculateCore(m, ct); break;
            case DeepSpaceBlueMap m: CalculateCore(m, ct); break;
            case EarthToneMap m: CalculateCore(m, ct); break;
            case IcefireColorMap m: CalculateCore(m, ct); break;
            case InfernoColorMap m: CalculateCore(m, ct); break;
            case OceanDepthMap m: CalculateCore(m, ct); break;
            case AuroraColorMap m: CalculateCore(m, ct); break;
            case PolarNightMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumGradient m: CalculateCore(m, ct); break;
            case WoodGrainGradient m: CalculateCore(m, ct); break;
            case RadioInterferenceGradient m: CalculateCore(m, ct); break;
            case FirePalette m: CalculateCore(m, ct); break;
            case CosmicLatteMap m: CalculateCore(m, ct); break;
            case TropicalMap m: CalculateCore(m, ct); break;
            case LavaLampMap m: CalculateCore(m, ct); break;
            case TriColorMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumCycling m: CalculateCore(m, ct); break;
            case WoodGrainCycling m: CalculateCore(m, ct); break;
            case RadioInterferenceCycling m: CalculateCore(m, ct); break;
            case NebulaDustMap m: CalculateCore(m, ct); break;
            case DigitalMatrixMap m: CalculateCore(m, ct); break;
            case PsychedelicMap m: CalculateCore(m, ct); break;
            case TwilightCyclicMap m: CalculateCore(m, ct); break;
            case SolarWindMap m: CalculateCore(m, ct); break;
            case SolarWindMapMOD m: CalculateCore(m, ct); break;
            case CopperSheenMap m: CalculateCore(m, ct); break;
            case VintageSepiaMap m: CalculateCore(m, ct); break;
            case GrayscalePalette m: CalculateCore(m, ct); break;
            case ViridisColorMap m: CalculateCore(m, ct); break;
            case PlasmaColorMap m: CalculateCore(m, ct); break;
            case DistanceFieldChromaticMap m: CalculateCore(m, ct); break;
            case DistanceFieldGlowMap m: CalculateCore(m, ct); break;
            case DistanceFieldSilverMap m: CalculateCore(m, ct); break;
            case LambertShadingMap m: CalculateCore(m, ct); break;
            case SlopeShadingMap m: CalculateCore(m, ct); break;
            case EmbossBumpMap m: CalculateCore(m, ct); break;
            case AmbientOcclusionMap m: CalculateCore(m, ct); break;
            case SoftShadowMap m: CalculateCore(m, ct); break;
            case CyclePeriodMap m: CalculateCore(m, ct); break;
            case MultiplierMap m: CalculateCore(m, ct); break;
            case AtomDomainsMap m: CalculateCore(m, ct); break;
            case InteriorArgumentMap m: CalculateCore(m, ct); break;
            case FakeDistanceEstimateMap m: CalculateCore(m, ct); break;
            default:
                // Unknown concrete type — fall back to virtual dispatch.
                // Still correct; just not devirtualized.
                CalculateCore(ColorMap, ct);
                break;
        }

        // ── Optional interior-aware pass ─────────────────────────────────────
        // Themes that colour the in-set region (Atom Domains, Multiplier,
        // Cycle Period, Fake DE, Argument) implement IInteriorAwareColorMap.
        // For each in-set pixel we run Brent cycle detection on z² + c starting
        // from 0, recover (period, attractor, |multiplier|), and let the theme
        // colour the pixel.  Exterior pixels are untouched.
        if (ColorMap is IInteriorAwareColorMap interiorMap)
            RunInteriorPass(interiorMap, ct);

        // #1343 — a solid-region frame runs these whole-frame passes once, on the
        // finished frame, not after each of its masked passes.
        if (!_deferWholeFramePasses) RunWholeFramePasses(ct);
    }

    private bool _deferWholeFramePasses;   // #1343

    private void RunWholeFramePasses(CancellationToken ct)
    {
        // ── Optional post-process pass ───────────────────────────────────────
        // Themes that need neighbourhood information (emboss, AO, soft shadow)
        // implement IPostProcessColorMap and run a second pass over the
        // finished ColorBuffer + aux float buffers.  No-op for everything else.
        if (ColorMap is IPostProcessColorMap pp)
            pp.PostProcess(ColorBuffer, SmoothBuffer,
                           NormalXBuffer, NormalYBuffer,
                           Width, Height, MaxIterations);

        // ── Global interior alpha (issue #96) ────────────────────────────────
        // Make the in-set region translucent so the present pass can composite
        // it over the selected 2D background. Runs last so it scales whatever
        // alpha the exterior/interior/post passes produced. No-op at 255.
        if (InteriorAlpha < 255)
            StampInteriorAlpha(ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Global interior alpha stamp (issue #96)
    //
    // In-set pixels are those that never escaped: IterationBuffer[idx] >=
    // MaxIterations (the same invariant RunInteriorPass relies on to skip the
    // exterior). We multiply the existing alpha byte by InteriorAlpha/255 so a
    // future per-theme authored interior alpha would compose multiplicatively
    // rather than be clobbered. RGB is left untouched.
    // ─────────────────────────────────────────────────────────────────────────

    // Delegates to the shared InteriorAlphaStamp helper (#97) so every in-scope
    // escape-time family reuses one implementation. Behaviour is unchanged from
    // the original private pass (#96).
    private void StampInteriorAlpha(CancellationToken ct) =>
        InteriorAlphaStamp.Apply(
            ColorBuffer, IterationBuffer, Width, Height, MaxIterations, InteriorAlpha, _po, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 4 — Interior orbit / attracting-cycle detection
    //
    // For each in-set pixel we want to know:
    //   • period p of the attracting cycle (1, 2, 3, …)
    //   • a point on the cycle (the attractor sample)
    //   • the cycle multiplier |λ| = |∏_{k=0}^{p−1} 2 z_k|, which classifies
    //     the bulb (hyperbolic if < 1, parabolic if = 1).
    //
    // Algorithm — Brent's cycle detection on z_{n+1} = z_n² + c:
    //   1. SETTLE: iterate from z=0 for `Settle` steps so we land near the
    //      attractor (transient dies out).  Settle = MaxIterations gives the
    //      cleanest result but is expensive; Settle = min(MaxIter, 2000) is
    //      a good compromise.
    //   2. BRENT: search for a cycle.  Tortoise is fixed, hare advances; when
    //      the hare lands within ε of the tortoise we have a cycle of length
    //      lam.  Power-of-two snapshots make this O(p) memory-free.
    //   3. MULTIPLIER: walk the detected cycle once and accumulate the complex
    //      product ∏ 2 z_k.
    //
    // Cost: every in-set pixel pays roughly (Settle + 2·MaxPeriod) extra
    // iterations.  Only invoked when a theme actually consumes the data.
    // ─────────────────────────────────────────────────────────────────────────

    private void RunInteriorPass(IInteriorAwareColorMap interiorMap, CancellationToken ct)
    {
        int maxIt = MaxIterations;
        if (maxIt <= 0) return;

        int w = Width, h = Height;
        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double cx0 = CenterX, cy0 = CenterY;

        // Settle budget — long enough to reach the attractor for high-period
        // bulbs.  Capped so deep-zoom renders don't pay an unbounded cost.
        int settle = Math.Min(maxIt, 2000);
        // Period search budget — covers all visible secondary / tertiary bulbs.
        const int maxPeriod = 1024;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = cy0 + (y - h * 0.5) * scale;
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                if (IterationBuffer[idx] < maxIt) continue;     // exterior pixel
                if (!Marked(idx)) continue;                      // #1344

                double cx = cx0 + (x - w * 0.5) * scale;
                DetectCycle(cx, cy, settle, maxPeriod,
                            out int period, out double aZr, out double aZi, out double multMag);

                InteriorPeriodBuffer[idx] = period;
                AttractorZrBuffer[idx] = (float)aZr;
                AttractorZiBuffer[idx] = (float)aZi;
                MultiplierMagBuffer[idx] = (float)multMag;

                ColorBuffer[idx] = (uint)interiorMap.MapInterior(
                    period, (float)aZr, (float)aZi, (float)multMag, cx, cy);
            }
        });
    }

    /// <summary>
    /// Brent cycle detection on the Mandelbrot iteration z² + c starting from
    /// z = 0.  Returns the detected cycle period, a point on the cycle, and
    /// |∏ 2 z_k| over one period.  Period = 0 if no cycle found within budget.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DetectCycle(
        double cx, double cy, int settle, int maxPeriod,
        out int period, out double attractorZr, out double attractorZi, out double multMag)
    {
        // ── Step 1: settle near the attractor ───────────────────────────────
        double zr = 0.0, zi = 0.0;
        for (int i = 0; i < settle; i++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 > 4.0) { period = 0; attractorZr = 0; attractorZi = 0; multMag = 0; return; }
            double nzr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = nzr;
        }

        // ── Step 2: Brent's algorithm — find cycle length `lam` ─────────────
        // Tortoise snapshot resets at power-of-two boundaries; hare always
        // advances.  Cycle detected when |hare − tortoise|² < eps².  The
        // tolerance scales with attractor magnitude so deeply nested bulbs
        // (small attractors) still detect cleanly.
        double tZr = zr, tZi = zi;
        double hZr = zr, hZi = zi;
        int power = 1, lam = 0;
        double eps2Base = 1e-12;
        // Search budget: enough hops to find any period ≤ maxPeriod.
        int budget = maxPeriod * 4 + 16;

        for (int step = 0; step < budget; step++)
        {
            if (power == lam)
            {
                tZr = hZr; tZi = hZi;
                power *= 2;
                lam = 0;
            }
            double hzr2 = hZr * hZr, hzi2 = hZi * hZi;
            if (hzr2 + hzi2 > 4.0) { period = 0; attractorZr = 0; attractorZi = 0; multMag = 0; return; }
            double nhr = hzr2 - hzi2 + cx;
            hZi = 2.0 * hZr * hZi + cy;
            hZr = nhr;
            lam++;

            double dx = hZr - tZr, dy = hZi - tZi;
            double dd = dx * dx + dy * dy;
            // Scale tolerance by attractor radius² (with floor) so we don't
            // miss tight cycles near origin.
            double scale2 = Math.Max(1.0, hzr2 + hzi2);
            if (dd < eps2Base * scale2 && lam <= maxPeriod)
            {
                period = lam;
                attractorZr = hZr;
                attractorZi = hZi;

                // ── Step 3: cycle multiplier λ = ∏_{k=0}^{p−1} 2 z_k ────────
                // Walk one full period; accumulate the complex product.
                double mr = 1.0, mi = 0.0;
                double pr = hZr, pi = hZi;
                for (int k = 0; k < period; k++)
                {
                    // m *= 2 p
                    double twoPr = 2.0 * pr, twoPi = 2.0 * pi;
                    double nmr = mr * twoPr - mi * twoPi;
                    double nmi = mr * twoPi + mi * twoPr;
                    mr = nmr; mi = nmi;
                    // p = p² + c
                    double p2r = pr * pr - pi * pi + cx;
                    pi = 2.0 * pr * pi + cy;
                    pr = p2r;
                }
                multMag = Math.Sqrt(mr * mr + mi * mi);
                return;
            }
        }

        period = 0;
        attractorZr = 0; attractorZi = 0; multMag = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generic core — TMap is resolved at JIT time → no virtual call per pixel
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateCore<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        bool useHP = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = useHP;

        if (useHP)
        {
            CalculateHighPrecision(colorMap, ct);
            LastPrecisionLabel = PerturbationLabel();
        }
        else
        {
            LastPrecisionLabel = "SP";
            CalculateDoublePrecision(colorMap, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH A — Standard double + SIMD (unchanged algorithm, inline color)
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateDoublePrecision<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        // Per-tile maxIter (PerTile mode). Captured once so the per-row
        // body never touches a property that could be re-assigned mid-frame.
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;

        // T3.1 GPU compute dispatch.
        //   • Only when explicitly toggled on (UseGpuCompute) AND a kernel
        //     is attached (host sets it if the renderer is D3D11).
        //   • Zoom-gated: above MaxGpuZoom the FP32 split-centre math
        //     hits ULP cancellation → pixelation + wrong bulb-skip
        //     predicates. CPU path handles those zooms cleanly.
        //   • Phase 1.b: PerRowMaxIter is plumbed into the shader as an SRV;
        //     the kernel reads gPerRow[y] when UsePerRow is set, applies the
        //     same in-set rewrite (row-capped unescaped → write gMaxIter).
        //     Orbit-aware themes are still CPU-only — they need per-step z
        //     samples the shader doesn't produce; dispatched via the
        //     CalculateOrbitAware top-level switch.
        if (UseGpuCompute && GpuKernel != null && Fp32GpuResolvesView)   // #1611: zoom + pixel size
        {
            try
            {
                // T3.1 phase 2/4 — pick GPU palette path when the active
                // colour map ships an IGpuHlslPalette impl. (Julia / Burning
                // Ship / Tricorn take the same path through
                // EscapeTimeCalculator.TryDispatchGpu.) Falls back
                // automatically when SetPalette failed at compile time.
                var hlslPalette = colorMap as FracturingFog.Interefaces.IGpuHlslPalette;
                if (hlslPalette != null) GpuKernel.SetPalette(hlslPalette);
                else GpuKernel.SetPalette(null);
                bool gpuPalette = hlslPalette != null && GpuKernel.HasGpuPalette;

                // #1332 — a GPU-coloured frame defers its iter / smooth / final z
                // (and the distance fill) until a CPU reader asks.
                bool deferred = RunGpuSp(GpuKernel, scale, maxIt, EscapeRadius2,
                    useTileCap ? perRow : null, FracturingFog.Rendering.FractalKind.Mandelbrot, 0f, 0f,
                    gpuPalette ? ColorBuffer : null, null, default, fillDistance: true);
                LastFrameUsedGpuCompute = true;

                if (gpuPalette)
                {
                    // GPU produced the colour buffer end-to-end. Normals stay
                    // zeroed — themes that need them aren't IGpuHlslPalette
                    // eligible. #1029: the distance estimate is still filled
                    // (the Distance relief height source reads it).
                    if (!deferred) FillDistanceFromFinalZ(maxIt, ct);
                    // #1394 — the buffers are allocated uninitialised, so the normals of a
                    // GPU-coloured frame are whatever the memory held: a solid-region pass
                    // zeroes them, so a tile's border compares (and the fill copies) defined values.
                    if (_gpuPassMask != null) { Array.Clear(NormalXBuffer); Array.Clear(NormalYBuffer); }
                    return;
                }
                // Emit ColorBuffer from the GPU's iter + smooth + final z/dz
                // outputs. FillAuxAndColorSP would re-derive smooth from z
                // (which it would clobber to whatever's already in
                // SmoothBuffer's adjacent slots — wrong); inline a GPU-aware
                // writeback that consumes the kernel's smooth + final state
                // directly. Distance + normal come from the standard
                // post-iteration formulae using the GPU's final z+dz.
                _po.CancellationToken = ct;
                var poFill = _po;
                bool handlesInSet = colorMap is IColorMapHandlesInSet;
                uint inSetColor = colorMap.InSetColor;
                ParallelForRows(0, Height, poFill, y =>
                {
                    if (ct.IsCancellationRequested || !GpuRowMarked(y)) return;
                    int rb = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        int idx = rb + x;
                        if (!GpuMarked(idx)) continue;   // #1394: another pass's pixel
                        int iters = IterationBuffer[idx];
                        if (iters < maxIt)
                        {
                            float smooth = SmoothBuffer[idx];
                            float fzr = FinalZrBuffer[idx];
                            float fzi = FinalZiBuffer[idx];
                            float fdr = FinalDrBuffer[idx];
                            float fdi = FinalDiBuffer[idx];
                            // Distance estimate: |z| * log(|z|) / |dz|
                            double mag = Math.Sqrt(fzr * fzr + fzi * fzi);
                            float dist = DeDist(mag, fdr, fdi, out float mapDist);
                            DistanceBuffer[idx] = dist;
                            // Normal via FillNormal helper (writes NormalX/Y
                            // from z + dz like the CPU path).
                            FillNormal(idx, fzr, fzi, fdr, fdi);
                            int iterArg = handlesInSet ? iters : maxIt;
                            ColorBuffer[idx] = (uint)colorMap.Map(
                                smooth, mapDist, iterArg,
                                NormalXBuffer[idx], NormalYBuffer[idx],
                                fzr, fzi, fdr, fdi);
                        }
                        else
                        {
                            SmoothBuffer[idx] = 0f;
                            DistanceBuffer[idx] = 0f;
                            NormalXBuffer[idx] = 0f;
                            NormalYBuffer[idx] = 0f;
                            FinalZrBuffer[idx] = 0f;
                            FinalZiBuffer[idx] = 0f;
                            FinalDrBuffer[idx] = 0f;
                            FinalDiBuffer[idx] = 0f;
                            ColorBuffer[idx] = handlesInSet
                                ? (uint)colorMap.Map(0f, 0f, maxIt, 0f, 0f, 0f, 0f, 0f, 0f)
                                : inSetColor;
                        }
                    }
                });
                return;
            }
            catch (Exception ex)
            {
                // Kernel failure (device lost, shader bug, OOM) — fall
                // through to the CPU path so the user still gets a frame.
                Debug.WriteLine($"[MandelbrotCalculator] GPU dispatch failed, falling back to CPU: {ex.Message}");
                _gpuSkip = GpuRoute.Cpu("GPU error", $"GPU dispatch failed: {ex.Message}");   // #1173-M
            }
        }

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            if (!RowMarked(y)) return;   // #1344
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy = CenterY + (y - Height * 0.5) * scale;
            ComputeRowSP(cy, CenterX, scale, rowMaxIt, y * Width, colorMap);
            // Phase 2.1 in-set rewrite. Pixels that exhausted the row cap
            // without escape land in IterationBuffer at rowMaxIt; the
            // recolor's in-set gate (`iters >= maxIter`) tests against the
            // global cap, so a row-cap pixel would be miscolored as
            // "escaped at rowMaxIt". Rewriting to maxIt restores the in-set
            // classification — semantically correct for unescaped pixels.
            if (rowMaxIt < maxIt)
            {
                int rb = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    if (IterationBuffer[rb + x] >= rowMaxIt)
                        IterationBuffer[rb + x] = maxIt;
                }
            }
        });
    }

    private void ComputeRowSP<TMap>(
        double cy, double centerX, double scale,
        int maxIter, int rowBase, TMap colorMap)
        where TMap : IColorMap
    {
        var escRad2V = new Vector<double>(EscapeRadius2);
        var twoV = new Vector<double>(2.0);
        var oneV = Vector<double>.One;
        var zeroV = Vector<double>.Zero;
        var cyV = new Vector<double>(cy);

        Span<double> cxBuf = stackalloc double[VecLen];
        Span<double> iterCntBuf = stackalloc double[VecLen];

        // Hoisted out of column loop to avoid CA2014 stack growth.
        // Only used when VecLen == 4 and vMap != null, but cheap to allocate
        // unconditionally — single 16 B span × 9 = 144 B once per row.
        Span<float> sm   = stackalloc float[4];
        Span<float> ds   = stackalloc float[4];
        Span<float> nxs  = stackalloc float[4];
        Span<float> nys  = stackalloc float[4];
        Span<float> fzrS = stackalloc float[4];
        Span<float> fziS = stackalloc float[4];
        Span<float> fdrS = stackalloc float[4];
        Span<float> fdiS = stackalloc float[4];
        Span<int>   colors = stackalloc int[4];

        int x = 0;

        // ── Vectorized lanes ──────────────────────────────────────────────────
        for (; x + VecLen <= Width; x += VecLen)
        {
            if (_mask != null && !TakeBatch(rowBase, x, VecLen, 0, Width)) continue;   // #1344 / #1394
            for (int k = 0; k < VecLen; k++)
                cxBuf[k] = centerX + ((x + k) - Width * 0.5) * scale;
            var cx = new Vector<double>(cxBuf);

            // Whole-block cardioid/bulb skip — fires often on shallow-zoom
            // video frames sweeping past the parent set. Per-lane test on the
            // four/eight x values; if every lane is in-set, we can write the
            // result directly without entering the iteration loop.
            int bulbBits = 0;
            for (int k = 0; k < VecLen; k++)
                if (IsInMainCardioidOrBulb(cxBuf[k], cy)) bulbBits |= 1 << k;
            int allLanesMask = (1 << VecLen) - 1;
            if (bulbBits == allLanesMask)
            {
                for (int k = 0; k < VecLen; k++)
                {
                    int idx = rowBase + x + k;
                    IterationBuffer[idx] = maxIter;
                    FillAuxAndColorSP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
                }
                continue;
            }

            var zr = zeroV; var zi = zeroV;
            var dr = oneV; var di = zeroV;
            var iterCountV = zeroV;

            // Block-level periodicity. Snapshot z every snapInterval iters;
            // when every lane equals its snapshot, no lane is escaping and no
            // lane is still on a transient orbit — escaped lanes are frozen
            // by ConditionalSelect (so they always self-match after one
            // interval), and in-set lanes have reached their attracting
            // cycle. Promote the unescaped lanes to maxIter and exit early.
            var zrSnap = zeroV;
            var ziSnap = zeroV;
            int snapInterval = PeriodicitySnapshotStart;
            int snapCounter = 0;

            for (int iter = 0; iter < maxIter; iter++)
            {
                var zr2 = zr * zr;
                var zi2 = zi * zi;
                var mag2 = zr2 + zi2;
                var notEscaped = Vector.LessThan(mag2, escRad2V);

                iterCountV += Vector.ConditionalSelect(notEscaped, oneV, zeroV);

                var newDr = twoV * (zr * dr - zi * di) + oneV;
                var newDi = twoV * (zr * di + zi * dr);
                dr = Vector.ConditionalSelect(notEscaped, newDr, dr);
                di = Vector.ConditionalSelect(notEscaped, newDi, di);

                var newZr = zr2 - zi2 + cx;
                var newZi = twoV * zr * zi + cyV;
                zr = Vector.ConditionalSelect(notEscaped, newZr, zr);
                zi = Vector.ConditionalSelect(notEscaped, newZi, zi);

                // Per-iter escape check: break as soon as every lane has
                // escaped. Cheaper than every-8 gating because branch predictor
                // handles the steady "still iterating" path, and we save up to
                // 7 iterations per block when all lanes diverge quickly.
                if (!Vector.LessThanAny(mag2, escRad2V)) break;

                if (++snapCounter >= snapInterval)
                {
                    if (Vector.EqualsAll(zr, zrSnap) && Vector.EqualsAll(zi, ziSnap))
                    {
                        // All lanes either frozen-escaped or on cycle. Promote
                        // any lane whose mag² is still below escape radius
                        // (i.e., never escaped) to maxIter so the post-loop
                        // FillAux routes it through the in-set branch.
                        iterCountV.CopyTo(iterCntBuf);
                        for (int k = 0; k < VecLen; k++)
                        {
                            double m2 = zr[k] * zr[k] + zi[k] * zi[k];
                            if (m2 < EscapeRadius2) iterCntBuf[k] = maxIter;
                        }
                        iterCountV = new Vector<double>(iterCntBuf);
                        break;
                    }
                    zrSnap = zr; ziSnap = zi;
                    snapCounter = 0;
                    if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
                }
            }

            // SIMD-batched color path: when the active theme implements
            // IVectorColorMap and VecLen == 4 (AVX2 double-lane width), gather
            // the four lanes' aux outputs into Vector128<float> packs and call
            // MapV once instead of four scalar Map() calls. Cast is constant-
            // folded by JIT generic specialisation when TMap is a concrete
            // type known not to implement the interface.
            var vMap = colorMap as IVectorColorMap;
            if (vMap != null && VecLen == 4)
            {
                // Scratch spans hoisted above loop (see CA2014 fix).
                int inSetBits = 0;

                for (int k = 0; k < 4; k++)
                {
                    int idx = rowBase + x + k;
                    int iters = (int)iterCountV[k];
                    IterationBuffer[idx] = iters;
                    FillAuxOnlySP(idx, iters, maxIter,
                        zr[k], zi[k], dr[k], di[k],
                        out sm[k], out ds[k], out nxs[k], out nys[k],
                        out fzrS[k], out fziS[k], out fdrS[k], out fdiS[k]);
                    if (iters >= maxIter) inSetBits |= 1 << k;
                }

                var smV  = Vector128.Create(sm[0],   sm[1],   sm[2],   sm[3]);
                var dsV  = Vector128.Create(ds[0],   ds[1],   ds[2],   ds[3]);
                var nxV  = Vector128.Create(nxs[0],  nxs[1],  nxs[2],  nxs[3]);
                var nyV  = Vector128.Create(nys[0],  nys[1],  nys[2],  nys[3]);
                var fzrV = Vector128.Create(fzrS[0], fzrS[1], fzrS[2], fzrS[3]);
                var fziV = Vector128.Create(fziS[0], fziS[1], fziS[2], fziS[3]);
                var fdrV = Vector128.Create(fdrS[0], fdrS[1], fdrS[2], fdrS[3]);
                var fdiV = Vector128.Create(fdiS[0], fdiS[1], fdiS[2], fdiS[3]);
                var colorV = vMap.MapV(smV, dsV, maxIter, nxV, nyV, fzrV, fziV, fdrV, fdiV);

                colorV.CopyTo(colors);
                uint inSetColor = colorMap.InSetColor;
                for (int k = 0; k < 4; k++)
                {
                    int idx = rowBase + x + k;
                    ColorBuffer[idx] = ((inSetBits >> k) & 1) != 0 ? inSetColor : (uint)colors[k];
                }
            }
            else
            {
                for (int k = 0; k < VecLen; k++)
                {
                    int idx = rowBase + x + k;
                    int iters = (int)iterCountV[k];
                    IterationBuffer[idx] = iters;
                    // HIGH IMPACT 3: fill aux buffers AND color in one pass
                    FillAuxAndColorSP(idx, iters, maxIter,
                        zr[k], zi[k], dr[k], di[k], colorMap);
                }
            }
        }

        // ── Scalar tail ───────────────────────────────────────────────────────
        for (; x < Width; x++)
        {
            if (!Marked(rowBase + x)) continue;   // #1344
            double cx2 = centerX + (x - Width * 0.5) * scale;
            ComputePixelSP(cx2, cy, maxIter, rowBase + x, colorMap);
        }
    }

    /// <summary>
    /// Aux-only twin of FillAuxAndColorSP — writes per-pixel buffers but
    /// emits the values it computed via out-params so the batched color
    /// stage can gather them into Vector128 packs without an extra read of
    /// SmoothBuffer / NormalXBuffer / etc. Color stage is then a single
    /// MapV() call per 4-pixel block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxOnlySP(
        int idx, int iters, int maxIter,
        double zr, double zi, double dr, double di,
        out float smooth, out float dist,
        out float nx, out float ny,
        out float fzr, out float fzi, out float fdr, out float fdi)
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            DistanceBuffer[idx] = DeDist(mag, dr, di, out dist);   // #1783: dist as the map takes it

            FillNormal(idx, zr, zi, dr, di);
            nx = NormalXBuffer[idx];
            ny = NormalYBuffer[idx];

            fzr = (float)zr; fzi = (float)zi;
            fdr = (float)dr; fdi = (float)di;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;
        }
        else
        {
            smooth = 0f; dist = 0f;
            nx = 0f; ny = 0f;
            fzr = 0f; fzi = 0f; fdr = 0f; fdi = 0f;
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelSP<TMap>(
        double cx, double cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        if (IsInMainCardioidOrBulb(cx, cy))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorSP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        double zr = 0, zi = 0, dr = 1, di = 0;
        double zrSnap = 0, ziSnap = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 >= EscapeRadius2) break;
            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;

            if (zr == zrSnap && zi == ziSnap) { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap = zr; ziSnap = zi;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }
        IterationBuffer[idx] = iter;
        FillAuxAndColorSP(idx, iter, maxIter, zr, zi, dr, di, colorMap);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorSP<TMap>(
        int idx, int iters, int maxIter,
        double zr, double zi, double dr, double di,
        TMap colorMap)
        where TMap : IColorMap
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            // Smooth iteration: iters + 1 - log2(log2(mag)). Math.Log2 is a
            // single hardware-backed call vs 3× Math.Log + 2 divides in the
            // log-ratio formulation. Identical algebraic result.
            float smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            float dist = DeDist(mag, dr, di, out float mapDist);
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);

            float fzr = (float)zr, fzi = (float)zi;
            float fdr = (float)dr, fdi = (float)di;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;

            // HIGH IMPACT 3: color computed HERE, no second pass
            // Themes that implement IColorMapHandlesInSet get the true escape
            // iteration count so their `isInSet` / `iter` inputs are accurate;
            // every other theme keeps the documented maxIter contract.
            int iterArg = colorMap is IColorMapHandlesInSet ? iters : maxIter;
            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, mapDist, iterArg,
                NormalXBuffer[idx], NormalYBuffer[idx],
                fzr, fzi, fdr, fdi);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            if (colorMap is IColorMapHandlesInSet)
            {
                // Route interior through Map() so the theme can colour the
                // inside of the set; iters = maxIter triggers isInSet = 1.0.
                ColorBuffer[idx] = (uint)colorMap.Map(
                    0f, 0f, maxIter,
                    0f, 0f, 0f, 0f, 0f, 0f);
            }
            else
            {
                ColorBuffer[idx] = colorMap.InSetColor; // theme-defined interior, default opaque black
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Normal computation (shared by both paths)
    // ─────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillNormal(int idx, double zr, double zi, double dr, double di)
    {
        double u = zr * dr + zi * di;
        double v = zi * dr - zr * di;
        double m = Math.Sqrt(u * u + v * v);
        if (m > 1e-10)
        {
            NormalXBuffer[idx] = (float)(u / m);
            NormalYBuffer[idx] = (float)(v / m);
        }
        else
        {
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH C — orbit-aware scalar (SP only)
    //
    // Used for orbit traps, stripe average, and triangle-inequality average
    // colour maps that require per-iteration z samples.  No SIMD, no high
    // precision — these themes are not supported at extreme zoom.
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateOrbitAware<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IOrbitAwareColorMap
    {
        bool useHP = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = useHP;

        // #609 — past the direct-double precision floor (~1e12, HPZoomThreshold)
        // the direct z²+c orbit below iterates a wrong/mushy orbit, so the trap /
        // stripe / TIA accumulators sample garbage. Route to the perturbation-orbit
        // path, which reconstructs z = Z[m] + δ from the shared reference orbit each
        // iteration (glitch-free Zhuoran rebasing) and samples the accumulator on
        // that reconstructed full value — mirroring ComputePixelOrbit's convention
        // so the crossover is continuous. Shallow stays on the direct loop (correct
        // and faster there).
        if (useHP)
        {
            CalculateOrbitAwarePerturbation(colorMap, ct);
            LastPrecisionLabel = PerturbationLabel();
            return;
        }
        LastPrecisionLabel = "SP";

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        // Phase 2.1: OrbitAware honours PerRowMaxIter when supplied.
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        // #590 (F14) — theme-driven interior orbit colouring. Read once (the
        // interface member would be a virtual call per pixel otherwise). When on,
        // in-set pixels are coloured via MapInteriorWithOrbit instead of the flat
        // InSetColor (and the bulb early-out is skipped so the accumulator fills).
        bool wantInterior = colorMap.WantsInteriorColor;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy = CenterY + (y - Height * 0.5) * scale;
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (!Marked(rowBase + x)) continue;   // #1344
                double cx = CenterX + (x - Width * 0.5) * scale;
                ComputePixelOrbit(cx, cy, rowMaxIt, rowBase + x, colorMap, wantInterior);
            }
            // Phase 2.1 in-set rewrite (see CalculateDoublePrecision).
            if (rowMaxIt < maxIt)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (IterationBuffer[rowBase + x] >= rowMaxIt)
                        IterationBuffer[rowBase + x] = maxIt;
                }
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelOrbit<TMap>(
        double cx, double cy, int maxIter, int idx, TMap colorMap, bool wantInterior)
        where TMap : IOrbitAwareColorMap
    {
        colorMap.InitOrbit(out var acc);

        // Bulb early-out: pixel guaranteed in-set, so the orbit accumulator
        // would be unused (the in-set branch below ignores acc). Skip the loop.
        // #590 (F14) — but when the theme colours the interior FROM the orbit,
        // the accumulator IS used, so we must actually iterate: skip the early-out.
        if (!wantInterior && IsInMainCardioidOrBulb(cx, cy))
        {
            IterationBuffer[idx] = maxIter;
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            TrapBuffer[idx] = 0f;
            StripeBuffer[idx] = 0f;
            TiaBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
            return;
        }

        double zr = 0, zi = 0, dr = 1, di = 0;
        double zrSnap = 0, ziSnap = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;
        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 >= EscapeRadius2) break;

            // Sample BEFORE update so iter==0 is skipped (z_0 = 0 has no arg).
            if (iter > 0) colorMap.Sample(ref acc, zr, zi, cx, cy, iter);

            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;

            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;

            // Periodicity early-out — orbit accumulators are unused for in-set
            // pixels (see in-set branch below), so terminating the iteration
            // here doesn't affect the final color, only the runtime.
            if (zr == zrSnap && zi == ziSnap) { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap = zr; ziSnap = zi;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }
        FinalizeOrbitPixel(idx, iter, maxIter, zr, zi, dr, di, ref acc, colorMap, wantInterior);
    }

    // Shared aux/colour writeback for both orbit paths — the direct
    // ComputePixelOrbit above and the deep-zoom ComputePixelOrbitPerturbation
    // below (#609). Extracted verbatim so the crossover between the two is
    // byte-identical for a fixed (iter, zr, zi, dr, di, acc): zr/zi is the
    // escape (or final) full-value z, dr/di the dz/dc derivative.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinalizeOrbitPixel<TMap>(
        int idx, int iter, int maxIter,
        double zr, double zi, double dr, double di,
        ref OrbitAccumulator acc, TMap colorMap, bool wantInterior)
        where TMap : IOrbitAwareColorMap
    {
        ToDistanceUnit(ref dr, ref di);   // #1843
        FinalizeOrbitPixelUnit(idx, iter, maxIter, zr, zi, dr, di, ref acc, colorMap, wantInterior);
    }

    // #1843 — dr / di in the distance unit (see FillAuxAndColorHPUnit).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinalizeOrbitPixelUnit<TMap>(
        int idx, int iter, int maxIter,
        double zr, double zi, double dr, double di,
        ref OrbitAccumulator acc, TMap colorMap, bool wantInterior)
        where TMap : IOrbitAwareColorMap
    {
        IterationBuffer[idx] = iter;

        if (iter < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            float smooth = (float)(iter + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            float dist = DeDistUnit(mag, dr, di, out float mapDist);
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);

            TrapBuffer[idx] = acc.TrapMin == float.MaxValue ? 0f : acc.TrapMin;
            StripeBuffer[idx] = acc.StripeCount > 0 ? (float)(acc.StripeSum / acc.StripeCount) : 0f;
            TiaBuffer[idx] = acc.TiaCount > 0 ? (float)(acc.TiaSum / acc.TiaCount) : 0f;

            FinalZrBuffer[idx] = (float)zr;
            FinalZiBuffer[idx] = (float)zi;
            FinalDrBuffer[idx] = (float)dr;
            FinalDiBuffer[idx] = (float)di;

            ColorBuffer[idx] = (uint)colorMap.MapWithOrbit(
                smooth, mapDist, maxIter,
                NormalXBuffer[idx], NormalYBuffer[idx], in acc);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            // #590 (F14) — keep the accumulated trap distance for interior pixels
            // when the theme colours the interior; otherwise 0f (byte-identical
            // to the historical flat path, which never populated these for in-set).
            if (wantInterior)
            {
                TrapBuffer[idx] = acc.TrapMin == float.MaxValue ? 0f : acc.TrapMin;
                StripeBuffer[idx] = acc.StripeCount > 0 ? (float)(acc.StripeSum / acc.StripeCount) : 0f;
                TiaBuffer[idx] = acc.TiaCount > 0 ? (float)(acc.TiaSum / acc.TiaCount) : 0f;
            }
            else
            {
                TrapBuffer[idx] = 0f;
                StripeBuffer[idx] = 0f;
                TiaBuffer[idx] = 0f;
            }
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            // #590 (F14) — colour the interior from the orbit when the theme asks
            // (opaque; StampInteriorAlpha applies InteriorAlpha afterwards, so no
            // double-scale here). Otherwise the historical flat interior fill.
            ColorBuffer[idx] = wantInterior
                ? (uint)colorMap.MapInteriorWithOrbit(maxIter, in acc)
                : colorMap.InSetColor;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH C-deep (#609) — orbit-aware PERTURBATION path.
    //
    // Past HPZoomThreshold (~1e12) the direct-double orbit in ComputePixelOrbit
    // loses precision, so its trap/stripe/TIA accumulators sample a wrong orbit.
    // This path builds ONE reference orbit at the view centre (DD/QD/OD centre,
    // stored as double Z[n]) and, per pixel, iterates only the double δ-chain,
    // reconstructing the full value z = Z[m] + δ each step (Zhuoran rebasing,
    // glitch-free at any zoom — the same maths as ComputePixelPTRebased). The
    // accumulator is sampled on that reconstructed z, matching ComputePixelOrbit's
    // convention (pre-update RAW z, iter > 0), so shallow↔deep is continuous.
    //
    // No SA/BLA skipping here: an orbit theme needs EVERY iteration's z, and a
    // skipped range would drop samples. Issue #609 option (a) — correct first cut;
    // perf note: deep orbit renders iterate the full δ-chain per pixel (no skips).
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateOrbitAwarePerturbation<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IOrbitAwareColorMap
    {
        int effImgW = EffectiveImageWidth;
        int effImgH = EffectiveImageHeight;
        double scale = (3.5 / Math.Max(effImgW, effImgH)) / Zoom;
        int maxIt = MaxIterations;

        // Build the shared reference orbit at the view centre (fresh — the orbit
        // path opts out of Wave 3.5 recycle so every pixel sees an exact anchor).
        // #1368 — with a periodic reference (#1614): this loop (CPU and GPU) rebases
        // when the reference runs out (m + 1 >= refLen), the same exact step the main
        // deep path's wrap at the period takes. The orbit path never set the tolerance,
        // so it always built the full orbit, and the reference index m never repeated:
        // the periodicity check below could not see a cycle (0 of 4096 interior pixels
        // at the period-3 nucleus, 1e14).
        _refPeriodTol2 = PeriodicReferenceOrbit ? scale * scale : 0;
        _refPeriodTolFx = PeriodTolFx(PeriodicReferenceOrbit);   // #1854
        if (UseApReference)
        {
            // #1849 — past OD's reach the BigFloat orbit at the exact centre (C4, #1840),
            // as the main path's ComputeReferenceOrbitForCenter.
            int prec = BigFloat.PrecisionForZoom(ZoomLog10);
            ComputeReferenceOrbitAP(CenterExact ?? ExactCentre.FromDeep(CurrentCentreLimbs(), prec), prec, maxIt, ct);
        }
        else if (Zoom > ODZoomThreshold)
        {
            var cxOD = new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                              CenterX4, CenterX5, CenterX6, CenterX7);
            var cyOD = new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                              CenterY4, CenterY5, CenterY6, CenterY7);
            ComputeReferenceOrbitOD(cxOD, cyOD, maxIt, ct);
        }
        else if (Zoom > QDZoomThreshold)
        {
            var cxQD = new QD(CenterX, CenterXLo, CenterX2, CenterX3);
            var cyQD = new QD(CenterY, CenterYLo, CenterY2, CenterY3);
            ComputeReferenceOrbitQD(cxQD, cyQD, maxIt, ct);
        }
        else
        {
            ComputeReferenceOrbit(new DD(CenterX, CenterXLo), new DD(CenterY, CenterYLo), maxIt, ct);
        }

        // #1849 — past double: the floatexp form of the orbit loop (dc, δ underflow).
        _lastFrameUsedFx = UseFloatExpPixels;
        _lastFrameUsedLa = false;
        if (_lastFrameUsedFx) { CalculateOrbitAwareFloatExpRows(colorMap, maxIt, ct); return; }

        // #607 / G4.6 — a GPU orbit palette accumulates on the GPU perturbation
        // kernel (same loop + sampling convention as below); colour stays here.
        if (TryRunGpuOrbitPerturbation(colorMap, scale, maxIt, effImgW, effImgH, ct)) return;

        // dc geometry mirrors ComputeRowPTScalar (sub-rect aware; collapses to the
        // legacy (x - Width*0.5)*scale formula when sub-rect mode is inactive).
        double halfH = effImgH * 0.5;
        double halfW = effImgW * 0.5;
        int offX = SubRectOffsetX;
        int offY = SubRectOffsetY;

        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        bool wantInterior = colorMap.WantsInteriorColor;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double rowOffsetY = offY + y - halfH;
            double dcY = rowOffsetY * scale + PixelJitter.Y * scale;   // #1851 (+ 0.0 without a jitter)
            double cy = CenterY + dcY;
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (!Marked(rowBase + x)) continue;   // #1344
                double dcX = (offX + x - halfW) * scale + PixelJitter.X * scale;
                double cx = CenterX + dcX;
                ComputePixelOrbitPerturbation(dcX, dcY, cx, cy, rowMaxIt, rowBase + x, colorMap, wantInterior);
            }
            // Phase 2.1 in-set rewrite (see CalculateOrbitAware / CalculateDoublePrecision).
            if (rowMaxIt < maxIt)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (IterationBuffer[rowBase + x] >= rowMaxIt)
                        IterationBuffer[rowBase + x] = maxIt;
                }
            }
        });
    }

    // #1849 (epic #1428, C5d) — the orbit-aware rows past double: as above, with dc from
    // the log-zoom pixel scale in FloatExp (CalculateFloatExpRows' geometry).
    private void CalculateOrbitAwareFloatExpRows<TMap>(TMap colorMap, int maxIt, CancellationToken ct)
        where TMap : IOrbitAwareColorMap
    {
        int effImgW = EffectiveImageWidth, effImgH = EffectiveImageHeight;
        var scale = FloatExp.FromLog10(Math.Log10(3.5 / Math.Max(effImgW, effImgH)) - ZoomLog10);
        double halfW = effImgW * 0.5, halfH = effImgH * 0.5;
        int offX = SubRectOffsetX, offY = SubRectOffsetY;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        bool wantInterior = colorMap.WantsInteriorColor;
        _po.CancellationToken = ct;
        ParallelForRows(0, Height, _po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            var dcI = scale * (offY + y - halfH + PixelJitter.Y);   // #1851
            double cy = CenterY + dcI.ToDouble();
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (!Marked(rowBase + x)) continue;
                var dcR = scale * (offX + x - halfW + PixelJitter.X);
                ComputePixelOrbitPerturbationFx(dcR, dcI, CenterX + dcR.ToDouble(), cy, rowMaxIt, rowBase + x, colorMap, wantInterior);
            }
            if (rowMaxIt < maxIt)
                for (int x = 0; x < Width; x++)
                    if (IterationBuffer[rowBase + x] >= rowMaxIt) IterationBuffer[rowBase + x] = maxIt;
        });
    }

    /// <summary>#1849 — <see cref="ComputePixelOrbitPerturbation"/> in FloatExp: δ, dc and the
    /// derivative in FloatExp; the accumulator samples z = Z[m] + δ as a double (z is of
    /// order 1 there), and the c it is given is the double centre plus dc.</summary>
    private void ComputePixelOrbitPerturbationFx<TMap>(
        FloatExp dcR, FloatExp dcI, double cx, double cy,
        int maxIter, int idx, TMap colorMap, bool wantInterior)
        where TMap : IOrbitAwareColorMap
    {
        colorMap.InitOrbit(out var acc);
        int refLen = _refOrbitLen;
        if (refLen < 1)
        {
            FinalizeOrbitPixel(idx, maxIter, maxIter, 0, 0, 1, 0, ref acc, colorMap, wantInterior);
            return;
        }
        FloatExp dr = FloatExp.Zero, di = FloatExp.Zero;
        FloatExp drv = FloatExp.One, div = FloatExp.Zero;
        double zrD = 0, ziD = 0;
        int m = 0;
        FloatExp drSnap = FloatExp.Zero, diSnap = FloatExp.Zero;
        int mSnap = -1;
        int snapInterval = PeriodicitySnapshotStart, snapCounter = 0;
        bool periodicity = DeepOrbitPeriodicity;
        int caughtAt = 0;
        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double Zr = _refZr[m], Zi = _refZi[m];
            FloatExp zr = dr + Zr, zi = di + Zi;
            zrD = zr.ToDouble(); ziD = zi.ToDouble();
            if (zrD * zrD + ziD * ziD >= EscapeRadius2) break;
            if (iter > 0) colorMap.Sample(ref acc, zrD, ziD, cx, cy, iter);

            var nDrv = (zr * drv - zi * div).ScaleB(1) + 1.0;
            var nDiv = (zr * div + zi * drv).ScaleB(1);
            drv = nDrv; div = nDiv;

            if (FloatExp.AbsLess(FloatExp.Norm2(zr, zi), FloatExp.Norm2(dr, di)) || m + 1 >= refLen)
            {
                dr = zr; di = zi;
                Zr = 0; Zi = 0;
                m = 0;
            }
            FloatExp a = dr + 2.0 * Zr, b = di + 2.0 * Zi;
            var nDr = a * dr - b * di + dcR;
            var nDi = a * di + b * dr + dcI;
            dr = nDr; di = nDi;
            m++;

            // #1368 — the (δ, m) periodicity early-out, with exact FloatExp repeats.
            if (periodicity)
            {
                if (m == mSnap && SameFx(dr, drSnap) && SameFx(di, diSnap))
                {
                    Interlocked.Increment(ref _deepPeriodicPixels);
                    caughtAt = iter;
                    iter = maxIter;
                    break;
                }
                if (++snapCounter >= snapInterval)
                {
                    drSnap = dr; diSnap = di; mSnap = m;
                    snapCounter = 0;
                    if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
                }
            }
        }
        var (udr, udi) = DistanceUnitOf(drv, div);   // #1843: finite past ~1e308
        FinalizeOrbitPixelUnit(idx, iter, maxIter, zrD, ziD, udr, udi, ref acc, colorMap, wantInterior);
        if (caughtAt > 0 && DeepOrbitPeriodicityDiagnostic) SmoothBuffer[idx] = -caughtAt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelOrbitPerturbation<TMap>(
        double dcR, double dcI, double cx, double cy,
        int maxIter, int idx, TMap colorMap, bool wantInterior)
        where TMap : IOrbitAwareColorMap
    {
        colorMap.InitOrbit(out var acc);

        // Bulb early-out (mirrors ComputePixelOrbit): guaranteed in-set, orbit
        // accumulator unused unless the theme colours the interior from it.
        if (!wantInterior && IsInMainCardioidOrBulb(cx, cy))
        {
            IterationBuffer[idx] = maxIter;
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            TrapBuffer[idx] = 0f;
            StripeBuffer[idx] = 0f;
            TiaBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
            return;
        }

        int refLen = _refOrbitLen;
        if (refLen < 1)
        {
            // Degenerate reference (interior centre) — colour flat, matching the
            // ComputePixelPTRebased refLen<1 guard.
            FinalizeOrbitPixel(idx, maxIter, maxIter, 0, 0, 1, 0, ref acc, colorMap, wantInterior);
            return;
        }

        // δ-chain in plain double + Zhuoran rebasing (see ComputePixelPTRebased).
        double dr = 0.0, di = 0.0;    // δ_0 = 0
        double drv = 1.0, div = 0.0;  // dz/dc derivative (IQ convention), full orbit
        int m = 0;                    // reference-orbit index (independent of iter)
        double zr = 0.0, zi = 0.0;    // reconstructed full value z = Z[m] + δ
        // #1368 — periodicity snapshot of the perturbation state (δ, m); see the
        // early-out below. mSnap = -1: no snapshot yet (m is never negative).
        double drSnap = 0, diSnap = 0;
        int mSnap = -1;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;
        bool periodicity = DeepOrbitPeriodicity;
        int caughtAt = 0;

        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double Zr = _refZr[m];
            double Zi = _refZi[m];
            zr = Zr + dr;
            zi = Zi + di;

            double zmag2 = zr * zr + zi * zi;
            if (zmag2 >= EscapeRadius2) break;

            // Sample the accumulator on the reconstructed full value z BEFORE the
            // δ/reference advance, iter > 0 (z_0 = 0 has no arg) — identical
            // indexing to ComputePixelOrbit so the crossover is continuous. The
            // sample sees the TRUE orbit z, not the rebased δ (z is captured above,
            // before any rebase below touches dr/di).
            if (iter > 0) colorMap.Sample(ref acc, zr, zi, cx, cy, iter);

            // Derivative of the FULL orbit — independent of rebasing.
            double newDrv = 2.0 * (zr * drv - zi * div) + 1.0;
            double newDiv = 2.0 * (zr * div + zi * drv);
            drv = newDrv; div = newDiv;

            // Rebase when the reference no longer anchors this pixel (full value
            // below the perturbation magnitude) or the reference is exhausted.
            double dmag2 = dr * dr + di * di;
            if (zmag2 < dmag2 || m + 1 >= refLen)
            {
                dr = zr; di = zi;      // δ := z, relative to restarted Z[0]=0
                Zr = 0.0; Zi = 0.0;
                m = 0;
            }

            // δ_{n+1} = (2·Z[m] + δ)·δ + dc  (Z[m]=0 right after a rebase).
            double a = 2.0 * Zr + dr;
            double b = 2.0 * Zi + di;
            double newDr = a * dr - b * di + dcR;
            double newDi = a * di + b * dr + dcI;
            dr = newDr; di = newDi;
            m++;

            // #1368 — periodicity early-out on the loop's whole state, (δ, m). The next
            // step is a function of δ, m and the pixel's constant dc alone (the rebase
            // test, the reference value Z[m], the δ update), so a bit-exact repeat of
            // (δ, m) means this loop would cycle through the same states for ever:
            // it never escapes, and the pixel is classified exactly as without the
            // check. The accumulator and the derivative don't steer the loop. Before
            // #1368 this compared z = Z[m] + δ and δ without m: a repeat at another
            // reference index only approximates periodicity, and z alone flagged whole
            // frames in set at 3e47, where z rounds to Z[m] (#607). A cycle is only
            // seen when m repeats too: with a periodic reference (#1614) the loop
            // wraps at its period, and the Zhuoran rebases (|z| < |δ|) restart it.
            if (periodicity)
            {
                if (dr == drSnap && di == diSnap && m == mSnap)
                {
                    Interlocked.Increment(ref _deepPeriodicPixels);
                    caughtAt = iter;
                    iter = maxIter;
                    break;
                }
                if (++snapCounter >= snapInterval)
                {
                    drSnap = dr; diSnap = di; mSnap = m;
                    snapCounter = 0;
                    if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
                }
            }
        }

        FinalizeOrbitPixel(idx, iter, maxIter, zr, zi, drv, div, ref acc, colorMap, wantInterior);
        if (caughtAt > 0 && DeepOrbitPeriodicityDiagnostic) SmoothBuffer[idx] = -caughtAt;   // #1368
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B — HIGH IMPACT 1: 4-wide AVX2 DD (DD4) with scalar fallback
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateHighPrecision<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        // D-6b — sub-rect mode derives per-pixel world geometry from the
        // FULL image's centre + dims, so all tiles of one image share a
        // single reference orbit. When inactive (Image* / SubRect* zero)
        // the effective dims fall back to Width/Height and behaviour
        // matches the legacy single-render path bit-for-bit.
        int effImgW = EffectiveImageWidth;
        int effImgH = EffectiveImageHeight;
        double scale = (3.5 / Math.Max(effImgW, effImgH)) / Zoom;
        int maxIt = MaxIterations;
        LastFrameUsedGpuPerturbation = false;   // set true only on a successful GPU perturb dispatch below
        LastFrameUsedGpuSeriesApproximation = false;
        LastFrameUsedGpuBla = false;
        LastFrameUsedGpuLa = false;   // #1630
        LastFrameUsedGpuLaFloatExp = false;   // #1664
        LastFrameUsedGpuLaTableFx = false;   // #1880
        LastFrameUsedGpuFloatExpPixels = false;   // #1882
        LastFrameUsedGpuLaAtFx = false;   // #1886
        _lastFrameUsedLa = false;   // #1626
        _lastFrameUsedLaSimd = false;   // #1628

        // One reference orbit at the view centre. Each pixel iterates only
        // the double-precision delta δ_n = z_n − Z_n.
        //
        // Centre precision selection:
        //   • Zoom ≤ 1e25  →  DD center (~31 digits) is sufficient.
        //   • Zoom > 1e25  →  promote to QD center (~62 digits), supporting
        //                     zoom to ~5×10⁵⁸. Adds 4 limbs (X2, X3, Y2, Y3)
        //                     populated by MainForm pan/zoom when active.
        //   • Zoom > 1e50  →  promote to OD center (~124 digits) — Wave 2.11.
        //                     Adds 4 more limbs (X4..X7, Y4..Y7).
        //
        // Wave 3.5 — reference-orbit recycling. Reset the per-frame centre
        // shift, then try to reuse the cached orbit from a slightly-moved
        // centre. On a hit TryRecycle keeps the orbit + sets Δc; on a miss
        // (or when the feature is off) Δc stays zero and we compute fresh.
        _refRecycleDx = 0.0;
        _refRecycleDy = 0.0;
        // SM-11b — recycle also when this render opts in per-frame (progressive
        // pan/zoom PREVIEW sidecars) so a drag keeps ONE reference orbit across
        // preview frames instead of recomputing it each move. Recomputing per
        // move is what makes the same region redraw slightly differently — the
        // "image jumps around while dragging" the user sees at deep zoom. The
        // committed full-res frame leaves this off and computes fresh, so the
        // final image is always exact; only the transient preview reuses.
        bool fx = UseFloatExpPixels;   // #1842: past double the scale and dc underflow
        _lastFrameUsedFx = fx;
        bool recycled = !fx && (AllowRefOrbitRecycle || AllowRecycleThisRender)
                        && TryRecycleReferenceOrbit(maxIt, scale);
        long tStage0 = Stopwatch.GetTimestamp();   // #1614 — LastHpStageMs
        if (!recycled) ComputeReferenceOrbitForCenter(maxIt, ct);   // #1427: cancellable
        long tStageOrbit = Stopwatch.GetTimestamp();
        LastHpStageMs = new HpStageMs(Ms(tStage0, tStageOrbit), 0, 0, 0);
        if (fx)
        {
            // #1882 (C6b) — the float-exp LA kernel with a float-exp pixel scale, where its
            // route holds; otherwise (and on a GPU failure) the CPU float-exp rows.
            if (TryRunGpuFloatExpPixels(colorMap, maxIt, ct))
            {
                LastHpStageMs = new HpStageMs(Ms(tStage0, tStageOrbit), 0, 0, Ms(tStageOrbit, Stopwatch.GetTimestamp()));
                _panFrameComplete = false;
                return;
            }
            CalculateFloatExpRows(colorMap, maxIt, ct);
            LastHpStageMs = new HpStageMs(Ms(tStage0, tStageOrbit), 0, 0, Ms(tStageOrbit, Stopwatch.GetTimestamp()));
            _panFrameComplete = false;
            return;
        }
        // #1851 — a pixel AA jitter is a shift of every pixel's dc: the recycle shift.
        _refRecycleDx += PixelJitter.X * scale;
        _refRecycleDy += PixelJitter.Y * scale;

        // V6 (#82) — GPU deep-zoom perturbation. With the reference orbit built,
        // dispatch the whole frame's δ-rebased loop to an FP64 GPU kernel, then
        // colour/aux writeback on the CPU exactly as the CPU PT path does. Gated
        // to the plain rebased regime this mirrors: recycle off (dc measured from
        // the live centre), no per-tile cap, diagnostic toggles off. Runs the
        // rebased loop for EVERY pixel (glitch-free, SM-2) — the same maths
        // --vulkanpturbprobe validated against ComputePixelPTRebased. Any kernel
        // failure falls through to the CPU SIMD path below.
        {
            int[]? prCap = PerRowMaxIter;
            bool tileCap = prCap != null && prCap.Length >= Height;
            bool gpuDeep = GpuDeepHere;   // #1665: explicit, or measured faster (#1786: or the calibration's route)
            bool gateOk = !LatticeAligned   // #1330: the lattice is a CPU path; #1427: a recycled orbit runs here too
                && gpuDeep && GpuKernel != null
                && (GpuKernel.SupportsPerturbation || FloatExpHere(GpuKernel))   // #1331: no fp64 needed
                && AllowPtRebasing && !UseDdRebaseReference && !ForceScalarPtPath
                && !tileCap && Zoom <= GpuPerturbZoomLimit && _refOrbitLen >= 1   // #1664: deeper on the float-exp LA route
                // #1626 / #1629 / #1630: LA forced On runs on the GPU only where the kernel has the LA stages.
                && (EffectiveLaMode != DeepLaMode.On || (GpuLaHere && GpuKernel.SupportsPerturbationLa) || LaFxRouteHere(GpuKernel));
            Dbg86($"GATE zoom={Zoom:0e+0} gateOk={gateOk} recycled={recycled} " +
                  $"useGpuPerturb={gpuDeep} kernel={(GpuKernel != null)} " +
                  $"supports={(GpuKernel?.SupportsPerturbation ?? false)} allowRebase={AllowPtRebasing} " +
                  $"ddRebaseRef={UseDdRebaseReference} forceScalar={ForceScalarPtPath} tileCap={tileCap} " +
                  $"zoomOk={Zoom <= MaxGpuPerturbZoom} refLen={_refOrbitLen}");
            // #1173-M — the first failing gate condition, when GPU perturbation is on.
            if (!gateOk && gpuDeep && GpuKernel != null && GpuKernel.SupportsPerturbation)
            {
                _gpuSkip = LatticeAligned ? GpuRoute.Cpu("pan reuse", "pixel reuse while panning (#1330) renders deep frames on the CPU")
                    : !AllowPtRebasing || UseDdRebaseReference || ForceScalarPtPath
                        ? GpuRoute.Cpu("diagnostic toggle", "a perturbation diagnostic toggle (rebasing / DD reference / scalar path) keeps deep zoom on the CPU")
                    : EffectiveLaMode == DeepLaMode.On ? GpuRoute.Cpu("LA evaluator", "the LA stage evaluator forced On (#1629) runs on the CPU: this GPU kernel has no LA stages (or FF_GPU_LA=0)")
                    : tileCap ? GpuRoute.Cpu("per-tile iter cap", "per-tile iteration caps render on the CPU only")
                    : Zoom > GpuPerturbZoomLimit ? GpuRoute.Cpu($"zoom > {GpuPerturbZoomLimit:0e0}", $"past the GPU perturbation zoom limit ({GpuPerturbZoomLimit:0e0})")
                    : GpuRoute.Cpu("no reference orbit", "the reference orbit is empty");
            }
            if (gateOk && TryRunGpuPerturbation(colorMap, scale, maxIt, effImgW, effImgH, ct))
            {
                return;
            }
        }

        // Build / refresh the BLA table now that the reference orbit is current.
        // dcMaxAbs is the worst-case pixel offset from view centre (corner
        // distance) measured across the FULL image — sub-rect tiles share
        // the BLA table parameters so the master-shipped orbit's BLA
        // coefficients are valid for any tile pixel of the same image.
        double halfWS = effImgW * 0.5 * scale;
        double halfHS = effImgH * 0.5 * scale;
        if (LatticeAligned)
        {
            // #1330 — sized for every offset up to LatticeMaxOffset, so one anchor
            // keeps one BLA table (pixel results then don't depend on the offset).
            double maxOff = Math.Max(EffectiveLatticeMaxOffset,
                Math.Max(Math.Abs(SubRectOffsetX), Math.Abs(SubRectOffsetY)));
            halfWS = (effImgW * 0.5 + maxOff) * scale;
            halfHS = (effImgH * 0.5 + maxOff) * scale;
        }
        double dcMaxAbs = Math.Sqrt(halfWS * halfWS + halfHS * halfHS);
        if (recycled)
        {
            // Recycled frames measure dc from the CACHED centre, so worst-case
            // |δ_0| grows by the centre shift. Widen the BLA/SA validity radius
            // to cover it — EnsureBlaTable rebuilds the (cheap) table on the
            // KEPT orbit when the drift exceeds its 5% tolerance, so every
            // recycled pixel's dc stays inside a valid linearisation.
            dcMaxAbs += Math.Sqrt(_refRecycleDx * _refRecycleDx + _refRecycleDy * _refRecycleDy);
        }
        else if (PixelJitter != default)
        {
            // #1851 — a pixel AA jitter moves dc by up to its length past the frame's corner.
            dcMaxAbs += Math.Sqrt(PixelJitter.X * PixelJitter.X + PixelJitter.Y * PixelJitter.Y) * scale;
        }
        long tStageBla0 = Stopwatch.GetTimestamp();
        EnsureBlaTable(dcMaxAbs);
        if (BuildLaStages || (LaWanted && !DisableAcceleration)) EnsureLaTable();   // #1625; #1626 / #1629 read it
        long tStageBla = Stopwatch.GetTimestamp();
        EnsureSeriesApproximation();
        long tStageSa = Stopwatch.GetTimestamp();

        _blaSkipsTotal = 0;
        _blaIterSkippedTotal = 0;
        _ptStepsTotal = 0;
        _saAppliedTotal = 0;
        _saIterSkippedTotal = 0;
        ResetLaWork();
        bool useLa = LaActive;   // #1626 — the LA evaluator replaces the PT rows for the frame
        _lastFrameUsedLa = useLa;
        if (useLa) SelectLaAt(dcMaxAbs);   // #1627
        bool useLa4 = useLa && LaSimdActive;   // #1628
        _lastFrameUsedLaSimd = useLa4;

        // D-6b1 — SIMD PT4 / PT8 now honour sub-rect geometry; the legacy
        // single-render path (SubRectOffsetX/Y == 0, ImageWidth/Height ==
        // Width/Height) collapses to the original Width/halfW formula by
        // arithmetic. The `subRect` local is gone — the same SIMD
        // dispatch fires for tile and full-image renders.
        bool useSimd512 = Avx512F.IsSupported && Vector512.IsHardwareAccelerated;
        bool useSimd = DD4.IsSupported;
        // SM-11a diagnostic: force the scalar per-pixel path so every pixel routes
        // through ComputePixelPTRebased (→ the DD-reference variant when enabled),
        // isolating whether DD reference cures the deep-zoom centre-dependence.
        if (ForceScalarPtPath) { useSimd512 = false; useSimd = false; }
        if (ForcePt8Rows) useSimd512 = true;   // #1802 test hook (Vector512 runs emulated without AVX-512)
        if (!_loggedSimdPath)
        {
            Debug.WriteLine(useSimd512 ? "PT path: AVX-512 (8 lanes)"
                                       : useSimd ? "PT path: AVX2 (4 lanes)"
                                                 : "PT path: scalar");
            _loggedSimdPath = true;
        }
        // Phase 2.1: HP perturbation honours PerRowMaxIter when supplied
        // (Video PerTile mode). Per-row lookup is one int read per row —
        // negligible against the perturbation inner loop. Reference orbit
        // length is independent of pixel cap, so capping individual rows
        // doesn't invalidate BLA/SA tables.
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;

        _po.CancellationToken = ct;
        var po = _po;
        // #1330 — a panned frame computes only the exposed rows / columns.
        var pan = _panRegion;
        int w = Width, h = Height;
        long computed = 0;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int xBegin = 0, xEnd = w;
            if (pan is { } pr && y + pr.Dy >= 0 && y + pr.Dy < h)
            {
                if (pr.Dx > 0) xBegin = w - pr.Dx;
                else if (pr.Dx < 0) xEnd = -pr.Dx;
                else return;   // this row is all reused
            }
            if (!RowMarked(y)) return;   // #1344
            Interlocked.Add(ref computed, xEnd - xBegin);
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            int rowBase = y * Width;
            if (useLa4)
                ComputeRowLa4(y, scale, rowMaxIt, rowBase, colorMap, xBegin, xEnd);
            else if (useLa)
                ComputeRowLa(y, scale, rowMaxIt, rowBase, colorMap, xBegin, xEnd);
            else if (useSimd512)
                ComputeRowPT8(y, scale, rowMaxIt, rowBase, colorMap, xBegin, xEnd);
            else if (useSimd)
                ComputeRowPT4(y, scale, rowMaxIt, rowBase, colorMap, xBegin, xEnd);
            else
                ComputeRowPTScalar(y, scale, rowMaxIt, rowBase, colorMap, xBegin, xEnd);
            // Phase 2.1 in-set rewrite (see CalculateDoublePrecision for
            // the rationale). Row-capped unescaped pixels get reclassed
            // as in-set so the recolor gate works.
            if (rowMaxIt < maxIt)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (IterationBuffer[rowBase + x] >= rowMaxIt)
                        IterationBuffer[rowBase + x] = maxIt;
                }
            }
        });
        LastFrameComputedPixels += computed;   // #1343: a solid-region frame runs several masked passes
        LastHpStageMs = new HpStageMs(Ms(tStage0, tStageOrbit), Ms(tStageBla0, tStageBla), Ms(tStageBla, tStageSa),
                                      Ms(tStageSa, Stopwatch.GetTimestamp()));
        // #1330 — a complete lattice frame (whole, or panned from one) can be panned again.
        _panFrameComplete = LatticeAligned && !recycled && !useTileCap && !ct.IsCancellationRequested;

        if (_blaTable != null)
            Debug.WriteLine(
                $"BLA{(_blaTable.DdPrecision ? "-DD" : "")}: {_blaSkipsTotal:N0} skips, " +
                $"{_blaIterSkippedTotal:N0} iter saved " +
                $"(avg {(_blaSkipsTotal == 0 ? 0 : _blaIterSkippedTotal / (double)_blaSkipsTotal):F1}/skip), " +
                $"refLen={_refOrbitLen}, levels={_blaTable.Levels}, dcMax={dcMaxAbs:E2}");

        if (_sa != null)
            Debug.WriteLine(
                $"SA : {_saAppliedTotal:N0} applied, {_saIterSkippedTotal:N0} iter saved " +
                $"(avg {(_saAppliedTotal == 0 ? 0 : _saIterSkippedTotal / (double)_saAppliedTotal):F1}/apply), " +
                $"safeMax={_sa.SafeMax}");
    }

    /// <summary>
    /// F16 (#603) — run an orbit ColorGen theme on the GPU shallow-escape kernel
    /// when it's enabled and viable, colouring the buffer end-to-end (the orbit
    /// kernel accumulates the trap/stripe/… inputs per iteration). Returns false —
    /// so the caller runs the scalar CPU <see cref="CalculateOrbitAware"/> — when
    /// GPU orbit is off (mask None ⇒ default off via
    /// <see cref="InterpretedOrbitColorMap.GpuEnabled"/>), the theme colours the
    /// interior (GPU in-set uses the isInSet=1 path, not MapInteriorWithOrbit),
    /// the zoom is past <see cref="MaxGpuZoom"/> (deep frames take the GPU deep orbit
    /// kernel instead, <see cref="TryRunGpuOrbitPerturbation{TMap}"/>, #607),
    /// or no kernel / palette compile. Exterior, shallow-zoom scope. #1173-J — the
    /// kernel also writes the per-pixel trap minimum into <see cref="TrapBuffer"/>
    /// (the orbit-trap relief height source), as the CPU orbit path does.
    /// </summary>
    private bool TryRunGpuOrbit(IOrbitAwareColorMap map, CancellationToken ct)
    {
        if (map is not FracturingFog.Interefaces.IGpuOrbitPalette orbit
            || orbit.OrbitInputs == FracturingFog.Interefaces.GpuOrbitInputs.None)
            return false;
        if (!UseGpuCompute || GpuKernel == null || !Fp32GpuResolvesView) return false;   // #1611
        // GPU colours in-set via EvalPalette(isInSet=1); the CPU interior path uses
        // MapInteriorWithOrbit. Keep interior-colouring themes on the CPU.
        if (map.WantsInteriorColor) return false;

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;

        try
        {
            GpuKernel.SetPalette((FracturingFog.Interefaces.IGpuHlslPalette)map);
            if (!GpuKernel.HasGpuPalette) return false; // compile failed → CPU
            RunGpuSp(GpuKernel, scale, maxIt, EscapeRadius2,   // #1332 — deferred aux
                useTileCap ? perRow : null, FracturingFog.Rendering.FractalKind.Mandelbrot, 0f, 0f,
                ColorBuffer, TrapBuffer, default, fillDistance: false);   // #1173-J trap
            LastFrameUsedGpuCompute = true;   // #1173-M — the orbit kernel ran on the GPU
            IsHighPrecisionActive = false;    // shallow by the gate above; don't keep a deep frame's flag
            return true;
        }
        catch
        {
            return false; // any GPU failure → fall back to the CPU orbit path
        }
    }

    /// <summary>
    /// V6 (#82) — run the deep-zoom perturbation frame on the GPU. Dispatches the
    /// δ-rebased loop over the just-built reference orbit (Hi-limb doubles) via
    /// <see cref="IGpuKernel.RunPerturb"/>, then fills colour/distance/normal on
    /// the CPU from the GPU's iter + final z/dz — the same
    /// <see cref="FillAuxAndColorHP{TMap}"/> writeback the CPU PT path uses.
    /// Offsets mirror <c>ComputeRowPTScalar</c>: pixel (x,y) sits at image-space
    /// offset (SubRectOffsetX + x − effImgW/2, …) from the reference centre.
    /// Returns false (and leaves the caller to run the CPU path) on any kernel
    /// failure. Caller has already gated recycle / per-tile / diagnostic modes.
    /// </summary>
    private bool TryRunGpuPerturbation<TMap>(
        TMap colorMap, double scale, int maxIt, int effImgW, int effImgH, CancellationToken ct)
        where TMap : IColorMap
    {
        var kernel = GpuKernel;
        if (kernel == null) return false;
        bool useSa = false, useBla = false, wave = false, useLa = false, useLaFx = false;
        FracturingFog.FFMath.LaStageTableFx? laTableFx = null;   // #1880
        // #1340 — the deep kernels' schedule for this dispatch (Auto: the last frame's hint).
        // #1394 — a sparse solid-region pass takes the wavefront schedule: its threads pull
        // only marked pixels, while one thread per pixel idles a group's lanes around a 1-px
        // line (GT 710, 640x360 deep grid lines: 9 % of the full frame's time vs 37 %). A
        // dense one (Auto declined: the rest of the frame) keeps the frame's own schedule.
        bool Hint() => _gpuWavefrontNext || _gpuPassMask is { } pm && pm.Count * 2 < (long)Width * Height;
        GpuWavefront.PerturbHint = Hint();
        try
        {
            // #1427 — a recycled orbit (the AA samples' jitter, a preview drag) measures dc
            // from the cached centre: the CPU rows add Δc to each pixel's dc, here it goes
            // into the pixel offset, dc = (x + off)·scale + Δc = (x + off + Δc/scale)·scale.
            double offX0 = SubRectOffsetX - effImgW * 0.5 + _refRecycleDx / scale;
            double offY0 = SubRectOffsetY - effImgH * 0.5 + _refRecycleDy / scale;
            // #1630 — the LA stages on the GPU, under the CPU evaluator's own conditions
            // (LaActive): the frame wants LA, acceleration and rebasing are on, the table
            // is finite; the kernel runs the evaluator with climbing back and no tail BLA.
            // #1664 — the float-exp LA kernel first, where it is the device's deep kernel.
            bool laFx = LaFxRouteHere(kernel);
            if (laFx || (LaWanted && GpuLaHere && kernel.SupportsPerturbationLa && !DisableAcceleration
                && LaClimbOnRebase && !LaTailBla && AllowPtRebasing && !UseDdRebaseReference))
            {
                EnsureLaTable();
                ResetLaWork();
                double hW = effImgW * 0.5 * scale, hH = effImgH * 0.5 * scale;
                SelectLaAt(Math.Sqrt(hW * hW + hH * hH));
                // (A one-entry reference has no step for the kernel's tail to take, #1661.)
                bool ok = _laTable is { Finite: true, Stages: >= 1 } && _refOrbitLen >= 2;
                useLaFx = ok && laFx;
                useLa = ok && !laFx;
                // #1880 — deep, A and B (∏2Z, the dc response) overflow the double table on
                // long orbits; the float-exp kernel takes the float-exp table (C5c) there.
                if (laFx && !ok && _refOrbitLen >= 2 && EnsureLaTableFx() is { Finite: true, Stages: >= 1 } tfx)
                {
                    laTableFx = tfx;
                    useLaFx = true;
                }
            }
            // #1664 — past the fp64 / plain kernels' limit only the float-exp LA kernel runs.
            if (!useLaFx && Zoom > MaxGpuPerturbZoom)
            {
                _gpuSkip = GpuRoute.Cpu($"zoom > {MaxGpuPerturbZoom:0e0}", $"past the GPU perturbation zoom limit ({MaxGpuPerturbZoom:0e0})");
                return false;
            }
            if (useLaFx)
            {
                // #1880 — the double table, or the float-exp one (no AT: as the CPU's FX rows).
                uint[] steps = laTableFx?.GpuStepsFloatExp ?? _laTable!.GpuStepsFloatExp;
                uint[] ints = laTableFx?.GpuInts ?? _laTable!.GpuInts;
                int stages = laTableFx?.Stages ?? _laTable!.Stages;
                var g = laTableFx == null && _laAt is { } a
                    ? new FracturingFog.Rendering.GpuLaAt(a.Len, a.ThrC, a.Esc2, a.RefCRe, a.RefCIm, a.CRe, a.CIm, a.InvZRe, a.InvZIm)
                    : default;
                if (laTableFx != null)
                {
                    // #1886 — the float-exp table's AT (the double one isn't there).
                    double hWx = effImgW * 0.5 * scale, hHx = effImgH * 0.5 * scale;
                    SelectLaAtFx(laTableFx, FracturingFog.FFMath.FloatExp.FromDouble(Math.Sqrt(hWx * hWx + hHx * hHx)));
                    if (_laAtFx is { } afx) g = FracturingFog.Rendering.GpuLaAt.FromFx(afx);
                }
                SeedWavefrontIfAsked((w, h, sc, ox, oy, it, sm, zr, zi, dr, di) => kernel.RunPerturbLaFloatExp(   // #1386
                    w, h, sc, maxIt, EscapeRadius2, ox, oy, _refZr, _refZi, _refOrbitLen,
                    steps, ints, stages, g, it, sm, zr, zi, dr, di), scale, offX0, offY0);
                GpuWavefront.PerturbHint = Hint();
                wave = GpuWavefront.UsePerturb;
                kernel.RunPerturbLaFloatExp(
                    Width, Height, scale, maxIt, EscapeRadius2,
                    offX0, offY0,
                    _refZr, _refZi, _refOrbitLen,
                    steps, ints, stages, g,
                    IterationBuffer, SmoothBuffer,
                    FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer);
                useLa = true;   // the LA labels and work below
                LastFrameUsedGpuFloatExp = true;
            }
            else if (useLa)
            {
                var t = _laTable!;
                var g = _laAt is { } a
                    ? new FracturingFog.Rendering.GpuLaAt(a.Len, a.ThrC, a.Esc2, a.RefCRe, a.RefCIm, a.CRe, a.CIm, a.InvZRe, a.InvZIm)
                    : default;
                SeedWavefrontIfAsked((w, h, sc, ox, oy, it, sm, zr, zi, dr, di) => kernel.RunPerturbLa(   // #1386
                    w, h, sc, maxIt, EscapeRadius2, ox, oy, _refZr, _refZi, _refOrbitLen,
                    t.GpuSteps, t.GpuInts, t.Stages, g, it, sm, zr, zi, dr, di), scale, offX0, offY0);
                GpuWavefront.PerturbHint = Hint();
                wave = GpuWavefront.UsePerturb;
                kernel.RunPerturbLa(
                    Width, Height, scale, maxIt, EscapeRadius2,
                    offX0, offY0,
                    _refZr, _refZi, _refOrbitLen,
                    t.GpuSteps, t.GpuInts, t.Stages, g,
                    IterationBuffer, SmoothBuffer,
                    FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer);
            }
            // #88 / G4.5 — the SA prelude under the same conditions the CPU path
            // applies it (coefficients valid past 16 iterations, acceleration on).
            // #1786 — the float-exp kernel chosen (Fp32 mode, a measured Fp32, the calibration's
            // route) runs as chosen: the SA / BLA kernel is fp64, so it would replace it.
            bool fxChosen = !useLa && FloatExpHere(kernel);   // (useLa is also set by the float-exp LA kernel)
            if (!useLa && !fxChosen && UseGpuSeriesApproximation && kernel.SupportsPerturbationSA
                && !DisableAcceleration && !DisableSeriesApproximation)
            {
                EnsureSeriesApproximation();
                useSa = _sa is { SafeMax: >= 16 };
            }
            // #88 / G4.5b — the CPU's BLA table, built for the same worst-case pixel
            // offset (the image corner) the CPU path uses; applied in the SA kernel.
            if (!useLa && !fxChosen && UseGpuBla && kernel.SupportsPerturbationSA && !DisableAcceleration)
            {
                double halfWS = effImgW * 0.5 * scale, halfHS = effImgH * 0.5 * scale;
                EnsureBlaTable(Math.Sqrt(halfWS * halfWS + halfHS * halfHS), cpuRows: false);
                useBla = _blaTable is { Levels: > 1 } && _blaTable.RefLen == _refOrbitLen;
                // Without SA the coefficient arrays only fill the kernel's bindings
                // (safeMax 0 never skips); the cached table is the cheapest source.
                if (useBla && !useSa) { EnsureSeriesApproximation(); useBla = _sa != null; }
            }
            if (!useLa && (useSa || useBla))
            {
                var sa = _sa!;
                var bla = useBla ? _blaTable : null;
                SeedWavefrontIfAsked((w, h, sc, ox, oy, it, sm, zr, zi, dr, di) => kernel.RunPerturbSA(   // #1386
                    w, h, sc, maxIt, EscapeRadius2, ox, oy, _refZr, _refZi, _refOrbitLen,
                    SaTolerance, useSa ? sa.SafeMax : 0, sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                    it, sm, zr, zi, dr, di, bla?.GpuCoefficients, bla?.Levels ?? 0), scale, offX0, offY0);
                GpuWavefront.PerturbHint = Hint();   // (the seed may have set the hint)
                wave = GpuWavefront.UsePerturb;
                kernel.RunPerturbSA(
                    Width, Height, scale, maxIt, EscapeRadius2,
                    offX0, offY0,
                    _refZr, _refZi, _refOrbitLen,
                    SaTolerance, useSa ? sa.SafeMax : 0,
                    sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                    IterationBuffer, SmoothBuffer,
                    FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer,
                    bla?.GpuCoefficients, bla?.Levels ?? 0);
            }
            else if (!useLa && fxChosen)
            {
                // #1331 — the all-fp32 float-exp kernel (weak / no fp64); #1384: wavefront too.
                SeedWavefrontIfAsked((w, h, sc, ox, oy, it, sm, zr, zi, dr, di) => kernel.RunPerturbFloatExp(   // #1386
                    w, h, sc, maxIt, EscapeRadius2, ox, oy, _refZr, _refZi, _refOrbitLen,
                    it, sm, zr, zi, dr, di), scale, offX0, offY0);
                GpuWavefront.PerturbHint = Hint();   // (the seed may have set the hint)
                wave = GpuWavefront.UsePerturb;
                kernel.RunPerturbFloatExp(
                    Width, Height, scale, maxIt, EscapeRadius2,
                    offX0, offY0,
                    _refZr, _refZi, _refOrbitLen,
                    IterationBuffer, SmoothBuffer,
                    FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer);
                LastFrameUsedGpuFloatExp = true;
            }
            else if (!useLa)
            {
                SeedWavefrontIfAsked((w, h, sc, ox, oy, it, sm, zr, zi, dr, di) => kernel.RunPerturb(   // #1386
                    w, h, sc, maxIt, EscapeRadius2, ox, oy, _refZr, _refZi, _refOrbitLen,
                    it, sm, zr, zi, dr, di), scale, offX0, offY0);
                GpuWavefront.PerturbHint = Hint();   // (the seed may have set the hint)
                wave = GpuWavefront.UsePerturb;
                kernel.RunPerturb(
                    Width, Height, scale, maxIt, EscapeRadius2,
                    offX0, offY0,
                    _refZr, _refZi, _refOrbitLen,
                    IterationBuffer, SmoothBuffer,
                    FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer);
            }
        }
        catch (Exception ex)
        {
            OnGpuPerturbationFailed(ex);
            return false;
        }
        finally { GpuWavefront.PerturbHint = false; }

        _po.CancellationToken = ct;
        var po = _po;
        // #1660 — the kernels write dz/dc · 2^ILogB(scale) (a plain float overflowed
        // past ~1e35); back to dz/dc in double before the normals and distance.
        double dzUn = FracturingFog.Rendering.GpuPerturbDerivative.Unscale(scale);
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested || !GpuRowMarked(y)) return;
            int rb = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = rb + x;
                if (!GpuMarked(idx)) continue;   // #1394: another pass's pixel
                FillAuxAndColorHP(idx, IterationBuffer[idx], maxIt,
                    FinalZrBuffer[idx], FinalZiBuffer[idx],
                    FinalDrBuffer[idx] * dzUn, FinalDiBuffer[idx] * dzUn, colorMap);
            }
        });
        LastFrameUsedGpuPerturbation = true;
        LastFrameUsedGpuSeriesApproximation = useSa;
        LastFrameUsedGpuBla = useBla;
        LastFrameUsedGpuLa = useLa;   // #1630
        LastFrameUsedGpuLaFloatExp = useLaFx;   // #1664
        LastFrameUsedGpuLaTableFx = laTableFx != null;   // #1880
        LastFrameUsedGpuLaAtFx = laTableFx != null && _laAtFx is { } atFxUsed   // #1886; #1887: as the kernel gets it
            && FracturingFog.Rendering.GpuLaFxParams.AtUsable(FracturingFog.Rendering.GpuLaAt.FromFx(atFxUsed));
        if (useLa) _lastFrameUsedLa = true;   // the precision label's " LA"
        LastFrameUsedGpuWavefront = wave;
        // #1394 — a solid-region pass computed only its marked pixels: no warp-waste
        // sample for the next frame's hint.
        if (_gpuPassMask != null) _gpuPassMasked = true;
        else MeasureWavefrontHint();
        LogGpuPerturbEngaged();
        return true;
    }

    // #1340 — Auto: this GPU deep frame's warp waste picks the next frame's schedule
    // (#1386: as this view's hint).
    private void MeasureWavefrontHint()
    {
        LastGpuWarpWaste = 0;
        if (GpuWavefront.Mode == GpuWavefront.On && GpuWavefront.PerturbMode == GpuWavefront.Auto)
        {
            LastGpuWarpWaste = GpuWavefront.WarpWaste(IterationBuffer, Width, Height);
            SetWavefrontHint(LastGpuWarpWaste);
        }
    }

    private void LogGpuPerturbEngaged()
    {
        if (!_loggedGpuPerturbEngaged)
        {
            _loggedGpuPerturbEngaged = true;
            Console.Error.WriteLine(
                $"[GPU] deep-zoom perturbation engaged — {GpuKernel?.GetType().Name ?? "GPU"} " +
                $"(first frame at zoom {(ZoomLog10 > 300 ? $"1e{ZoomLog10:0}" : Zoom.ToString("0e+0"))}).");   // #1882: log zoom past double
        }
    }

    // Per-pixel orbit means read back from the GPU deep orbit kernel (#607).
    private float[]? _gpuOrbitMeans;

    /// <summary>
    /// #607 / G4.6 — run a deep-zoom orbit frame on the GPU: the orbit-accumulating
    /// perturbation kernel (<see cref="IGpuKernel.RunPerturbOrbit"/>) iterates the
    /// same rebased δ loop as <see cref="ComputePixelOrbitPerturbation{TMap}"/> over
    /// the just-built reference orbit and returns the mask'd orbit means; each pixel
    /// is then coloured here through the same <see cref="FinalizeOrbitPixel{TMap}"/>
    /// (MapWithOrbit + aux buffers), with an accumulator rebuilt from the means
    /// (sums = mean, counts = 1, which MapWithOrbit and the aux writeback read back
    /// unchanged). Scope = the shallow GPU orbit kernel's: a GPU orbit palette,
    /// exterior colouring. Returns false (CPU path) when not viable or on failure.
    /// </summary>
    private bool TryRunGpuOrbitPerturbation<TMap>(
        TMap colorMap, double scale, int maxIt, int effImgW, int effImgH, CancellationToken ct)
        where TMap : IOrbitAwareColorMap
    {
        if (colorMap is not FracturingFog.Interefaces.IGpuOrbitPalette orbit
            || orbit.OrbitInputs == FracturingFog.Interefaces.GpuOrbitInputs.None)
            return false;
        var kernel = GpuKernel;
        if (!GpuDeepHere || !UseGpuDeepOrbit || kernel == null || !kernel.SupportsPerturbationOrbit)
            return false;
        // GPU in-set pixels carry no accumulator; interior-colouring themes stay on the CPU.
        if (colorMap.WantsInteriorColor) return false;
        int[]? perRow = PerRowMaxIter;
        if (perRow != null && perRow.Length >= Height)
        {
            _gpuSkip = GpuRoute.Cpu("per-tile iter cap", "per-tile iteration caps render on the CPU only");
            return false;
        }
        if (Zoom > MaxGpuPerturbZoom)
        {
            _gpuSkip = GpuRoute.Cpu($"zoom > {MaxGpuPerturbZoom:0e0}", $"past the GPU perturbation zoom limit ({MaxGpuPerturbZoom:0e0})");
            return false;
        }
        if (_refOrbitLen < 1) return false;

        int mask = (int)orbit.OrbitInputs & ((1 << 11) - 1);
        int stride = System.Numerics.BitOperations.PopCount((uint)mask);
        int n = Width * Height;
        if (_gpuOrbitMeans == null || _gpuOrbitMeans.Length < n * stride) _gpuOrbitMeans = new float[n * stride];
        var means = _gpuOrbitMeans;
        // #1384 — the orbit kernel's schedule (Auto: the last frame's hint). It runs no
        // seed dispatch (#1386): that would need an orbit-means buffer of its own.
        GpuWavefront.PerturbHint = _gpuWavefrontNext;
        bool wave = GpuWavefront.UsePerturb;
        try
        {
            kernel.RunPerturbOrbit(
                Width, Height, scale, maxIt, EscapeRadius2,
                SubRectOffsetX - effImgW * 0.5, SubRectOffsetY - effImgH * 0.5,
                _refZr, _refZi, _refOrbitLen,
                mask, CenterX, CenterY,
                IterationBuffer, SmoothBuffer,
                FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer,
                means);
        }
        catch (Exception ex)
        {
            OnGpuPerturbationFailed(ex);
            return false;
        }
        finally { GpuWavefront.PerturbHint = false; }

        _po.CancellationToken = ct;
        var po = _po;
        double dzUn = FracturingFog.Rendering.GpuPerturbDerivative.Unscale(scale);   // #1660: scaled dz/dc
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rb = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = rb + x;
                colorMap.InitOrbit(out var acc);
                int o = idx * stride;
                // Bit order = GpuOrbitInputs (MandelbrotKernelSource.BuildPerturbOrbit).
                if ((mask & (1 << 0)) != 0) acc.TrapMin = means[o++];
                if ((mask & (1 << 1)) != 0) acc.TrapMin2 = means[o++];
                if ((mask & (1 << 2)) != 0) acc.TrapMin3 = means[o++];
                if ((mask & (1 << 3)) != 0) acc.TrapMin4 = means[o++];
                if ((mask & (1 << 4)) != 0) acc.TrapMin5 = means[o++];
                if ((mask & (1 << 5)) != 0) { acc.StripeSum = means[o++]; acc.StripeCount = 1; }
                if ((mask & (1 << 6)) != 0) { acc.TiaSum = means[o++]; acc.TiaCount = 1; }
                if ((mask & (1 << 7)) != 0) { acc.CurvatureSum = means[o++]; acc.CurvatureCount = 1; }
                if ((mask & (1 << 8)) != 0) { acc.LyapunovSum = means[o++]; acc.LyapunovCount = 1; }
                if ((mask & (1 << 9)) != 0) { acc.GaussianSum = means[o++]; acc.GaussianCount = 1; }
                if ((mask & (1 << 10)) != 0) { acc.ExpSum = means[o++]; acc.ExpCount = 1; }
                FinalizeOrbitPixel(idx, IterationBuffer[idx], maxIt,
                    FinalZrBuffer[idx], FinalZiBuffer[idx], FinalDrBuffer[idx] * dzUn, FinalDiBuffer[idx] * dzUn,
                    ref acc, colorMap, wantInterior: false);
            }
        });
        LastFrameUsedGpuPerturbation = true;
        LastFrameUsedGpuOrbitPerturbation = true;
        LastFrameUsedGpuWavefront = wave;   // #1384
        MeasureWavefrontHint();
        LogGpuPerturbEngaged();
        return true;
    }

    /// <summary>Common GPU perturbation dispatch failure handling: record the route,
    /// and latch GPU perturbation off for the session on a too-slow abort or a lost
    /// device.</summary>
    private void OnGpuPerturbationFailed(Exception ex)
    {
        Debug.WriteLine($"[MandelbrotCalculator] GPU perturbation dispatch failed; CPU fallback: {ex.Message}");
        Dbg86($"RUNPERTURB-THREW {ex.GetType().Name}: {ex.Message} refLen={_refOrbitLen} zoom={Zoom:0e+0}");
        _gpuSkip = GpuRoute.Cpu("GPU error", $"GPU perturbation failed: {ex.Message}");   // #1173-M

        // A lost/removed/hung GPU device (TDR) does not recover on its own and
        // — because the D3D kernel shares the renderer's device — also kills
        // presentation. Disable GPU perturbation for the rest of the session
        // so we (a) stop hammering a dead device with retries and (b) fall
        // straight through to the CPU deep path from here on. Row-band tiling
        // is meant to prevent the TDR in the first place; this is the belt-
        // and-braces backstop if a band still overruns on very slow FP64.
        string m = ex.Message ?? "";
        // Marker set by MandelbrotKernelSource.PerturbTooSlowMarker in the
        // backend kernels (Engine can't reference Rendering.*, so match the
        // literal — keep the two in sync).
        bool tooSlow = m.Contains("GPU-PERTURB-TOO-SLOW", StringComparison.Ordinal);
        bool deviceLost = m.Contains("DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
                       || m.Contains("DeviceRemoved", StringComparison.OrdinalIgnoreCase)
                       || m.Contains("DEVICE_HUNG", StringComparison.OrdinalIgnoreCase)
                       || m.Contains("device lost", StringComparison.OrdinalIgnoreCase);
        if (tooSlow && CalibrationRoute != null)
        {
            // #1786 — a calibration run measured the GPU too slow: that is its result (the
            // route fell back, so it is not eligible); the session keeps its GPU route.
            Dbg86($"TOO-SLOW during calibration ({CalibrationRoute}): not latched");
        }
        else if (tooSlow)
        {
            // The GPU's FP64 is too slow at this depth to beat the CPU (weak
            // consumer card). Disable for the session so every later frame
            // goes straight to the multi-threaded CPU deep path.
            UseGpuPerturbation = false;
            s_gpuPerturbDisabled = GpuRoute.Cpu("deep zoom too slow", "GPU deep-zoom perturbation was disabled for this session: the GPU is slower than the CPU here (weak fp64). "
                + "FF_GPU_PERTURB_BUDGET_MS sets the abort budget; Control Center \u2192 Advanced \u2192 GPU deep zoom turns it back on.");   // #1173-M, #1493
            Console.Error.WriteLine(
                "[GPU] deep-zoom perturbation DISABLED for this session — the GPU is slower than the " +
                "CPU here (weak FP64). Using the CPU deep path. Tune FF_GPU_PERTURB_BUDGET_MS to change " +
                "the threshold, or run on a stronger-FP64 GPU.");
            Dbg86($"TOO-SLOW → UseGpuPerturbation disabled for session ({ex.Message})");
        }
        else if (deviceLost)
        {
            UseGpuPerturbation = false;
            s_gpuPerturbDisabled = GpuRoute.Cpu("device lost", "GPU deep-zoom perturbation was disabled for this session: the device was lost during a dispatch (TDR). "
                + "Control Center \u2192 Advanced \u2192 GPU deep zoom turns it back on.");   // #1173-M, #1493
            Console.Error.WriteLine(
                "[GPU] deep-zoom perturbation DISABLED for this session — the GPU device was " +
                "lost/removed during a dispatch (TDR). Falling back to the CPU deep path. " +
                "Restart with a higher OS TdrDelay or a stronger-FP64 GPU to re-enable.");
            Dbg86("DEVICE-LOST → UseGpuPerturbation disabled for session");
        }
    }

    // One-shot latch so the "engaged" line logs once per process, not per frame.
    private bool _loggedGpuPerturbEngaged;

    /// <summary>
    /// Build / refresh the series approximation table for the current
    /// reference orbit. Recomputed only when the orbit changes — coefficients
    /// are dc-independent so no zoom-drift invalidation is needed.
    /// </summary>
    private void EnsureSeriesApproximation()
    {
        if (_sa != null
            && _saForRefOrbitLen == _refOrbitLen
            && _saForRefMaxIter == _refCachedMaxIter
            && _saForRefOrbitGen == _refOrbitGen)
            return;

        if (_refOrbitLen < 4) { _sa = null; return; }
        _sa = new SeriesApproximation(_refZr, _refZi, _refOrbitLen);
        _saForRefOrbitLen = _refOrbitLen;
        _saForRefMaxIter = _refCachedMaxIter;
        _saForRefOrbitGen = _refOrbitGen;
    }

    /// <summary>
    /// Build or refresh the BLA hierarchical table. Invalidated when the
    /// reference orbit changes (maxIter, length) or when dcMaxAbs has drifted
    /// far enough that previously-merged validity radii are stale.
    /// </summary>
    // cpuRows: the table is for the CPU PT rows, which tighten ε in the QD band when they
    // resume rebased pixels (#1613); otherwise for the GPU SA / BLA kernel, which always
    // rebases with BLA, so it tightens in the same band (#1791).
    private void EnsureBlaTable(double dcMaxAbs, bool cpuRows = true)
    {
        bool refChanged = _blaForRefMaxIter != _refCachedMaxIter
                       || _blaForRefOrbitLen != _refOrbitLen
                       || _blaForRefOrbitGen != _refOrbitGen;
        // Relative tolerance: BLA merge uses dcMaxAbs in its validity bound,
        // so a 5% drift is safely within the linearisation margin (ε, BlaTolerance).
        double dcDrift = _blaDcMaxAbs <= 0 ? double.PositiveInfinity
                                           : Math.Abs(dcMaxAbs - _blaDcMaxAbs) / _blaDcMaxAbs;
        double eps = BlaTolerance > 0 && double.IsFinite(BlaTolerance) ? BlaTolerance : BlaTable.DefaultEpsilon;
        if (cpuRows ? ResumesInQdBand : InQdBand) eps = Math.Min(eps, QdBandBlaTolerance);   // #1613, #1791
        if (!refChanged && _blaTable != null && dcDrift < 0.05 && _blaTable.Epsilon == eps) return;

        if (_refOrbitLen < 4) { _blaTable = null; return; }
        // Wave 2.10 — DD-precision BLA tables. HP path always populates
        // _refZrLo/_refZiLo (DD low limb when Zoom ≤ 1e25, QD X1 limb when
        // Zoom > 1e25). Feed them into the DD merge so chains of 2^k step
        // merges retain DD precision in A_n (near 1.0 + tiny at deep zoom
        // — single-precision storage loses ULPs after k≈14 merge levels,
        // visible as iteration banding past ~1e15).
        //
        // Gate at Zoom > DdBlaZoomThreshold (default 1e15). Below that, the
        // 14-merge-level error hasn't accumulated to ULP-visible banding yet,
        // and the DD merge is ~7× slower than the single-precision merge.
        // Hitting every Calculate at zoom 1e12..1e15 with the DD merge cost
        // for invisible benefit was the perf regression user reported when
        // "DD kicks in" at medium depth.
        bool useDdBla = !DisableDdBla && Zoom > DdBlaZoomThreshold;
        _blaTable = useDdBla
            ? new BlaTable(_refZr, _refZrLo, _refZi, _refZiLo, _refOrbitLen, dcMaxAbs, eps)
            : new BlaTable(_refZr, _refZi, _refOrbitLen, dcMaxAbs, eps);
        _blaDcMaxAbs = dcMaxAbs;
        _blaForRefMaxIter = _refCachedMaxIter;
        _blaForRefOrbitLen = _refOrbitLen;
        _blaForRefOrbitGen = _refOrbitGen;
    }

    // #1625 — build or keep the LA stages for this reference. The stages cover Z_0 ..
    // Z_R with R the period when the orbit is periodic (#1614: Z_p ≈ 0, the arrays run
    // one further, to Z_{p+1}), else the orbit length (cap or escape). τ follows the
    // ε the CPU rows use (BlaTolerance, tightened in the QD band, #1613).
    private void EnsureLaTable()
    {
        double eps = BlaTolerance > 0 && double.IsFinite(BlaTolerance) ? BlaTolerance : BlaTable.DefaultEpsilon;
        if (ResumesInQdBand) eps = Math.Min(eps, QdBandBlaTolerance);
        double tol = LaStageTable.ToleranceFor(eps);
        int r = _refPeriod > 0 ? _refPeriod : _refOrbitLen;
        var test = LaPeriodTest;   // #1626
        if (_laTable != null && _laForRefOrbitGen == _refOrbitGen && _laTable.Tolerance == tol && _laTable.RefLen == r
            && _laTable.PeriodTest == test && _laTable.StageBits == LaStageBits) return;
        _laTable = LaStageTable.Build(_refZr, _refZi, r, tol, test, LaStageBits);   // #1614: stage bits
        _laForRefOrbitGen = _refOrbitGen;
    }

    // ── Blend helpers (replaces VBLENDVPD masking) ────────────────────────────

    /// <summary>
    /// Converts a 4-bit integer mask (bit i = lane i active) to a
    /// Vector256 of all-ones (active) or all-zeros (frozen) per lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> MaskToVector(int mask)
    {
        // Each lane gets all-bits-set (-1 reinterpreted as double) if active
        long m0 = (mask & 1) != 0 ? -1L : 0L;
        long m1 = (mask & 2) != 0 ? -1L : 0L;
        long m2 = (mask & 4) != 0 ? -1L : 0L;
        long m3 = (mask & 8) != 0 ? -1L : 0L;
        return Vector256.Create(m0, m1, m2, m3).AsDouble();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> BlendActive(
        Vector256<double> frozen, Vector256<double> updated, int activeMask)
    {
        var mask = MaskToVector(activeMask);
        return Avx.BlendVariable(frozen, updated, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DD4 BlendDD4Active(DD4 frozen, DD4 updated, int activeMask)
    {
        var mask = MaskToVector(activeMask);
        return new DD4(
            Avx.BlendVariable(frozen.Hi, updated.Hi, mask),
            Avx.BlendVariable(frozen.Lo, updated.Lo, mask));
    }

    /// <summary>
    /// Horizontal max over Vector256&lt;double&gt;, restricted to lanes selected
    /// by the 4-bit active mask. Inactive lanes are ignored. Returns 0 when
    /// activeMask is empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HMaxMasked(Vector256<double> v, int activeMask)
    {
        double max = 0.0;
        if ((activeMask & 1) != 0) { double d = v.GetElement(0); if (d > max) max = d; }
        if ((activeMask & 2) != 0) { double d = v.GetElement(1); if (d > max) max = d; }
        if ((activeMask & 4) != 0) { double d = v.GetElement(2); if (d > max) max = d; }
        if ((activeMask & 8) != 0) { double d = v.GetElement(3); if (d > max) max = d; }
        return max;
    }

    // ── 8-lane helpers (AVX-512 path) ─────────────────────────────────────────

    /// <summary>8-bit mask → Vector512&lt;double&gt; with all-ones in active lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> MaskToVector512(int mask)
    {
        return Vector512.Create(
            (mask & 0x01) != 0 ? -1L : 0L,
            (mask & 0x02) != 0 ? -1L : 0L,
            (mask & 0x04) != 0 ? -1L : 0L,
            (mask & 0x08) != 0 ? -1L : 0L,
            (mask & 0x10) != 0 ? -1L : 0L,
            (mask & 0x20) != 0 ? -1L : 0L,
            (mask & 0x40) != 0 ? -1L : 0L,
            (mask & 0x80) != 0 ? -1L : 0L).AsDouble();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> BlendActive512(
        Vector512<double> frozen, Vector512<double> updated, int activeMask)
    {
        var mask = MaskToVector512(activeMask);
        return Vector512.ConditionalSelect(mask, updated, frozen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HMaxMasked8(Vector512<double> v, int activeMask)
    {
        double max = 0.0;
        if ((activeMask & 0x01) != 0) { double d = v.GetElement(0); if (d > max) max = d; }
        if ((activeMask & 0x02) != 0) { double d = v.GetElement(1); if (d > max) max = d; }
        if ((activeMask & 0x04) != 0) { double d = v.GetElement(2); if (d > max) max = d; }
        if ((activeMask & 0x08) != 0) { double d = v.GetElement(3); if (d > max) max = d; }
        if ((activeMask & 0x10) != 0) { double d = v.GetElement(4); if (d > max) max = d; }
        if ((activeMask & 0x20) != 0) { double d = v.GetElement(5); if (d > max) max = d; }
        if ((activeMask & 0x40) != 0) { double d = v.GetElement(6); if (d > max) max = d; }
        if ((activeMask & 0x80) != 0) { double d = v.GetElement(7); if (d > max) max = d; }
        return max;
    }

    // ── HP per-pixel color fill (inline, no second pass) ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorHP<TMap>(
        int idx, int iters, int maxIter,
        double zrD, double ziD, double drD, double diD,
        TMap colorMap)
        where TMap : IColorMap
    {
        ToDistanceUnit(ref drD, ref diD);   // #1843
        FillAuxAndColorHPUnit(idx, iters, maxIter, zrD, ziD, drD, diD, colorMap);
    }

    // #1843 — from a FloatExp derivative (the float-exp rows, past ~1e290): scaled into
    // the distance unit before it becomes a double, so it stays finite past ~1e308.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorHP<TMap>(
        int idx, int iters, int maxIter,
        double zrD, double ziD, FloatExp drv, FloatExp div,
        TMap colorMap)
        where TMap : IColorMap
    {
        var (ur, ui) = DistanceUnitOf(drv, div);
        FillAuxAndColorHPUnit(idx, iters, maxIter, zrD, ziD, ur, ui, colorMap);
    }

    // #1843 — <paramref name="drD"/>, <paramref name="diD"/>: dz/dc in the distance unit
    // (ToDistanceUnit). The normals are exact (only the derivative's length was scaled);
    // FinalDr/Di hold this unit too; the map gets complex-plane units unless it opts in.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorHPUnit<TMap>(
        int idx, int iters, int maxIter,
        double zrD, double ziD, double drD, double diD,
        TMap colorMap)
        where TMap : IColorMap
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zrD * zrD + ziD * ziD);
            float smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            // Distance estimate: mag·ln(mag)/|d|. Derivative tracked as plain
            // double in the PT path — same magnitude domain as SP, so the
            // formula transfers without precision loss. Restores 3D-theme
            // shading (Phong/PBR) at deep zoom.
            float dist = DeDistUnit(mag, drD, diD, out float mapDist);
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zrD, ziD, drD, diD);

            float fzr = (float)zrD, fzi = (float)ziD;
            float fdr = (float)drD, fdi = (float)diD;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;

            int iterArg = colorMap is IColorMapHandlesInSet ? iters : maxIter;
            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, mapDist, iterArg,
                NormalXBuffer[idx], NormalYBuffer[idx],
                fzr, fzi, MapDz(drD, _mapDistExp2), MapDz(diD, _mapDistExp2));   // #1843
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            if (colorMap is IColorMapHandlesInSet)
            {
                ColorBuffer[idx] = (uint)colorMap.Map(
                    0f, 0f, maxIter,
                    0f, 0f, 0f, 0f, 0f, 0f);
            }
            else
            {
                ColorBuffer[idx] = colorMap.InSetColor;
            }
        }
    }

    // ── Full-DD per-pixel fallback (used for PT glitches at zoom ≤ QDZoomThreshold) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelHP<TMap>(DD cx, DD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        // Bulb early-out — practically only fires for shallow-zoom HP frames
        // (the parent set's main bulb/cardioid lives at zoom ~1). At deep
        // zoom the test returns false in O(1), so the overhead is trivial.
        if (IsInMainCardioidOrBulb(cx.Hi, cy.Hi))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        DD zr = DD.Zero, zi = DD.Zero;
        double dr = 1.0, di = 0.0;
        int iterStart = 0;

        // SA prelude — skip the first k DD iterations by evaluating the
        // polynomial in dc and seeding (zr, zi) = ref[k] + δ.
        //
        // dc must be computed via DD subtraction. At deep zoom the pixel
        // offset (~3e-15 at z=1e14) is well below ULP(CenterX) (~6e-17 at
        // |x|=0.5), so naive `cx.Hi - _refCxHi` rounds adjacent pixels to
        // identical doubles, producing identical SA δ and visible pixelation.
        // DD subtraction preserves the offset in the resulting Hi limb.
        var sa = _sa;
        if (sa != null && sa.SafeMax >= 16 && !DisableAcceleration && !DisableSeriesApproximation)
        {
            DD dcRdd = cx - new DD(_refCxHi, _refCxLo);
            DD dcIdd = cy - new DD(_refCyHi, _refCyLo);
            double dcR = dcRdd.Hi;
            double dcI = dcIdd.Hi;
            int k = sa.FindSkip(dcR, dcI, SaTolerance, maxIter - 1);
            if (k >= 16 && k <= _refOrbitLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                sa.EvalDDelta(k, dcR, dcI, out double ddR, out double ddI);
                // Seed from full-precision reference orbit limbs. Using only
                // _refZr[k] (Hi limb) drops DD precision of ref_k, so all
                // near-centre pixels start from the same wrong z and converge
                // to one colour at deep zoom — the "growing centre dot" bug.
                zr = new DD(_refZr[k], _refZrLo[k]) + new DD(dR, 0);
                zi = new DD(_refZi[k], _refZiLo[k]) + new DD(dI, 0);
                dr = ddR;
                di = ddI;
                iterStart = k;
            }
        }

        double zrSnapHi = 0, zrSnapLo = 0, ziSnapHi = 0, ziSnapLo = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = iterStart; iter < maxIter; iter++)
        {
            DD zr2 = zr.Square();
            DD zi2 = zi.Square();
            DD mag2 = zr2 + zi2;
            if (mag2 >= EscapeRadius2) break;

            double newDr = 2.0 * (zr.Hi * dr - zi.Hi * di) + 1.0;
            double newDi = 2.0 * (zr.Hi * di + zi.Hi * dr);
            dr = newDr; di = newDi;

            DD newZi = (zr * zi) * 2.0 + cy;
            DD newZr = zr2 - zi2 + cx;
            zr = newZr; zi = newZi;

            // Periodicity check on full DD limbs — at deep zoom the Lo limbs
            // carry significant cycle-distinguishing bits, so comparing only
            // Hi could yield false positives in tightly packed mini-set
            // interiors.
            if (zr.Hi == zrSnapHi && zr.Lo == zrSnapLo
                && zi.Hi == ziSnapHi && zi.Lo == ziSnapLo)
            { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnapHi = zr.Hi; zrSnapLo = zr.Lo;
                ziSnapHi = zi.Hi; ziSnapLo = zi.Lo;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr.Hi, zi.Hi, dr, di, colorMap);
    }

    // ── Full-QD per-pixel fallback (used for PT glitches at zoom > QDZoomThreshold) ─
    // DD cannot distinguish adjacent pixels at zoom > ~5e27 (pixel spacing falls below
    // DD precision ~6e-32), causing all glitched pixels in a block to produce identical
    // coordinates and colors. QD (~62 digits) resolves pixels down to zoom ~1e64.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelQD<TMap>(QD cx, QD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        // Bulb test — at QD zoom (>1e25) this never fires but the cost is
        // a single compare-and-branch, negligible compared to QD math.
        if (IsInMainCardioidOrBulb(cx.X0, cy.X0))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        QD zr = QD.Zero, zi = QD.Zero;
        double dr = 1.0, di = 0.0;
        int iterStart = 0;

        // SA prelude — mirror of ComputePixelHP, with QD-precision dc.
        // QD subtraction preserves the pixel offset even at zoom > 1e25 where
        // the offset lives in the X2/X3 limbs; collapsing to X0 after the
        // subtraction yields the small offset at double precision, suitable
        // for SA polynomial input.
        var sa = _sa;
        if (sa != null && sa.SafeMax >= 16 && !DisableAcceleration && !DisableSeriesApproximation)
        {
            QD refCx = new QD(_refCxHi, _refCxLo, _refCx2, _refCx3);
            QD refCy = new QD(_refCyHi, _refCyLo, _refCy2, _refCy3);
            QD dcRqd = cx - refCx;
            QD dcIqd = cy - refCy;
            double dcR = dcRqd.X0;
            double dcI = dcIqd.X0;
            int k = sa.FindSkip(dcR, dcI, SaTolerance, maxIter - 1);
            if (k >= 16 && k <= _refOrbitLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                sa.EvalDDelta(k, dcR, dcI, out double ddR, out double ddI);
                // Seed from full QD reference orbit. Constructing the QD from
                // only `_refZr[k] + dR` discards X1/X2/X3 of ref_k — at deep
                // zoom (>~1e32) the lost limbs swamp pixel-level differences,
                // collapsing near-centre pixels to a single trajectory and a
                // single colour ("growing centre dot" bug).
                QD refZk = new QD(_refZr[k], _refZrLo[k], _refZrX2[k], _refZrX3[k]);
                QD refIk = new QD(_refZi[k], _refZiLo[k], _refZiX2[k], _refZiX3[k]);
                zr = refZk + new QD(dR);
                zi = refIk + new QD(dI);
                dr = ddR;
                di = ddI;
                iterStart = k;
            }
        }

        double zrSnap0 = 0, zrSnap1 = 0, ziSnap0 = 0, ziSnap1 = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = iterStart; iter < maxIter; iter++)
        {
            double zrH = zr.X0, ziH = zi.X0;
            if (zrH * zrH + ziH * ziH >= EscapeRadius2) break;

            double newDr = 2.0 * (zrH * dr - ziH * di) + 1.0;
            double newDi = 2.0 * (zrH * di + ziH * dr);
            dr = newDr; di = newDi;

            QD newZi = (zr * zi) * 2.0 + cy;
            QD newZr = zr.Square() - zi.Square() + cx;
            zr = newZr; zi = newZi;

            // Periodicity — comparing X0/X1 limbs is sufficient even at QD
            // zoom because attracting cycles snap to bit-equal X0 bits well
            // before they could diverge on X2/X3.
            if (zr.X0 == zrSnap0 && zr.X1 == zrSnap1
                && zi.X0 == ziSnap0 && zi.X1 == ziSnap1)
            { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap0 = zr.X0; zrSnap1 = zr.X1;
                ziSnap0 = zi.X0; ziSnap1 = zi.X1;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr.X0, zi.X0, dr, di, colorMap);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B perturbation theory — reference orbit + double-precision delta
    // ─────────────────────────────────────────────────────────────────────────

    // Wave 3.5 — a recycled orbit may serve a frame whose centre moved up to
    // this fraction of the frame's corner-dc. The reference orbit (the
    // expensive QD/OD build we're skipping) stays a valid perturbation base
    // well past the frame corner; only the cheaper BLA/SA linearisation is
    // radius-bounded, and those are rebuilt for the widened dc on a recycle.
    // 0.25 → worst-case pixel dc ≤ 1.25× the original corner, a mild
    // extrapolation the --reforbitrecycle probe verifies against a fresh render.
    private const double RecycleMaxShiftFactor = 0.25;

    /// <summary>Wave 3.5 — decide whether the cached reference orbit can serve
    /// the current frame from a slightly-shifted centre instead of being
    /// recomputed. Returns true (and sets <see cref="_refRecycleDx"/> /
    /// <see cref="_refRecycleDy"/>) only when: the cache exists at the SAME
    /// precision tier, its length is usable, its maxIter covers this frame, and
    /// the centre shift stays under <see cref="RecycleMaxShiftFactor"/> of the
    /// frame corner-dc. On a hit the (cheap) BLA/SA tables are rebuilt for the
    /// widened dc by the caller; the (expensive) orbit build is skipped. A
    /// recycled frame matches a fresh render within perturbation float
    /// tolerance — the DD/QD/OD glitch fallbacks derive
    /// δc = absoluteWorldCoord − storedRefCentre, so they stay exact. Any doubt
    /// → false → recompute. A zero shift also returns false so the cheaper
    /// exact-centre short-circuit in ComputeReferenceOrbit* owns that case.</summary>
    private bool TryRecycleReferenceOrbit(int maxIter, double scale)
    {
        // Nothing cached, or the orbit is too short to be a useful base.
        if (_refCachedTier < 0 || _refOrbitLen < 4) { _refRecycleMisses++; return false; }

        // Precision tier must match — a tier change alters the centre limbs and
        // the fallback precision, invalidating direct reuse.
        int tier = UseApReference ? 3 : Zoom > ODZoomThreshold ? 2 : (Zoom > QDZoomThreshold ? 1 : 0);   // #1840: 3 = AP
        if (tier != _refCachedTier) { _refRecycleMisses++; return false; }

        // maxIter coverage — same predicate as the exact-centre cache: a cached
        // orbit that escaped is valid for any cap; otherwise its cap must reach
        // this frame's cap.
        if (!(_refCachedEscaped || maxIter <= _refCachedMaxIter)) { _refRecycleMisses++; return false; }

        // Centre shift Δc = newCentre − cachedCentre, computed at the tier's
        // precision then rounded to double (the SIMD PT dc is double at every
        // tier; precision-sensitive pixels take the exact QD/OD fallback).
        double dx, dy;
        switch (tier)
        {
            case 0:
                dx = (double)(new DD(CenterX, CenterXLo) - new DD(_refCxHi, _refCxLo));
                dy = (double)(new DD(CenterY, CenterYLo) - new DD(_refCyHi, _refCyLo));
                break;
            case 1:
                dx = (double)(new QD(CenterX, CenterXLo, CenterX2, CenterX3)
                            - new QD(_refCxHi, _refCxLo, _refCx2, _refCx3));
                dy = (double)(new QD(CenterY, CenterYLo, CenterY2, CenterY3)
                            - new QD(_refCyHi, _refCyLo, _refCy2, _refCy3));
                break;
            case 3 when CenterExact is { } ce && _refExact is { } re:
                // #1840 — exact: the view's exact centre minus the orbit's. (Without a
                // valid exact centre — a jitter moved the limbs — the limb difference
                // below, against the orbit centre's own limbs: OD's rounding of the exact
                // centre, ~1e-128, is then never taken for a shift.)
                dx = BigFloat.Sub(ce.Re, re.Re, Math.Max(ce.Precision, re.Precision)).ToDouble();
                dy = BigFloat.Sub(ce.Im, re.Im, Math.Max(ce.Precision, re.Precision)).ToDouble();
                break;
            default:
                dx = (double)(new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                                     CenterX4, CenterX5, CenterX6, CenterX7)
                            - new OD(_refCxHi, _refCxLo, _refCx2, _refCx3,
                                     _refCx4, _refCx5, _refCx6, _refCx7));
                dy = (double)(new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                                     CenterY4, CenterY5, CenterY6, CenterY7)
                            - new OD(_refCyHi, _refCyLo, _refCy2, _refCy3,
                                     _refCy4, _refCy5, _refCy6, _refCy7));
                break;
        }

        // Identical centre → let the exact-centre cache branch handle it (it is
        // cheaper and bit-exact; recycling only earns its keep when the centre
        // actually moved).
        if (dx == 0.0 && dy == 0.0) { _refRecycleMisses++; return false; }

        double shift = Math.Sqrt(dx * dx + dy * dy);
        double halfWS = EffectiveImageWidth  * 0.5 * scale;
        double halfHS = EffectiveImageHeight * 0.5 * scale;
        double frameDcMax = Math.Sqrt(halfWS * halfWS + halfHS * halfHS);

        // Reject shifts that push the reference too far from the frame — the
        // orbit's perturbation validity, while wider than the BLA radius, is not
        // unbounded, and a large extrapolation risks glitch-storm fallback that
        // erases the recycling win.
        if (shift > frameDcMax * RecycleMaxShiftFactor) { _refRecycleMisses++; return false; }

        _refRecycleDx = dx;
        _refRecycleDy = dy;
        _refRecycleHits++;
        return true;
    }

    private void ComputeReferenceOrbit(DD cx, DD cy, int maxIter, CancellationToken ct = default)
    {
        // Cache hit: same center (all 4 QD limbs match — DD path uses limbs 2,3 = 0)
        // and either previous run already escaped (valid for any maxIter) or
        // previous maxIter cap >= current cap.
        bool centerSame = cx.Hi == _refCxHi && cx.Lo == _refCxLo
                       && _refCx2 == 0 && _refCx3 == 0
                       && _refCx4 == 0 && _refCx5 == 0 && _refCx6 == 0 && _refCx7 == 0
                       && cy.Hi == _refCyHi && cy.Lo == _refCyLo
                       && _refCy2 == 0 && _refCy3 == 0
                       && _refCy4 == 0 && _refCy5 == 0 && _refCy6 == 0 && _refCy7 == 0;
        if (centerSame && (_refCachedEscaped || PeriodicOrbitFits() || maxIter <= _refCachedMaxIter)
            && PeriodicOrbitStillValid())
            return;

        BeginRefOrbit(maxIter);   // #1828: grows as built
        InvalidateRefOrbitCache();   // #1427: a cancel mid-build leaves no stale hit
        DD zr = DD.Zero, zi = DD.Zero;
        double logDerivSum = 0.0;   // Σ log₁₀|2·Zₙ| — δ-amplification decades
        int period = 0;             // #1614
        double periodZ2 = 0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            if ((n & 1023) == 1023) ct.ThrowIfCancellationRequested();   // #1427
            if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828
            _refZr[n] = zr.Hi;  _refZrLo[n] = zr.Lo;
            _refZi[n] = zi.Hi;  _refZiLo[n] = zi.Lo;
            _refZrX2[n] = 0;    _refZrX3[n] = 0;
            _refZiX2[n] = 0;    _refZiX3[n] = 0;
            _refZrX4[n] = 0;    _refZrX5[n] = 0; _refZrX6[n] = 0; _refZrX7[n] = 0;
            _refZiX4[n] = 0;    _refZiX5[n] = 0; _refZiX6[n] = 0; _refZiX7[n] = 0;
            double zmag2 = zr.Hi * zr.Hi + zi.Hi * zi.Hi;
            if (zmag2 >= EscapeRadius2) break;
            // #1614 — back at the nucleus: |Z_n| below the pixel size. The orbit
            // repeats from here, so keep Z_{n+1} and stop (see _refPeriod).
            if (n > 0 && zmag2 < _refPeriodTol2) { period = n; periodZ2 = zmag2; }
            // Derivative product ∏|2·Zₙ| ⇒ Σ log₁₀|2·Zₙ|, INCLUDING contraction
            // steps (|2·Z|<1, negative), and frozen once |Z|>2: past the classic
            // escape radius the orbit blows up to the bailout and its derivative
            // growth no longer corresponds to resolvable detail (that tail would
            // otherwise inflate the estimate ~5 decades over the real collapse).
            if (zmag2 > 0.0 && zmag2 <= 4.0) logDerivSum += 0.5 * Math.Log10(4.0 * zmag2);
            DD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
            if (period > 0) { n++; break; }
        }
        MaxUsefulZoomLog10 = n < maxIter && period == 0 ? logDerivSum : double.PositiveInfinity;
        if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828: the final Z_n
        _refZr[n] = zr.Hi;  _refZrLo[n] = zr.Lo;
        _refZi[n] = zi.Hi;  _refZiLo[n] = zi.Lo;
        _refZrX2[n] = 0;    _refZrX3[n] = 0;
        _refZiX2[n] = 0;    _refZiX3[n] = 0;
        _refZrX4[n] = 0;    _refZrX5[n] = 0; _refZrX6[n] = 0; _refZrX7[n] = 0;
        _refZiX4[n] = 0;    _refZiX5[n] = 0; _refZiX6[n] = 0; _refZiX7[n] = 0;
        TrimRefOrbit(n);   // #1828
        _refOrbitLen = n;  // == maxIter when centre is interior
        _refOrbitGen++;

        _refCxHi = cx.Hi; _refCxLo = cx.Lo; _refCx2 = 0; _refCx3 = 0;
        _refCx4 = 0; _refCx5 = 0; _refCx6 = 0; _refCx7 = 0;
        _refCyHi = cy.Hi; _refCyLo = cy.Lo; _refCy2 = 0; _refCy3 = 0;
        _refCy4 = 0; _refCy5 = 0; _refCy6 = 0; _refCy7 = 0;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter && period == 0;
        _refPeriod = period;   // #1614
        _refPeriodZ2 = periodZ2;
        _refCachedTier = 0;  // DD
    }

    // Wave 2.11 — single allocation point. QD/OD paths share this storage
    // even when their lower limbs hold zero, so per-pixel fallback can read
    // a uniform 8-limb refZ unconditionally.
    // #1427 — the orbit arrays are about to be overwritten: drop the cache key first, so
    // a build cancelled part-way (ct, every 1,024 iterations) can't be taken for the
    // cached orbit of its old centre, or recycled. The generation bump makes the BLA /
    // LA / SA tables built on the old orbit stale too.
    private void InvalidateRefOrbitCache()
    {
        _refCxHi = double.NaN; _refCyHi = double.NaN;
        _refCachedTier = -1;
        _refCachedEscaped = false;
        _refExact = null;   // #1840
        _refOrbitGen++;
    }

    // #1828 — the CPU builders grow the orbit arrays as they go, instead of sizing them to
    // the iteration cap up front: at p951,094 the cap (28.5 M) made 16 × 228 MB = 3.65 GB per
    // calculator for an orbit that stops at its period (951,095 entries), and zeroing the
    // allocation could not be cancelled. Every reader indexes up to _refOrbitLen (≤ the
    // built length + 1), so only the length matters.
    private const int RefOrbitInitialCapacity = 1 << 16;

    /// <summary>#1828 — the orbit arrays' capacity (entries). Diagnostic.</summary>
    public int ReferenceOrbitCapacity => _refZr.Length;

    // A build up to maxIter starts with min(maxIter + 1, 65,536) entries (keeping larger
    // arrays it already has).
    private void BeginRefOrbit(int maxIter)
    {
        int want = Math.Min(maxIter + 1, RefOrbitInitialCapacity);
        if (_refZr.Length < want) ResizeRefOrbit(want, 0);
    }

    // Room for index n: double the capacity (at most maxIter + 1), keeping entries 0..n-1.
    private void GrowRefOrbit(int n, int maxIter)
    {
        long next = Math.Max((long)_refZr.Length * 2, n + 1L);
        ResizeRefOrbit((int)Math.Min(next, maxIter + 1L), n);
    }

    // After a build of length n: drop a capacity more than 4× what it holds (above the
    // initial size), so one long orbit doesn't pin its memory for every later frame.
    private void TrimRefOrbit(int n)
    {
        long need = n + 1L;
        if (_refZr.Length > RefOrbitInitialCapacity && _refZr.Length > 4 * need)
            ResizeRefOrbit((int)Math.Max(need, RefOrbitInitialCapacity), n + 1);
    }

    private void ResizeRefOrbit(int size, int keep)
    {
        static double[] R(double[] a, int size, int keep)
        {
            var b = new double[size];
            Array.Copy(a, b, Math.Min(keep, Math.Min(a.Length, size)));
            return b;
        }
        _refZr = R(_refZr, size, keep); _refZi = R(_refZi, size, keep);
        _refZrLo = R(_refZrLo, size, keep); _refZiLo = R(_refZiLo, size, keep);
        _refZrX2 = R(_refZrX2, size, keep); _refZiX2 = R(_refZiX2, size, keep);
        _refZrX3 = R(_refZrX3, size, keep); _refZiX3 = R(_refZiX3, size, keep);
        _refZrX4 = R(_refZrX4, size, keep); _refZiX4 = R(_refZiX4, size, keep);
        _refZrX5 = R(_refZrX5, size, keep); _refZiX5 = R(_refZiX5, size, keep);
        _refZrX6 = R(_refZrX6, size, keep); _refZiX6 = R(_refZiX6, size, keep);
        _refZrX7 = R(_refZrX7, size, keep); _refZiX7 = R(_refZiX7, size, keep);
    }

    // The GPU QD orbit and the seeded (cluster) orbits write up to maxIter + 1 entries in one
    // go, so they still size the arrays up front.
    private void EnsureRefOrbitCapacity(int maxIter)
    {
        if (_refZr.Length > maxIter) return;
        int sz = maxIter + 1;
        _refZr = new double[sz];
        _refZi = new double[sz];
        _refZrLo = new double[sz];
        _refZiLo = new double[sz];
        _refZrX2 = new double[sz];
        _refZiX2 = new double[sz];
        _refZrX3 = new double[sz];
        _refZiX3 = new double[sz];
        _refZrX4 = new double[sz];
        _refZiX4 = new double[sz];
        _refZrX5 = new double[sz];
        _refZiX5 = new double[sz];
        _refZrX6 = new double[sz];
        _refZiX6 = new double[sz];
        _refZrX7 = new double[sz];
        _refZiX7 = new double[sz];
    }

    // Quad-double reference orbit — engaged at zoom > 1e25. Storage of Z_n
    // remains a single double per slot (the Hi limb), since the per-pixel PT
    // delta loop only consumes Z_n at double precision. The QD math here just
    // ensures Z_n is *correctly rounded* to that double after thousands of
    // iterations from a 62-digit centre.
    private void ComputeReferenceOrbitQD(QD cx, QD cy, int maxIter, CancellationToken ct = default)
    {
        bool centerSame = cx.X0 == _refCxHi && cx.X1 == _refCxLo
                       && cx.X2 == _refCx2 && cx.X3 == _refCx3
                       && _refCx4 == 0 && _refCx5 == 0 && _refCx6 == 0 && _refCx7 == 0
                       && cy.X0 == _refCyHi && cy.X1 == _refCyLo
                       && cy.X2 == _refCy2 && cy.X3 == _refCy3
                       && _refCy4 == 0 && _refCy5 == 0 && _refCy6 == 0 && _refCy7 == 0;
        if (centerSame && (_refCachedEscaped || PeriodicOrbitFits() || maxIter <= _refCachedMaxIter)
            && PeriodicOrbitStillValid())
            return;

        BeginRefOrbit(maxIter);   // #1828: grows as built
        InvalidateRefOrbitCache();   // #1427: a cancel mid-build leaves no stale hit
        QD zr = QD.Zero, zi = QD.Zero;
        double logDerivSum = 0.0;   // Σ log₁₀|2·Zₙ| — δ-amplification decades
        int period = 0;             // #1614
        double periodZ2 = 0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            if ((n & 1023) == 1023) ct.ThrowIfCancellationRequested();   // #1427
            if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828
            _refZr[n] = zr.X0;  _refZrLo[n] = zr.X1;  _refZrX2[n] = zr.X2;  _refZrX3[n] = zr.X3;
            _refZi[n] = zi.X0;  _refZiLo[n] = zi.X1;  _refZiX2[n] = zi.X2;  _refZiX3[n] = zi.X3;
            _refZrX4[n] = 0; _refZrX5[n] = 0; _refZrX6[n] = 0; _refZrX7[n] = 0;
            _refZiX4[n] = 0; _refZiX5[n] = 0; _refZiX6[n] = 0; _refZiX7[n] = 0;
            double zmag2 = zr.X0 * zr.X0 + zi.X0 * zi.X0;
            if (zmag2 >= EscapeRadius2) break;
            // #1614 — back at the nucleus: |Z_n| below the pixel size. The orbit
            // repeats from here, so keep Z_{n+1} and stop (see _refPeriod).
            if (n > 0 && zmag2 < _refPeriodTol2) { period = n; periodZ2 = zmag2; }
            // Derivative product ∏|2·Zₙ| ⇒ Σ log₁₀|2·Zₙ|, INCLUDING contraction
            // steps (|2·Z|<1, negative), and frozen once |Z|>2: past the classic
            // escape radius the orbit blows up to the bailout and its derivative
            // growth no longer corresponds to resolvable detail (that tail would
            // otherwise inflate the estimate ~5 decades over the real collapse).
            if (zmag2 > 0.0 && zmag2 <= 4.0) logDerivSum += 0.5 * Math.Log10(4.0 * zmag2);
            QD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
            if (period > 0) { n++; break; }
        }
        MaxUsefulZoomLog10 = n < maxIter && period == 0 ? logDerivSum : double.PositiveInfinity;
        if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828: the final Z_n
        _refZr[n] = zr.X0;  _refZrLo[n] = zr.X1;  _refZrX2[n] = zr.X2;  _refZrX3[n] = zr.X3;
        _refZi[n] = zi.X0;  _refZiLo[n] = zi.X1;  _refZiX2[n] = zi.X2;  _refZiX3[n] = zi.X3;
        _refZrX4[n] = 0; _refZrX5[n] = 0; _refZrX6[n] = 0; _refZrX7[n] = 0;
        _refZiX4[n] = 0; _refZiX5[n] = 0; _refZiX6[n] = 0; _refZiX7[n] = 0;
        TrimRefOrbit(n);   // #1828
        _refOrbitLen = n;
        _refOrbitGen++;

        _refCxHi = cx.X0; _refCxLo = cx.X1; _refCx2 = cx.X2; _refCx3 = cx.X3;
        _refCx4 = 0; _refCx5 = 0; _refCx6 = 0; _refCx7 = 0;
        _refCyHi = cy.X0; _refCyLo = cy.X1; _refCy2 = cy.X2; _refCy3 = cy.X3;
        _refCy4 = 0; _refCy5 = 0; _refCy6 = 0; _refCy7 = 0;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter && period == 0;
        _refPeriod = period;   // #1614
        _refPeriodZ2 = periodZ2;
        _refCachedTier = 1;  // QD
    }

    /// <summary>The reference orbit at the view centre, at the precision tier the
    /// zoom needs (DD, QD — on the GPU with <see cref="UseGpuReferenceOrbit"/> — or
    /// OD). A centre and length already cached are not recomputed.</summary>
    private void ComputeReferenceOrbitForCenter(int maxIt, CancellationToken ct = default)
    {
        // #1614 — a reference centred on a period-p nucleus returns to |Z_p| below
        // the pixel size and repeats; building past that is wasted. The tolerance
        // is the frame's pixel spacing (squared), so the wrap at Z_p moves no pixel.
        double px = 3.5 / Math.Max(EffectiveImageWidth, EffectiveImageHeight) / Zoom;
        // Detection needs the rebased loops, which wrap at the period (CPU), as do
        // the GPU deep kernels: they rebase when the reference runs out, and since
        // #1621 their SA skip stops short of the buffer's end.
        _refPeriodTol2 = PeriodicReferenceOrbit && AllowPtRebasing && !UseDdRebaseReference
            ? px * px : 0;
        _refPeriodTolFx = PeriodTolFx(PeriodicReferenceOrbit && AllowPtRebasing && !UseDdRebaseReference);   // #1854
        if (UseApReference)
        {
            // #1840 — past OD's reach: BigFloat at the precision the zoom needs, at the
            // exact centre (or the limbs exactly).
            int prec = BigFloat.PrecisionForZoom(ZoomLog10);
            var c = CenterExact ?? ExactCentre.FromDeep(CurrentCentreLimbs(), prec);
            ComputeReferenceOrbitAP(c, prec, maxIt, ct);
        }
        else if (Zoom > ODZoomThreshold)
        {
            var cxOD = new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                              CenterX4, CenterX5, CenterX6, CenterX7);
            var cyOD = new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                              CenterY4, CenterY5, CenterY6, CenterY7);
            ComputeReferenceOrbitOD(cxOD, cyOD, maxIt, ct);
        }
        else if (Zoom > QDZoomThreshold)
        {
            var cxQD = new QD(CenterX, CenterXLo, CenterX2, CenterX3);
            var cyQD = new QD(CenterY, CenterYLo, CenterY2, CenterY3);
            // Wave 2.12 — opt-in GPU QD reference orbit. Falls back to CPU on
            // any GPU init / kernel / copy failure.
            if (!(UseGpuReferenceOrbit && TryComputeReferenceOrbitQDGpu(cxQD, cyQD, maxIt)))
                ComputeReferenceOrbitQD(cxQD, cyQD, maxIt, ct);
        }
        else
        {
            ComputeReferenceOrbit(new DD(CenterX, CenterXLo), new DD(CenterY, CenterYLo), maxIt, ct);
        }
    }

    /// <summary>#1166 — build the reference orbit for the current centre and
    /// <see cref="MaxIterations"/> exactly as a deep-zoom frame does, without
    /// rendering; returns the orbit length. For the reference-orbit bench (the
    /// orbit is cached per centre, so a frame bench only builds it once).
    /// <see cref="LastFrameBuiltGpuReferenceOrbit"/> says where it was built.</summary>
    public int BuildReferenceOrbit() => BuildReferenceOrbit(CancellationToken.None);

    /// <summary>#1840 — <see cref="BuildReferenceOrbit()"/>, cancellable (#1427).</summary>
    public int BuildReferenceOrbit(CancellationToken ct)
    {
        LastFrameBuiltGpuReferenceOrbit = false;
        ComputeReferenceOrbitForCenter(MaxIterations, ct);
        return _refOrbitLen;
    }

    // Wave 2.12 — GPU QD reference orbit dispatch. Lazily instantiated so
    // the kernel JIT cost is paid only when first engaged. Lives on the
    // calculator (not static) because each instance owns its own ref-orbit
    // arrays the GPU writes into.
    private FracturingFog.Calculators.Gpu.MandelbrotRefOrbitGpu? _refOrbitGpu;

    /// <summary>Wave 2.12 — try to run the QD reference orbit on the GPU.
    /// Returns true on success (orbit arrays + cache updated, identical
    /// post-state to <see cref="ComputeReferenceOrbitQD"/>); false on any
    /// GPU failure (caller falls back to CPU). Mirrors the CPU method's
    /// centre-cache short-circuit so cached orbits skip the GPU launch.</summary>
    private bool TryComputeReferenceOrbitQDGpu(QD cx, QD cy, int maxIter)
    {
        bool centerSame = cx.X0 == _refCxHi && cx.X1 == _refCxLo
                       && cx.X2 == _refCx2 && cx.X3 == _refCx3
                       && _refCx4 == 0 && _refCx5 == 0 && _refCx6 == 0 && _refCx7 == 0
                       && cy.X0 == _refCyHi && cy.X1 == _refCyLo
                       && cy.X2 == _refCy2 && cy.X3 == _refCy3
                       && _refCy4 == 0 && _refCy5 == 0 && _refCy6 == 0 && _refCy7 == 0;
        if (centerSame && (_refCachedEscaped || maxIter <= _refCachedMaxIter))
            return true;  // cache hit — nothing to do (same as CPU path)

        EnsureRefOrbitCapacity(maxIter);
        _refOrbitGpu ??= new FracturingFog.Calculators.Gpu.MandelbrotRefOrbitGpu();
        bool ok = _refOrbitGpu.Compute(
            cx.X0, cx.X1, cx.X2, cx.X3,
            cy.X0, cy.X1, cy.X2, cy.X3,
            maxIter, EscapeRadius2,
            _refZr, _refZrLo, _refZrX2, _refZrX3,
            _refZi, _refZiLo, _refZiX2, _refZiX3,
            out int n, out bool escaped);
        if (!ok)
        {
            Debug.WriteLine($"[Wave2.12] GPU ref-orbit failed, falling back to CPU: {_refOrbitGpu.LastError}");
            return false;
        }

        // QD path leaves OD limbs (X4..X7) zero — clear those slots up to n
        // so any cached OD orbit residue from a prior frame doesn't bleed
        // into the QD-tier per-pixel HP fallback.
        for (int k = 0; k <= n; k++)
        {
            _refZrX4[k] = 0; _refZrX5[k] = 0; _refZrX6[k] = 0; _refZrX7[k] = 0;
            _refZiX4[k] = 0; _refZiX5[k] = 0; _refZiX6[k] = 0; _refZiX7[k] = 0;
        }

        TrimRefOrbit(n);   // #1828
        _refOrbitLen = n;
        _refOrbitGen++;

        _refCxHi = cx.X0; _refCxLo = cx.X1; _refCx2 = cx.X2; _refCx3 = cx.X3;
        _refCx4 = 0; _refCx5 = 0; _refCx6 = 0; _refCx7 = 0;
        _refCyHi = cy.X0; _refCyLo = cy.X1; _refCy2 = cy.X2; _refCy3 = cy.X3;
        _refCy4 = 0; _refCy5 = 0; _refCy6 = 0; _refCy7 = 0;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = escaped;
        _refPeriod = 0;   // #1614 — the GPU QD orbit runs to maxIter
        _refCachedTier = 1;  // QD (GPU)
        LastFrameBuiltGpuReferenceOrbit = true;   // #1166
        return true;
    }

    // Octuple-double reference orbit — engaged at zoom > 1e50 (Wave 2.11).
    // Storage mirrors the QD path: per slot, all 8 limbs of Z_n are
    // recorded so the per-pixel HP fallback can reconstruct the full
    // 124-digit ref-orbit value. The PT inner loop still pulls only the
    // Hi limb (_refZr[k] / _refZi[k]) since δ stays double.
    private void ComputeReferenceOrbitOD(OD cx, OD cy, int maxIter, CancellationToken ct = default)
    {
        bool centerSame = cx.X0 == _refCxHi && cx.X1 == _refCxLo
                       && cx.X2 == _refCx2 && cx.X3 == _refCx3
                       && cx.X4 == _refCx4 && cx.X5 == _refCx5
                       && cx.X6 == _refCx6 && cx.X7 == _refCx7
                       && cy.X0 == _refCyHi && cy.X1 == _refCyLo
                       && cy.X2 == _refCy2 && cy.X3 == _refCy3
                       && cy.X4 == _refCy4 && cy.X5 == _refCy5
                       && cy.X6 == _refCy6 && cy.X7 == _refCy7;
        if (centerSame && (_refCachedEscaped || PeriodicOrbitFits() || maxIter <= _refCachedMaxIter)
            && PeriodicOrbitStillValid())
            return;

        BeginRefOrbit(maxIter);   // #1828: grows as built
        InvalidateRefOrbitCache();   // #1427: a cancel mid-build leaves no stale hit
        OD zr = OD.Zero, zi = OD.Zero;
        double logDerivSum = 0.0;   // Σ log₁₀|2·Zₙ| — δ-amplification decades
        int period = 0;             // #1614
        double periodZ2 = 0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            if ((n & 1023) == 1023) ct.ThrowIfCancellationRequested();   // #1427
            if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828
            _refZr[n] = zr.X0;   _refZrLo[n] = zr.X1;
            _refZrX2[n] = zr.X2; _refZrX3[n] = zr.X3;
            _refZrX4[n] = zr.X4; _refZrX5[n] = zr.X5;
            _refZrX6[n] = zr.X6; _refZrX7[n] = zr.X7;
            _refZi[n] = zi.X0;   _refZiLo[n] = zi.X1;
            _refZiX2[n] = zi.X2; _refZiX3[n] = zi.X3;
            _refZiX4[n] = zi.X4; _refZiX5[n] = zi.X5;
            _refZiX6[n] = zi.X6; _refZiX7[n] = zi.X7;
            double zmag2 = zr.X0 * zr.X0 + zi.X0 * zi.X0;
            if (zmag2 >= EscapeRadius2) break;
            // #1614 — back at the nucleus: |Z_n| below the pixel size. The orbit
            // repeats from here, so keep Z_{n+1} and stop (see _refPeriod).
            if (n > 0 && zmag2 < _refPeriodTol2) { period = n; periodZ2 = zmag2; }
            // Derivative product ∏|2·Zₙ| ⇒ Σ log₁₀|2·Zₙ|, INCLUDING contraction
            // steps (|2·Z|<1, negative), and frozen once |Z|>2: past the classic
            // escape radius the orbit blows up to the bailout and its derivative
            // growth no longer corresponds to resolvable detail (that tail would
            // otherwise inflate the estimate ~5 decades over the real collapse).
            if (zmag2 > 0.0 && zmag2 <= 4.0) logDerivSum += 0.5 * Math.Log10(4.0 * zmag2);
            OD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
            if (period > 0) { n++; break; }
        }
        MaxUsefulZoomLog10 = n < maxIter && period == 0 ? logDerivSum : double.PositiveInfinity;
        if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828: the final Z_n
        _refZr[n] = zr.X0;   _refZrLo[n] = zr.X1;
        _refZrX2[n] = zr.X2; _refZrX3[n] = zr.X3;
        _refZrX4[n] = zr.X4; _refZrX5[n] = zr.X5;
        _refZrX6[n] = zr.X6; _refZrX7[n] = zr.X7;
        _refZi[n] = zi.X0;   _refZiLo[n] = zi.X1;
        _refZiX2[n] = zi.X2; _refZiX3[n] = zi.X3;
        _refZiX4[n] = zi.X4; _refZiX5[n] = zi.X5;
        _refZiX6[n] = zi.X6; _refZiX7[n] = zi.X7;
        TrimRefOrbit(n);   // #1828
        _refOrbitLen = n;
        _refOrbitGen++;

        _refCxHi = cx.X0; _refCxLo = cx.X1; _refCx2 = cx.X2; _refCx3 = cx.X3;
        _refCx4 = cx.X4;  _refCx5 = cx.X5;  _refCx6 = cx.X6;  _refCx7 = cx.X7;
        _refCyHi = cy.X0; _refCyLo = cy.X1; _refCy2 = cy.X2; _refCy3 = cy.X3;
        _refCy4 = cy.X4;  _refCy5 = cy.X5;  _refCy6 = cy.X6;  _refCy7 = cy.X7;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter && period == 0;
        _refPeriod = period;   // #1614
        _refPeriodZ2 = periodZ2;
        _refCachedTier = 2;  // OD
    }

    // #1840 — the exact centre and precision the cached tier-3 orbit was built at.
    private ExactCentre? _refExact;
    private int _refExactPrec;

    /// <summary>#1840 (epic #1428, C4) — the reference orbit in BigFloat: z ← z² + c with
    /// one rounding per operation at <paramref name="prec"/> bits. Each Z_n is stored as its
    /// greedy OD split in the eight-limb arrays, so every consumer (perturbation, SA seed,
    /// LA / BLA, rebasing, periodicity) reads it as it reads the OD orbit. Period detection
    /// (#1614), the δ-amplification sum and the cancellation (#1427, every 64 iterations:
    /// an iteration costs ~µs here) follow ComputeReferenceOrbitOD.</summary>
    private void ComputeReferenceOrbitAP(ExactCentre c, int prec, int maxIter, CancellationToken ct = default)
    {
        bool centerSame = _refCachedTier == 3 && _refExact is { } rc
                       && rc.Re == c.Re && rc.Im == c.Im && _refExactPrec >= prec;
        if (centerSame && (_refCachedEscaped || PeriodicOrbitFits() || maxIter <= _refCachedMaxIter)
            && PeriodicOrbitStillValid())
            return;

        BeginRefOrbit(maxIter);   // #1828: grows as built
        InvalidateRefOrbitCache();   // #1427: a cancel mid-build leaves no stale hit
        BigFloat cx = c.Re.WithPrecision(prec), cy = c.Im.WithPrecision(prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        double logDerivSum = 0.0;
        int period = 0;
        double periodZ2 = 0;
        FloatExp periodZ2Fx = FloatExp.Zero;
        bool tolFx = !_refPeriodTolFx.IsZero;   // #1854: past ~1e154 the tolerance is a FloatExp
        OD zrOd = OD.Zero, ziOd = OD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            if ((n & 63) == 63) ct.ThrowIfCancellationRequested();   // #1427
            if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828
            zrOd = zr.ToOD(); ziOd = zi.ToOD();
            StoreRefOd(n, zrOd, ziOd);
            double zmag2 = zrOd.X0 * zrOd.X0 + ziOd.X0 * ziOd.X0;
            if (zmag2 >= EscapeRadius2) break;
            if (n > 0 && zmag2 < _refPeriodTol2) { period = n; periodZ2 = zmag2; }
            else if (n > 0 && tolFx)
            {
                // #1854 — |Z_n|² in FloatExp, from the BigFloat Z_n (its double head underflows).
                var m2 = FloatExp.Norm2(zr.ToFloatExp(), zi.ToFloatExp());
                if (FloatExp.AbsLess(m2, _refPeriodTolFx)) { period = n; periodZ2 = zmag2; periodZ2Fx = m2; }
            }
            if (zmag2 > 0.0 && zmag2 <= 4.0) logDerivSum += 0.5 * Math.Log10(4.0 * zmag2);
            var zr2 = BigFloat.Mul(zr, zr, prec);
            var zi2 = BigFloat.Mul(zi, zi, prec);
            var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), cy, prec);                      // 2·zr·zi + cy
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cx, prec);       // zr² − zi² + cx
            if (period > 0) { n++; break; }
        }
        MaxUsefulZoomLog10 = n < maxIter && period == 0 ? logDerivSum : double.PositiveInfinity;
        if (n >= _refZr.Length) GrowRefOrbit(n, maxIter);   // #1828: the final Z_n
        if (n == maxIter || period > 0) { zrOd = zr.ToOD(); ziOd = zi.ToOD(); }
        StoreRefOd(n, zrOd, ziOd);
        TrimRefOrbit(n);   // #1828
        _refOrbitLen = n;
        _refOrbitGen++;

        var d = c.ToDeep();
        _refCxHi = d.Re.X0; _refCxLo = d.Re.X1; _refCx2 = d.Re.X2; _refCx3 = d.Re.X3;
        _refCx4 = d.Re.X4;  _refCx5 = d.Re.X5;  _refCx6 = d.Re.X6;  _refCx7 = d.Re.X7;
        _refCyHi = d.Im.X0; _refCyLo = d.Im.X1; _refCy2 = d.Im.X2; _refCy3 = d.Im.X3;
        _refCy4 = d.Im.X4;  _refCy5 = d.Im.X5;  _refCy6 = d.Im.X6;  _refCy7 = d.Im.X7;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter && period == 0;
        _refPeriod = period;
        _refPeriodZ2 = periodZ2;
        _refPeriodZ2Fx = periodZ2Fx;   // #1854
        _refExact = c;
        _refExactPrec = prec;
        _refCachedTier = 3;  // AP
    }

    private void StoreRefOd(int n, in OD zr, in OD zi)
    {
        _refZr[n] = zr.X0;   _refZrLo[n] = zr.X1;
        _refZrX2[n] = zr.X2; _refZrX3[n] = zr.X3;
        _refZrX4[n] = zr.X4; _refZrX5[n] = zr.X5;
        _refZrX6[n] = zr.X6; _refZrX7[n] = zr.X7;
        _refZi[n] = zi.X0;   _refZiLo[n] = zi.X1;
        _refZiX2[n] = zi.X2; _refZiX3[n] = zi.X3;
        _refZiX4[n] = zi.X4; _refZiX5[n] = zi.X5;
        _refZiX6[n] = zi.X6; _refZiX7[n] = zi.X7;
    }

    /// <summary>#1840 — test / probe access: Z_n of the last reference orbit as its eight
    /// limbs (real, imaginary), and the tier it was built at (3 = AP).</summary>
    public (OD Re, OD Im) ReferenceOrbitPoint(int n) => (
        new OD(_refZr[n], _refZrLo[n], _refZrX2[n], _refZrX3[n], _refZrX4[n], _refZrX5[n], _refZrX6[n], _refZrX7[n]),
        new OD(_refZi[n], _refZiLo[n], _refZiX2[n], _refZiX3[n], _refZiX4[n], _refZiX5[n], _refZiX6[n], _refZiX7[n]));

    /// <summary>#1840 — the precision tier of the cached reference orbit (0 DD, 1 QD, 2 OD,
    /// 3 AP; -1 none).</summary>
    public int ReferenceOrbitTier => _refCachedTier;

    // Full-OD per-pixel fallback (used for PT glitches at zoom > 1e50).
    // Mirrors ComputePixelQD line-by-line, but uses OD math so pixel offsets
    // at the ~10⁻⁵² scale (where QD precision starts to fail) remain
    // distinguishable. Wave 2.11.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelOD<TMap>(OD cx, OD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        if (IsInMainCardioidOrBulb(cx.X0, cy.X0))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        OD zr = OD.Zero, zi = OD.Zero;
        double dr = 1.0, di = 0.0;
        int iterStart = 0;

        // SA prelude — mirror of ComputePixelQD with OD dc subtraction.
        var sa = _sa;
        if (sa != null && sa.SafeMax >= 16 && !DisableAcceleration && !DisableSeriesApproximation)
        {
            OD refCx = new OD(_refCxHi, _refCxLo, _refCx2, _refCx3,
                              _refCx4, _refCx5, _refCx6, _refCx7);
            OD refCy = new OD(_refCyHi, _refCyLo, _refCy2, _refCy3,
                              _refCy4, _refCy5, _refCy6, _refCy7);
            OD dcRod = cx - refCx;
            OD dcIod = cy - refCy;
            double dcR = dcRod.X0;
            double dcI = dcIod.X0;
            int k = sa.FindSkip(dcR, dcI, SaTolerance, maxIter - 1);
            if (k >= 16 && k <= _refOrbitLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                sa.EvalDDelta(k, dcR, dcI, out double ddR, out double ddI);
                // Seed from full OD reference orbit — at this depth pixel
                // offsets live in X4..X7 of ref_k, dropping them would
                // collapse near-centre pixels exactly the same way QD-vs-DD
                // does at zoom > 1e32.
                OD refZk = new OD(_refZr[k], _refZrLo[k], _refZrX2[k], _refZrX3[k],
                                  _refZrX4[k], _refZrX5[k], _refZrX6[k], _refZrX7[k]);
                OD refIk = new OD(_refZi[k], _refZiLo[k], _refZiX2[k], _refZiX3[k],
                                  _refZiX4[k], _refZiX5[k], _refZiX6[k], _refZiX7[k]);
                zr = refZk + new OD(dR);
                zi = refIk + new OD(dI);
                dr = ddR;
                di = ddI;
                iterStart = k;
            }
        }

        double zrSnap0 = 0, zrSnap1 = 0, ziSnap0 = 0, ziSnap1 = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = iterStart; iter < maxIter; iter++)
        {
            double zrH = zr.X0, ziH = zi.X0;
            if (zrH * zrH + ziH * ziH >= EscapeRadius2) break;

            double newDr = 2.0 * (zrH * dr - ziH * di) + 1.0;
            double newDi = 2.0 * (zrH * di + ziH * dr);
            dr = newDr; di = newDi;

            OD newZi = (zr * zi) * 2.0 + cy;
            OD newZr = zr.Square() - zi.Square() + cx;
            zr = newZr; zi = newZi;

            if (zr.X0 == zrSnap0 && zr.X1 == zrSnap1
                && zi.X0 == ziSnap0 && zi.X1 == ziSnap1)
            { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap0 = zr.X0; zrSnap1 = zr.X1;
                ziSnap0 = zi.X0; ziSnap1 = zi.X1;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr.X0, zi.X0, dr, di, colorMap);
    }

    // Returns false when the reference orbit was exhausted before this pixel
    // escaped (glitch condition) — caller must fall back to ComputePixelHP.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ComputePixelPT<TMap>(
        double dcR, double dcI, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        double dr = 0.0, di = 0.0;    // δ_0 = 0
        double drv = 1.0, div = 0.0;  // dz/dc for surface normals (IQ convention)
        int refLen = _refOrbitLen;
        double escZr = 0.0, escZi = 0.0;
        int iter;

        for (iter = 0; iter < maxIter; iter++)
        {
            if (iter > refLen) return false;  // reference exhausted → glitch

            double Zr = _refZr[iter];
            double Zi = _refZi[iter];
            double zr = Zr + dr;
            double zi = Zi + di;

            if (zr * zr + zi * zi >= EscapeRadius2)
            {
                escZr = zr; escZi = zi;
                break;
            }

            // Precision glitch: dr/di absorbed by Z in double arithmetic
            // (|dc| << ULP(Z) at extreme zoom). All pixels share identical
            // escape check → wrong iteration counts. Fall back to DD or QD.
            if (zr == Zr && zi == Zi && (dr != 0.0 || di != 0.0))
                return false;

            double newDrv = 2.0 * (zr * drv - zi * div) + 1.0;
            double newDiv = 2.0 * (zr * div + zi * drv);
            drv = newDrv; div = newDiv;

            // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
            double a = 2.0 * Zr + dr;
            double b = 2.0 * Zi + di;
            double newDr = a * dr - b * di + dcR;
            double newDi = a * di + b * dr + dcI;
            dr = newDr; di = newDi;
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, escZr, escZi, drv, div, colorMap);
        return true;
    }

    // SM-2 — rebasing perturbation (Zhuoran). Always resolves a pixel in double
    // precision, at any zoom, from the single shared reference orbit — the
    // glitch-free replacement for the per-pixel QD/OD/HP fallback. Reference
    // index m advances independently of the iteration count; z = Z[m] + δ is
    // reconstructed each step for the escape test, and when the full value is
    // smaller than the perturbation (reference no longer a good anchor) or the
    // reference runs out, the reference is restarted from Z[0]=0 with δ := z.
    // Because Z[0]=0, the step right after a rebase is δ' = δ² + dc — the exact
    // non-perturbative step for the full value δ now carries. Escape count is
    // the global iteration n, not m. No SA prelude: this is the correctness
    // fallback for lanes the SIMD path could not carry, so it favours a simple
    // exact chain over the SA skip (the SIMD attempt already paid the SA cost).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelPTRebased<TMap>(
        double colOffsetX, double rowOffsetY, double scale,
        int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        if (UseDdRebaseReference)
        {
            ComputePixelPTRebasedDD(colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
            return;
        }

        int refLen = _refOrbitLen;
        if (refLen < 1)
        {
            // No usable reference orbit — treat as interior (matches the
            // degenerate case the QD/OD fallbacks would also colour flat).
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        // The δ-chain runs in plain DOUBLE. A DD δ-chain + DD reference + DD dc
        // was measured against the per-pixel QD path and produced byte-identical
        // iteration counts (--rebaseprobe): precision does NOT change the result
        // at these depths. The apparent ~50 % divergence from the QD render is
        // chaotic sensitivity of deep filamentary regions at high iteration
        // count — the QD path disagrees with ITSELF (SA-off vs SA-on) by the
        // same ~50 % (QDself ≈ reb-vs-QD). So double rebasing is as accurate as
        // QD here, at ~100× the speed. dc is a single-rounded double for the
        // same reason (the exact low word buys nothing).
        double dcR = colOffsetX * scale + _refRecycleDx;
        double dcI = rowOffsetY * scale + _refRecycleDy;

        // δ_0 = 0, dz/dc = 1 (IQ convention), reference index m = 0.
        ResumePixelPTRebased(dcR, dcI, 0, 0, 0.0, 0.0, 1.0, 0.0, null, maxIter, idx, colorMap);
    }

    // #1613 — the rebased loop from any state: iteration iter0 at reference index
    // m0 with perturbation δ and full-orbit derivative. ComputePixelPTRebased starts
    // it from zero without BLA (the #1274 specification the 4-wide kernel matches
    // bit for bit). The QD-band PT4 / PT8 rows hand a pixel over here when it trips
    // the rebase test (|z| < |δ|), continuing from where it is instead of restarting,
    // and pass their BLA table: skips are looked up by the reference index m, not the
    // iteration count, so they keep working after a rebase. Rebasing combined with BLA
    // as in Zhuoran, "Another solution to perturbation glitches" (fractalforums.org,
    // 2021). A step never runs past refLen − 1 (the rebase test's own limit) nor past
    // maxIter.
    private void ResumePixelPTRebased<TMap>(
        double dcR, double dcI, int iter0, int m0,
        double dr, double di, double drv, double div,
        BlaTable? bla, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        int refLen = _refOrbitLen;
        int m = m0;                   // reference-orbit index (independent of iter)
        double zr = 0.0, zi = 0.0;    // full value z = Z[m] + δ (last is escape z)
        // #1803 — PeriodicAtRebase: the state right after a rebase (δ := z, m = 0),
        // snapshotted at doubling rebase counts. With BLA the skips' limit
        // min(refLen − 1, m + (maxIter − iter)) is fixed between two rebases at
        // maxIter − (the rebase's iteration), so the check needs maxIter − iter ≥ refLen.
        bool periodicity = DeepPeriodicity;
        double snapR = 0, snapI = 0;
        bool haveSnap = false;
        int rebases = 0, nextSnap = 1;

        int iter;
        for (iter = iter0; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                int blaIdx = bla.Lookup(m, dr * dr + di * di, Math.Min(refLen - 1, m + (maxIter - iter)));
                if (blaIdx >= 0)
                {
                    ref readonly var e = ref bla.Data[blaIdx];
                    if (e.L >= 2)
                    {
                        double aRe = e.ARe, aIm = e.AIm, bRe = e.BRe, bIm = e.BIm;
                        // δ' = A·δ + B·dc, so the derivative d' = A·d + B (#1782: A and B
                        // don't depend on dc; B was dropped before, as in the PT4 rows).
                        double nDr = aRe * dr - aIm * di + (bRe * dcR - bIm * dcI);
                        double nDi = aRe * di + aIm * dr + (bRe * dcI + bIm * dcR);
                        dr = nDr; di = nDi;
                        double nDrv = aRe * drv - aIm * div + bRe;
                        double nDiv = aRe * div + aIm * drv + bIm;
                        drv = nDrv; div = nDiv;
                        m += e.L;
                        iter += e.L - 1;   // the loop's ++ makes it +L
                        continue;
                    }
                }
            }

            double Zr = _refZr[m];
            double Zi = _refZi[m];
            zr = Zr + dr;
            zi = Zi + di;

            double zmag2 = zr * zr + zi * zi;
            if (zmag2 >= EscapeRadius2) break;

            // Derivative of the FULL orbit — independent of rebasing.
            double newDrv = 2.0 * (zr * drv - zi * div) + 1.0;
            double newDiv = 2.0 * (zr * div + zi * drv);
            drv = newDrv; div = newDiv;

            // Rebase when the reference no longer anchors this pixel (full value
            // below the perturbation magnitude) or the reference is exhausted.
            double dmag2 = dr * dr + di * di;
            if (zmag2 < dmag2 || m + 1 >= refLen)
            {
                dr = zr; di = zi;      // δ := z, relative to the restarted Z[0]=0
                Zr = 0.0; Zi = 0.0;
                m = 0;
                if (periodicity && (bla == null || maxIter - iter >= refLen))
                {
                    if (haveSnap && SameRebaseState(dr, di, snapR, snapI))
                    {
                        Interlocked.Increment(ref _deepPeriodicPixels);
                        LogCaught(idx, 1, 1);
                        iter = maxIter;
                        break;
                    }
                    if (++rebases >= nextSnap) { snapR = dr; snapI = di; haveSnap = true; nextSnap = rebases * 2; }
                }
            }

            // δ_{n+1} = (2·Z[m] + δ)·δ + dc  (Z[m]=0 right after a rebase).
            double a = 2.0 * Zr + dr;
            double b = 2.0 * Zi + di;
            double newDr = a * dr - b * di + dcR;
            double newDi = a * di + b * dr + dcI;
            dr = newDr; di = newDi;
            m++;
        }

        Interlocked.Increment(ref _ptRebasedPixels);
        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr, zi, drv, div, colorMap);
    }

    // ── #1842 (epic #1428, C5a) — floatexp pixels past double's range ─────────────
    // Each pixel runs the rebased loop (ResumePixelPTRebased, without BLA) with δ, dc and
    // the full-orbit derivative in FloatExp; Z_n comes from the reference orbit as double
    // (the C4 BigFloat orbit's head limb). The pixel scale is 10^-ZoomLog10 · 3.5 / max(W, H)
    // from the log, so it holds at any depth. No BLA / SA / LA / SIMD yet (later C5 parts).
    private void CalculateFloatExpRows<TMap>(TMap colorMap, int maxIt, CancellationToken ct)
        where TMap : IColorMap
    {
        int effImgW = EffectiveImageWidth, effImgH = EffectiveImageHeight;
        var scale = FloatExp.FromLog10(Math.Log10(3.5 / Math.Max(effImgW, effImgH)) - ZoomLog10);
        double halfW = effImgW * 0.5, halfH = effImgH * 0.5;
        // #1845 — the floatexp BLA table, for the frame's largest |dc|.
        var bla = DisableAcceleration ? null
            : EnsureBlaTableFx(scale * (Math.Sqrt(halfW * halfW + halfH * halfH)
                + Math.Sqrt(PixelJitter.X * PixelJitter.X + PixelJitter.Y * PixelJitter.Y)));   // #1851: + the jitter
        _blaSkipsTotal = 0; _blaIterSkippedTotal = 0; _ptStepsTotal = 0;
        // #1847 — the LA stages where the double path would use them (LaWanted: Auto from
        // its crossover zoom, so past double by default), with the same gates as LaActive.
        var laFx = LaWanted && !DisableAcceleration && AllowPtRebasing && !UseDdRebaseReference
            ? EnsureLaTableFx() : null;
        if (laFx is { Finite: false }) laFx = null;
        var laTail = LaTailBla ? bla : null;
        _lastFrameUsedLa = laFx != null;
        _laPixelsTotal = _laStepsTotal = _laPlainTotal = _laBlaTotal = 0;
        _laAtStepsTotal = _laAtPixelsTotal = 0;
        _fxDoubleStepsTotal = 0; _fxDoubleClimbs = 0; _fxDoubleRenorms = 0;   // #1889
        // #1886 (C5g) — the AT over the float-exp table, for the view's half diagonal.
        SelectLaAtFx(laFx, scale * (Math.Sqrt(halfW * halfW + halfH * halfH)
            + Math.Sqrt(PixelJitter.X * PixelJitter.X + PixelJitter.Y * PixelJitter.Y)));
        var atFx = _laAtFx;
        int offX = SubRectOffsetX, offY = SubRectOffsetY;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        _po.CancellationToken = ct;
        int w = Width;
        long computed = 0;
        ParallelForRows(0, Height, _po, y =>
        {
            if (ct.IsCancellationRequested || !RowMarked(y)) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            int rowBase = y * w;
            var dcI = scale * (offY + y - halfH + PixelJitter.Y);   // #1851: the pixel jitter
            long rowSkips = 0, rowSaved = 0, rowSteps = 0;
            long laSteps = 0, laPlain = 0, laBla = 0, laPx = 0, atSteps = 0, atPx = 0, dblSteps = 0;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                if (!Marked(idx)) continue;
                if (laFx != null)
                {
                    EvaluateLaFx(laFx, atFx, laTail, scale * (offX + x - halfW + PixelJitter.X), dcI, rowMaxIt, idx, colorMap,
                        ref laSteps, ref laPlain, ref laBla, ref atSteps, ref atPx, ref dblSteps);
                    laPx++;
                }
                else
                    ComputePixelPTFx(scale * (offX + x - halfW + PixelJitter.X), dcI, rowMaxIt, idx, colorMap, bla,
                        ref rowSkips, ref rowSaved, ref rowSteps);
            }
            if (laPx > 0)
            {
                Interlocked.Add(ref _laPixelsTotal, laPx);
                Interlocked.Add(ref _laStepsTotal, laSteps);
                Interlocked.Add(ref _laPlainTotal, laPlain);
                Interlocked.Add(ref _laBlaTotal, laBla);
                Interlocked.Add(ref _laAtStepsTotal, atSteps);   // #1886
                Interlocked.Add(ref _laAtPixelsTotal, atPx);
                Interlocked.Add(ref _fxDoubleStepsTotal, dblSteps);   // #1889
            }
            Interlocked.Add(ref _blaSkipsTotal, rowSkips);
            Interlocked.Add(ref _blaIterSkippedTotal, rowSaved);
            Interlocked.Add(ref _ptStepsTotal, rowSteps);
            Interlocked.Add(ref computed, w);
            if (rowMaxIt < maxIt)
                for (int x = 0; x < w; x++)
                    if (IterationBuffer[rowBase + x] >= rowMaxIt) IterationBuffer[rowBase + x] = maxIt;
        });
        LastFrameComputedPixels += computed;
    }

    // #1845 — the floatexp BLA table, kept while the orbit, ε and dcMax (within 5%, as
    // EnsureBlaTable) are the same.
    private BlaTableFx? _blaFx;
    private int _blaFxGen = -1;

    private BlaTableFx? EnsureBlaTableFx(FloatExp dcMaxAbs)
    {
        if (_refOrbitLen < 4) return _blaFx = null;
        double eps = BlaTolerance > 0 && double.IsFinite(BlaTolerance) ? BlaTolerance : BlaTable.DefaultEpsilon;
        if (_blaFx != null && _blaFxGen == _refOrbitGen && _blaFx.RefLen == _refOrbitLen && _blaFx.Epsilon == eps
            && Math.Abs((_blaFx.DcMaxAbs / dcMaxAbs).ToDouble() - 1) < 0.05)
            return _blaFx;
        _blaFx = new BlaTableFx(_refZr, _refZi, _refOrbitLen, dcMaxAbs, eps);
        _blaFxGen = _refOrbitGen;
        return _blaFx;
    }

    /// <summary>#1842 — one pixel of the rebased perturbation loop in FloatExp (see
    /// <see cref="ResumePixelPTRebased"/> for the double form and its sources).</summary>
    private void ComputePixelPTFx<TMap>(FloatExp dcR, FloatExp dcI, int maxIter, int idx, TMap colorMap,
        BlaTableFx? bla, ref long skips, ref long saved, ref long steps)
        where TMap : IColorMap
    {
        int refLen = _refOrbitLen;
        if (refLen < 1)
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }
        FloatExp dr = FloatExp.Zero, di = FloatExp.Zero;          // δ
        FloatExp drv = FloatExp.One, div = FloatExp.Zero;         // dz/dc of the full orbit
        double zrD = 0, ziD = 0;
        int m = 0, iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                // #1845 — a BLA skip by reference index (as ResumePixelPTRebased):
                // δ' = A·δ + B·dc, the derivative d' = A·d + B (#1782).
                int k = bla.Lookup(m, FloatExp.Norm2(dr, di), Math.Min(refLen - 1, m + (maxIter - iter)));
                if (k >= 0)
                {
                    ref readonly var e = ref bla.Data[k];
                    if (e.L >= 2)
                    {
                        var nDr0 = e.ARe * dr - e.AIm * di + (e.BRe * dcR - e.BIm * dcI);
                        var nDi0 = e.ARe * di + e.AIm * dr + (e.BRe * dcI + e.BIm * dcR);
                        dr = nDr0; di = nDi0;
                        var nDrv0 = e.ARe * drv - e.AIm * div + e.BRe;
                        var nDiv0 = e.ARe * div + e.AIm * drv + e.BIm;
                        drv = nDrv0; div = nDiv0;
                        m += e.L;
                        iter += e.L - 1;
                        skips++; saved += e.L;
                        continue;
                    }
                }
            }
            steps++;
            double Zr = _refZr[m], Zi = _refZi[m];
            FloatExp zr = dr + Zr, zi = di + Zi;                  // z = Z[m] + δ
            zrD = zr.ToDouble(); ziD = zi.ToDouble();
            if (zrD * zrD + ziD * ziD >= EscapeRadius2) break;

            FloatExp nDrv = (zr * drv - zi * div).ScaleB(1) + 1.0;
            FloatExp nDiv = (zr * div + zi * drv).ScaleB(1);
            drv = nDrv; div = nDiv;

            // Rebase (Zhuoran) when |z| < |δ| or the reference is exhausted.
            if (FloatExp.AbsLess(FloatExp.Norm2(zr, zi), FloatExp.Norm2(dr, di)) || m + 1 >= refLen)
            {
                dr = zr; di = zi;
                Zr = 0; Zi = 0;
                m = 0;
            }

            // δ' = (2·Z[m] + δ)·δ + dc
            FloatExp a = dr + 2.0 * Zr, b = di + 2.0 * Zi;
            FloatExp nDr = a * dr - b * di + dcR;
            FloatExp nDi = a * di + b * dr + dcI;
            dr = nDr; di = nDi;
            m++;
        }
        Interlocked.Increment(ref _ptRebasedPixels);
        IterationBuffer[idx] = iter;
        // The derivative is ~1/pixel: past double it reaches the colouring as ±∞, where the
        // distance is 0 and the normals 0, as the double path's already are well before this
        // depth (normals past ~1e150, distance past ~1e40: #1843).
        FillAuxAndColorHP(idx, iter, maxIter, zrD, ziD, drv, div, colorMap);   // #1843: FloatExp derivative
    }

    // SM-11a — DOUBLE-DOUBLE-reference variant of ComputePixelPTRebased. Same
    // Zhuoran rebasing, but the reference orbit Z[m] and the δ-chain carry a DD
    // (Hi+Lo) so recentring reproduces the same iteration count for a fixed world
    // point (centre-invariance). dc is captured exactly from the two source
    // doubles via a DD multiply. Selected by UseDdRebaseReference; ~4-8× the
    // double loop cost, so intended to gate on deep zoom once --navrepro confirms
    // it drives focus-err → 0. Mirror of the double loop line-for-line — keep the
    // two in sync if either changes. See Docs/Deep-Zoom-Perturbation.md.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelPTRebasedDD<TMap>(
        double colOffsetX, double rowOffsetY, double scale,
        int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        int refLen = _refOrbitLen;
        if (refLen < 1)
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        // dc as DD, exact product of the two source doubles (DD*double keeps the
        // low word that colOffsetX*scale would round away).
        DD dcR = new DD(scale) * colOffsetX + _refRecycleDx;
        DD dcI = new DD(scale) * rowOffsetY + _refRecycleDy;

        DD dr = new DD(0.0), di = new DD(0.0);   // δ_0 = 0
        double drv = 1.0, div = 0.0;             // derivative stays double (normals)
        int m = 0;
        double zrHi = 0.0, ziHi = 0.0;           // full-value Hi (last = escape z)
        // #1803 — PeriodicAtRebase (ResumePixelPTRebased), on the full DD δ.
        bool periodicity = DeepPeriodicity;
        DD snapR = default, snapI = default;
        bool haveSnap = false;
        int rebases = 0, nextSnap = 1;

        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            DD Zr = new DD(_refZr[m], _refZrLo[m]);
            DD Zi = new DD(_refZi[m], _refZiLo[m]);
            DD zr = Zr + dr;      // z = Z[m] + δ   (DD)
            DD zi = Zi + di;
            zrHi = zr.Hi; ziHi = zi.Hi;

            double zmag2 = zr.Hi * zr.Hi + zi.Hi * zi.Hi;
            if (zmag2 >= EscapeRadius2) break;

            double newDrv = 2.0 * (zr.Hi * drv - zi.Hi * div) + 1.0;
            double newDiv = 2.0 * (zr.Hi * div + zi.Hi * drv);
            drv = newDrv; div = newDiv;

            // Rebase when the full value drops below the perturbation magnitude
            // (reference no longer anchors) or the reference is exhausted.
            double dmag2 = dr.Hi * dr.Hi + di.Hi * di.Hi;
            if (zmag2 < dmag2 || m + 1 >= refLen)
            {
                dr = zr; di = zi;               // δ := z (full DD kept)
                Zr = new DD(0.0); Zi = new DD(0.0);
                m = 0;
                if (periodicity)
                {
                    if (haveSnap && SameRebaseState(dr, di, snapR, snapI))
                    {
                        Interlocked.Increment(ref _deepPeriodicPixels);
                        LogCaught(idx, 1, 1);
                        iter = maxIter;
                        break;
                    }
                    if (++rebases >= nextSnap) { snapR = dr; snapI = di; haveSnap = true; nextSnap = rebases * 2; }
                }
            }

            // δ_{n+1} = (2·Z[m] + δ)·δ + dc   (Z[m]=0 right after a rebase).
            DD a = Zr * 2.0 + dr;
            DD b = Zi * 2.0 + di;
            DD newDr = a * dr - b * di + dcR;
            DD newDi = a * di + b * dr + dcI;
            dr = newDr; di = newDi;
            m++;
        }

        Interlocked.Increment(ref _ptRebasedPixels);
        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zrHi, ziHi, drv, div, colorMap);
    }

    // #1274 — the rebased fallback, 4 pixels at a time (AVX2). Glitched lanes of
    // the PT4 / PT8 rows queue here (RebaseQueue) instead of running
    // ComputePixelPTRebased one by one; past ~1e30 that is nearly every pixel.
    // Each lane carries its own pixel through the exact scalar loop: its own
    // reference index m, rebase test and escape test, the same operations in the
    // same order (no FMA contraction), so every lane's result is bit-identical to
    // ComputePixelPTRebased for that pixel. A lane whose pixel escapes or reaches
    // maxIter is refilled with the next queued pixel, so a long-iterating pixel
    // never holds three idle lanes. Two such groups run interleaved per thread
    // (each step is one long dependency chain; the core overlaps the two), and
    // 2·x is computed as x + x (the same IEEE result, off the multiply latency).
    // Measured at 3e47 (i5-13420H): 2.7x per performance core, 1.1x per
    // efficiency core (128-bit units split the 256-bit ops), ~1.9x a frame on
    // all 12 threads. AVX-512 rows (PT8) use the same 4-wide loop.
    // UseSimdRebasing = false restores the scalar fallback (A/B, #1274 tests);
    // the DD-reference variant (SM-11a) stays scalar.
    public static bool UseSimdRebasing { get; set; } = true;

    // #1613 — in the QD band with BLA on, a pixel that trips the rebase test in the
    // PT4 / PT8 rows continues from its state with BLA (ResumePixelPTRebased, at
    // QdBandBlaTolerance) instead of restarting in the queue above. False restores the
    // absorption test and restart (the #1274 tests pin it, so the 4-wide kernel keeps
    // its bit-for-bit check against the scalar loop on the frames it was measured on).
    public static bool ResumeRebased { get; set; } = true;

    [ThreadStatic] private static double[]? t_rebaseCol;
    [ThreadStatic] private static int[]? t_rebaseIdx;

    /// <summary>#1274 — this thread's queue of glitched pixels for one row: column
    /// offsets (image-centred, as ComputePixelPTRebased takes them) and buffer indices.</summary>
    private static (double[] Col, int[] Idx) RebaseQueue(int width)
    {
        if (t_rebaseCol is not { } col || col.Length < width)
        {
            t_rebaseCol = col = new double[width];
            t_rebaseIdx = new int[width];
        }
        return (col, t_rebaseIdx!);
    }

    // 4-bit lane mask → all-ones lanes, for the double blends and the int32 index blend.
    private static readonly Vector256<double>[] s_laneMaskD = BuildLaneMasksD();
    private static readonly Vector128<int>[] s_laneMaskI = BuildLaneMasksI();
    private static Vector256<double>[] BuildLaneMasksD()
    {
        var t = new Vector256<double>[16];
        for (int i = 0; i < 16; i++) t[i] = MaskToVector(i);
        return t;
    }
    private static Vector128<int>[] BuildLaneMasksI()
    {
        var t = new Vector128<int>[16];
        for (int i = 0; i < 16; i++)
            t[i] = Vector128.Create((i & 1) != 0 ? -1 : 0, (i & 2) != 0 ? -1 : 0, (i & 4) != 0 ? -1 : 0, (i & 8) != 0 ? -1 : 0);
        return t;
    }

    // #1274 — one group of 4 lanes of the vector rebased loop: each lane's δ, dz/dc,
    // dc, reference index m, iteration count and queue position.
    private unsafe struct RebaseQuad
    {
        public Vector256<double> DcR, Dr, Di, Drv, Div;
        public Vector128<int> M, It;
        public int Active;
        public fixed int Pix[4];
        // #1803 — per lane: the PeriodicAtRebase snapshot (δ after a rebase), whether it
        // is set, and the rebase count / next snapshot count.
        public fixed double SnapR[4];
        public fixed double SnapI[4];
        public fixed int Rebases[4];
        public fixed int NextSnap[4];
        public int HaveSnap;
    }

    // Loop-invariant inputs of the vector rebased loop.
    private readonly unsafe struct RebaseInputs
    {
        public readonly double* Zr, Zi;
        public readonly double[] Cols;
        public readonly int[] Idxs;
        public readonly int Count, MaxIter;
        public readonly double Scale, RecycleDx;
        public readonly Vector256<double> Er2, DcI;
        public readonly Vector128<int> RefLast2, Cap;
        public readonly bool Periodicity;   // #1803

        public RebaseInputs(double* zr, double* zi, double[] cols, int[] idxs, int count, int maxIter,
            double scale, double recycleDx, double er2, double dcI, int refLen, bool periodicity)
        {
            Periodicity = periodicity;
            Zr = zr; Zi = zi; Cols = cols; Idxs = idxs; Count = count; MaxIter = maxIter;
            Scale = scale; RecycleDx = recycleDx;
            Er2 = Vector256.Create(er2); DcI = Vector256.Create(dcI);
            RefLast2 = Vector128.Create(refLen - 2);  // m > refLen - 2: reference exhausted
            Cap = Vector128.Create(maxIter - 1);      // it > maxIter - 1: iteration cap reached
        }
    }

    private unsafe void ComputePixelsPTRebased4<TMap>(
        double[] cols, int[] idxs, int count, double rowOffsetY, double scale,
        int maxIter, TMap colorMap)
        where TMap : IColorMap
    {
        int refLen = _refOrbitLen;
        if (refLen < 1 || maxIter <= 0 || !Avx2.IsSupported)
        {
            for (int p = 0; p < count; p++)
                ComputePixelPTRebased(cols[p], rowOffsetY, scale, maxIter, idxs[p], colorMap);
            return;
        }

        fixed (double* pZr = _refZr, pZi = _refZi)
        {
            // dc exactly as ComputePixelPTRebased forms it, per pixel.
            var inp = new RebaseInputs(pZr, pZi, cols, idxs, count, maxIter, scale, _refRecycleDx,
                EscapeRadius2, rowOffsetY * scale + _refRecycleDy, refLen, DeepPeriodicity);
            // Two independent groups per thread: each iteration is one long
            // dependency chain, and the core overlaps the two.
            RebaseQuad qa = default, qb = default;
            int next = 0;
            for (int k = 0; k < 4; k++) LoadLane(ref qa, k, ref next, in inp);
            for (int k = 0; k < 4; k++) LoadLane(ref qb, k, ref next, in inp);
            while ((qa.Active | qb.Active) != 0)
            {
                if (qa.Active != 0) RebaseStep(ref qa, ref next, in inp, colorMap);
                if (qb.Active != 0) RebaseStep(ref qb, ref next, in inp, colorMap);
            }
        }

        Interlocked.Add(ref _ptRebasedPixels, count);
    }

    /// <summary>Start lane <paramref name="k"/> on the next queued pixel (δ_0 = 0,
    /// dz/dc = 1, m = 0), or retire it (zeros) when the queue is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void LoadLane(ref RebaseQuad q, int k, ref int next, in RebaseInputs inp)
    {
        if (next < inp.Count)
        {
            q.Pix[k] = next;
            q.DcR = q.DcR.WithElement(k, inp.Cols[next] * inp.Scale + inp.RecycleDx);
            q.Active |= 1 << k;
            next++;
        }
        else
        {
            q.Active &= ~(1 << k);
            q.DcR = q.DcR.WithElement(k, 0.0);
        }
        q.Dr = q.Dr.WithElement(k, 0.0); q.Di = q.Di.WithElement(k, 0.0);
        q.Drv = q.Drv.WithElement(k, 1.0); q.Div = q.Div.WithElement(k, 0.0);
        q.M = q.M.WithElement(k, 0); q.It = q.It.WithElement(k, 0);
        q.HaveSnap &= ~(1 << k); q.Rebases[k] = 0; q.NextSnap[k] = 1;   // #1803
    }

    /// <summary>One iteration of ComputePixelPTRebased on 4 lanes, operation for
    /// operation; finished lanes are resolved and refilled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void RebaseStep<TMap>(ref RebaseQuad q, ref int next, in RebaseInputs inp, TMap colorMap)
        where TMap : IColorMap
    {
        var one = Vector256.Create(1.0);
        var oneI = Vector128.Create(1);
        var dr = q.Dr; var di = q.Di; var drv = q.Drv; var div = q.Div; var m = q.M;

        // Every lane's m stays in [0, refLen): an exhausted reference rebases to 0
        // below on every lane, live or idle, before m advances.
        var Zr = Avx2.GatherVector256(inp.Zr, m, 8);
        var Zi = Avx2.GatherVector256(inp.Zi, m, 8);
        var zr = Avx.Add(Zr, dr);
        var zi = Avx.Add(Zi, di);
        var zmag2 = Avx.Add(Avx.Multiply(zr, zr), Avx.Multiply(zi, zi));

        int finished = Avx.MoveMask(Avx.Compare(zmag2, inp.Er2,
            FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling)) & q.Active;
        if (finished != 0)
        {
            // Escape: z of this iteration, derivative before its update.
            for (int k = 0; k < 4; k++)
                if ((finished >> k & 1) != 0)
                    Resolve(inp.Idxs[q.Pix[k]], q.It.GetElement(k), inp.MaxIter,
                        zr.GetElement(k), zi.GetElement(k), drv.GetElement(k), div.GetElement(k), colorMap);
        }

        // 2·x as x + x: the same IEEE result, one fewer multiply on the chain.
        var tDrv = Avx.Subtract(Avx.Multiply(zr, drv), Avx.Multiply(zi, div));
        var tDiv = Avx.Add(Avx.Multiply(zr, div), Avx.Multiply(zi, drv));
        var newDrv = Avx.Add(Avx.Add(tDrv, tDrv), one);
        var newDiv = Avx.Add(tDiv, tDiv);
        drv = newDrv; div = newDiv;

        // Rebase where the full value is below the perturbation or the reference
        // is exhausted. Lanes that just finished are refilled below and idle
        // lanes carry zeros, so neither needs masking.
        var dmag2 = Avx.Add(Avx.Multiply(dr, dr), Avx.Multiply(di, di));
        int rebase = Avx.MoveMask(Avx.Compare(zmag2, dmag2, FloatComparisonMode.OrderedLessThanNonSignaling))
                   | Sse.MoveMask(Sse2.CompareGreaterThan(m, inp.RefLast2).AsSingle());   // m + 1 >= refLen
        if (rebase != 0)
        {
            // δ := z, relative to the restarted Z[0] = 0.
            var mask = s_laneMaskD[rebase];
            dr = Avx.BlendVariable(dr, zr, mask);
            di = Avx.BlendVariable(di, zi, mask);
            Zr = Avx.BlendVariable(Zr, Vector256<double>.Zero, mask);
            Zi = Avx.BlendVariable(Zi, Vector256<double>.Zero, mask);
            m = Sse41.BlendVariable(m, Vector128<int>.Zero, s_laneMaskI[rebase]);

            // #1803 — PeriodicAtRebase per live lane (no BLA here, so no cap condition):
            // a lane back at a snapshotted post-rebase δ cycles for ever: in set.
            int live = rebase & q.Active & ~finished;
            if (inp.Periodicity && live != 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    if ((live >> k & 1) == 0) continue;
                    double r = dr.GetElement(k), i = di.GetElement(k);
                    if ((q.HaveSnap >> k & 1) != 0 && SameRebaseState(r, i, q.SnapR[k], q.SnapI[k]))
                    {
                        Interlocked.Increment(ref _deepPeriodicPixels);
                        int pix = inp.Idxs[q.Pix[k]];
                        LogCaught(pix, 1, 1);
                        Resolve(pix, inp.MaxIter, inp.MaxIter, zr.GetElement(k), zi.GetElement(k),
                            drv.GetElement(k), div.GetElement(k), colorMap);
                        finished |= 1 << k;
                        continue;
                    }
                    if (++q.Rebases[k] >= q.NextSnap[k])
                    {
                        q.SnapR[k] = r; q.SnapI[k] = i; q.HaveSnap |= 1 << k;
                        q.NextSnap[k] = q.Rebases[k] * 2;
                    }
                }
            }
        }

        // δ_{n+1} = (2·Z[m] + δ)·δ + dc  (Z[m] = 0 right after a rebase).
        var a = Avx.Add(Avx.Add(Zr, Zr), dr);
        var b = Avx.Add(Avx.Add(Zi, Zi), di);
        q.Dr = Avx.Add(Avx.Subtract(Avx.Multiply(a, dr), Avx.Multiply(b, di)), q.DcR);
        q.Di = Avx.Add(Avx.Add(Avx.Multiply(a, di), Avx.Multiply(b, dr)), inp.DcI);
        q.Drv = drv; q.Div = div;
        q.M = Sse2.Add(m, oneI);
        var it = Sse2.Add(q.It, oneI);
        q.It = it;

        int capped = Sse.MoveMask(Sse2.CompareGreaterThan(it, inp.Cap).AsSingle()) & q.Active & ~finished;
        if (capped != 0)
        {
            // In set: z of the last iteration, derivative after its update.
            for (int k = 0; k < 4; k++)
                if ((capped >> k & 1) != 0)
                    Resolve(inp.Idxs[q.Pix[k]], inp.MaxIter, inp.MaxIter,
                        zr.GetElement(k), zi.GetElement(k), drv.GetElement(k), div.GetElement(k), colorMap);
            finished |= capped;
        }

        if (finished != 0)
            for (int k = 0; k < 4; k++)
                if ((finished >> k & 1) != 0) LoadLane(ref q, k, ref next, in inp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Resolve<TMap>(int idx, int iter, int maxIter, double zr, double zi, double drv, double div, TMap colorMap)
        where TMap : IColorMap
    {
        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr, zi, drv, div, colorMap);
    }

    // #1614 — a row against a periodic reference (_refPeriod): every pixel runs the
    // rebased loop with BLA by reference index (ResumePixelPTRebased), which wraps at
    // the period. The PT4 / PT8 rows index the reference by iteration count and would
    // run off its end at p + 1. Per pixel, so a lattice range (#1330) is exact.
    private void ComputeRowPeriodic<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        double halfW = EffectiveImageWidth * 0.5, halfH = EffectiveImageHeight * 0.5;
        double dcY = (SubRectOffsetY + y - halfH) * scale + _refRecycleDy;
        var bla = DisableAcceleration ? null : _blaTable;
        for (int x = xBegin; x < xEnd; x++)
        {
            int idx = rowBase + x;
            if (!Marked(idx)) continue;   // #1344
            double dcX = (SubRectOffsetX + x - halfW) * scale + _refRecycleDx;
            ResumePixelPTRebased(dcX, dcY, 0, 0, 0.0, 0.0, 1.0, 0.0, bla, maxIter, idx, colorMap);
        }
    }

    private void ComputeRowPTScalar<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        if (_refPeriod > 0) { ComputeRowPeriodic(y, scale, maxIter, rowBase, colorMap, xBegin, xEnd); return; }   // #1614
        // D-6b — halfH / halfW measure from the FULL image centre, and the
        // per-pixel offset adds SubRectOffsetY/X so dc reflects the pixel's
        // position in image coordinates (not tile-local). Inactive sub-rect
        // mode (ImageHeight == Height && SubRectOffsetY == 0 etc.) collapses
        // to the legacy `(y - Height*0.5) * scale` formula by arithmetic.
        double halfH = EffectiveImageHeight * 0.5;
        double halfW = EffectiveImageWidth  * 0.5;
        int    offX  = SubRectOffsetX;
        int    offY  = SubRectOffsetY;
        double rowOffsetY = offY + y - halfH;
        // Wave 3.5 — _refRecycleDy is the frame's centre shift from the cached
        // reference centre (zero unless the orbit was recycled), so dc is
        // measured from the actual reference orbit. `+ 0.0` on the common path
        // keeps this bit-identical to pre-3.5.
        double dcY = rowOffsetY * scale + _refRecycleDy;
        bool useOD = Zoom > ODZoomThreshold;
        bool useQD = Zoom > QDZoomThreshold && !useOD;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), rowOffsetY, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), rowOffsetY, scale)
            : QD.Zero;
        OD cy_od = useOD
            ? OD.FromCenterOffset(
                new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                       CenterY4, CenterY5, CenterY6, CenterY7),
                rowOffsetY, scale)
            : OD.Zero;

        for (int x = xBegin; x < xEnd; x++)   // #1330: per pixel, so a range is exact
        {
            if (!Marked(rowBase + x)) continue;   // #1344
            double colOffsetX = offX + x - halfW;
            double dcX = colOffsetX * scale + _refRecycleDx;  // Wave 3.5 recycle shift (0 on normal path)
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (AllowPtRebasing)
                    ComputePixelPTRebased(colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
                else if (useOD)
                {
                    OD cx_od = OD.FromCenterOffset(
                        new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                               CenterX4, CenterX5, CenterX6, CenterX7),
                        colOffsetX, scale);
                    ComputePixelOD(cx_od, cy_od, maxIter, idx, colorMap);
                }
                else if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), colOffsetX, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), colOffsetX, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }
    }

    private void ComputeRowPT4<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        // D-6b1 — sub-rect aware. halfW/halfH measure from the FULL
        // image centre; the per-pixel offset adds SubRectOffsetX/Y so
        // dc reflects the pixel's IMAGE position (not tile-local).
        // Inactive sub-rect mode (ImageWidth == Width, SubRectOffsetX
        // == 0, etc.) collapses to the legacy `(x - Width*0.5) * scale`
        // formula by arithmetic — bit-identical to the pre-D-6b1 path.
        double halfW = EffectiveImageWidth  * 0.5;
        double halfH = EffectiveImageHeight * 0.5;
        int    offX  = SubRectOffsetX;
        int    offY  = SubRectOffsetY;
        double rowOffsetY = offY + y - halfH;
        // Wave 3.5 — _refRecycleDy is the frame's centre shift from the cached
        // reference centre (zero unless the orbit was recycled), so dc is
        // measured from the actual reference orbit. `+ 0.0` on the common path
        // keeps this bit-identical to pre-3.5.
        double dcY = rowOffsetY * scale + _refRecycleDy;
        bool useOD = Zoom > ODZoomThreshold;
        bool useQD = Zoom > QDZoomThreshold && !useOD;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), rowOffsetY, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), rowOffsetY, scale)
            : QD.Zero;
        OD cy_od = useOD
            ? OD.FromCenterOffset(
                new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                       CenterY4, CenterY5, CenterY6, CenterY7),
                rowOffsetY, scale)
            : OD.Zero;

        var er2v = Vector256.Create(EscapeRadius2);
        var one = Vector256.Create(1.0);
        var two = Vector256.Create(2.0);
        var dcYv = Vector256.Create(dcY);
        int refLen = _refOrbitLen;
        var bla = DisableAcceleration ? null : _blaTable;
        var sa = (DisableAcceleration || DisableSeriesApproximation) ? null : _sa;
        // #1613 — in the QD band with BLA on, a pixel that trips the rebase test
        // continues in the rebased loop from its state, with BLA (otherwise, and in
        // the DD / OD bands: as before — the absorption test in the QD band and the
        // #1274 restart through the queue / scalar loop).
        bool resume = bla != null && ResumesInQdBand;
        // #1614 — a periodic reference (_refPeriod): the rows index it by m, which
        // wraps at the period (δ += Z_p, the exact rebase at Z_p ≈ 0); without one
        // m == iter, as before.
        int period = _refPeriod;
        bool deepPeriodicity = DeepPeriodicity;   // #1802
        long rowBlaSkips = 0;
        long rowSteps = 0;   // #1614 — plain (non-BLA) vector steps
        long rowBlaIterSaved = 0;
        long rowSaApplied = 0;
        long rowSaIterSaved = 0;
        int x = 0;

        // #1274 — glitched pixels queue for the 4-wide rebased loop at the row end.
        bool queueRebase = AllowPtRebasing && UseSimdRebasing && !UseDdRebaseReference;
        var (rqCol, rqIdx) = queueRebase ? RebaseQueue(Width) : (Array.Empty<double>(), Array.Empty<int>());
        int rqN = 0;

        // Hoisted out of column loop to avoid CA2014 stack growth.
        Span<double> icSpan  = stackalloc double[4];
        Span<double> drSpan  = stackalloc double[4];
        Span<double> diSpan  = stackalloc double[4];
        Span<double> drvSpan = stackalloc double[4];
        Span<double> divSpan = stackalloc double[4];

        // #1330 — LatticeAligned: batches start on lattice multiples (the first may
        // begin left of the range) and run past the range end; lanes outside
        // [xBegin, xEnd) are computed and discarded, so a pixel's batch (and SA /
        // BLA / glitch decision) is the same at every frame offset. No scalar tail.
        bool lattice = LatticeAligned;
        x = lattice ? xBegin - LatticeMod(offX + xBegin, 4) : xBegin;
        for (; lattice ? x < xEnd : x + 4 <= xEnd; x += 4)
        {
            if (_mask != null && !TakeBatch(rowBase, x, 4, xBegin, xEnd)) continue;   // #1344 / #1394
            // Wave 3.5 — + _refRecycleDx applies the frame's centre shift so dc
            // is measured from the cached reference centre (zero unless the
            // orbit was recycled; `+ 0.0` keeps the common path bit-identical).
            double dcR0 = (offX + x     - halfW) * scale + _refRecycleDx;
            double dcR1 = (offX + x + 1 - halfW) * scale + _refRecycleDx;
            double dcR2 = (offX + x + 2 - halfW) * scale + _refRecycleDx;
            double dcR3 = (offX + x + 3 - halfW) * scale + _refRecycleDx;
            var dcRv = Vector256.Create(dcR0, dcR1, dcR2, dcR3);

            var dr = Vector256<double>.Zero;
            var di = Vector256<double>.Zero;
            var drv = one;
            var div = Vector256<double>.Zero;
            var iterCount = Vector256<double>.Zero;
            int escapedMask = 0;
            bool glitched = false;
            int glitchIter = -1;   // #1613 — iteration the rebase test tripped at
            int glitchM = -1;      // #1614 — and its reference index
            int iterStart = 0;

            // ── SA prelude ─────────────────────────────────────────────────
            // Skip the first iterations from z=0 by evaluating the third-order
            // polynomial in dc. Lane-uniform k: use min skip across lanes
            // (largest dc has smallest valid skip; conservative — every lane
            // is safe at that k). Per-lane scalar polynomial eval to seed δ
            // and the chain-rule derivative.
            if (sa != null && sa.SafeMax >= 4)
            {
                int k0 = sa.FindSkip(dcR0, dcY, SaTolerance, maxIter - 1);
                int k1 = sa.FindSkip(dcR1, dcY, SaTolerance, maxIter - 1);
                int k2 = sa.FindSkip(dcR2, dcY, SaTolerance, maxIter - 1);
                int k3 = sa.FindSkip(dcR3, dcY, SaTolerance, maxIter - 1);
                int k = k0;
                if (k1 < k) k = k1;
                if (k2 < k) k = k2;
                if (k3 < k) k = k3;
                if (period > 0 && k > period) k = period;   // #1614 — m starts at k
                if (k >= 4)   // amortise polynomial cost only when skip is meaningful
                {
                    sa.EvalDelta(k, dcR0, dcY, out double d0r, out double d0i);
                    sa.EvalDelta(k, dcR1, dcY, out double d1r, out double d1i);
                    sa.EvalDelta(k, dcR2, dcY, out double d2r, out double d2i);
                    sa.EvalDelta(k, dcR3, dcY, out double d3r, out double d3i);
                    dr = Vector256.Create(d0r, d1r, d2r, d3r);
                    di = Vector256.Create(d0i, d1i, d2i, d3i);

                    sa.EvalDDelta(k, dcR0, dcY, out double v0r, out double v0i);
                    sa.EvalDDelta(k, dcR1, dcY, out double v1r, out double v1i);
                    sa.EvalDDelta(k, dcR2, dcY, out double v2r, out double v2i);
                    sa.EvalDDelta(k, dcR3, dcY, out double v3r, out double v3i);
                    drv = Vector256.Create(v0r, v1r, v2r, v3r);
                    div = Vector256.Create(v0i, v1i, v2i, v3i);

                    iterCount = Vector256.Create((double)k);
                    iterStart = k;
                    rowSaApplied += 4;
                    rowSaIterSaved += 4L * k;
                }
            }

            int m = iterStart;   // #1614 — reference index (== iter without a period)
            // #1802 — the batch's state at its last snapshot wrap (see below).
            var snapDr = Vector256<double>.Zero; var snapDi = Vector256<double>.Zero;
            int snapEsc = -1, wraps = 0, nextSnapWrap = 1;
            // #1803 test hook: hand the whole batch to the fallback before its first step.
            if (ForceRebasedLoops) { glitched = true; if (resume) { glitchIter = iterStart; glitchM = m; } }
            else
            for (int iter = iterStart; iter < maxIter; iter++)
            {
                if (m == period && period > 0)
                {
                    // #1614 — back at Z_p ~ 0: restart the reference (δ := z for the
                    // lanes still iterating; escaped lanes keep their frozen δ).
                    int act = ~escapedMask & 0b1111;
                    dr = BlendActive(dr, dr + Vector256.Create(_refZr[period]), act);
                    di = BlendActive(di, di + Vector256.Create(_refZi[period]), act);
                    m = 0;
                    // #1802 — periodicity at the wrap (PeriodicAtWrap). A bit-exact repeat
                    // of the batch's state (every lane's δ, the escaped lanes) here means the
                    // loop cycles for ever: its active lanes are in set.
                    if (deepPeriodicity && maxIter - iter >= period)
                    {
                        if (SameBatchState(dr, di, escapedMask, snapDr, snapDi, snapEsc))
                        {
                            iterCount = BlendActive(iterCount, Vector256.Create((double)maxIter), act);
                            Interlocked.Add(ref _deepPeriodicPixels, System.Numerics.BitOperations.PopCount((uint)act));
                            LogCaught(rowBase + x, act, 4);
                            break;
                        }
                        if (++wraps >= nextSnapWrap)
                        {
                            snapDr = dr; snapDi = di; snapEsc = escapedMask;
                            nextSnapWrap = wraps * 2;
                        }
                    }
                }
                if (m > refLen) { glitched = true; break; }

                // ── BLA skip attempt (per-lane via active-mask blending) ───
                // Lookup uses max |δ|² over ACTIVE lanes only — escaped lanes
                // have frozen (possibly large) δ that would block any skip.
                // Escaped lanes are preserved via BlendActive, so the skip
                // continues to compound even after individual lanes diverge.
                if (bla != null)
                {
                    int activeBla = ~escapedMask & 0b1111;
                    if (activeBla != 0)
                    {
                        var dmag2v = Avx.Add(Avx.Multiply(dr, dr), Avx.Multiply(di, di));
                        double maxActiveDmag2 = HMaxMasked(dmag2v, activeBla);
                        int blaIdx = bla.Lookup(m, maxActiveDmag2,
                            period > 0 ? Math.Min(period, m + (maxIter - iter)) : maxIter);   // #1614
                        if (blaIdx >= 0)
                        {
                            ref readonly var bEntry = ref bla.Data[blaIdx];
                            if (bEntry.L >= 2)
                            {
                                var aRev = Vector256.Create(bEntry.ARe);
                                var aImv = Vector256.Create(bEntry.AIm);
                                var bRev = Vector256.Create(bEntry.BRe);
                                var bImv = Vector256.Create(bEntry.BIm);

                                // δ' = A·δ + B·dc  (active lanes only)
                                var aDr = Avx.Subtract(Avx.Multiply(aRev, dr), Avx.Multiply(aImv, di));
                                var aDi = Avx.Add(Avx.Multiply(aRev, di), Avx.Multiply(aImv, dr));
                                var bDcR = Avx.Subtract(Avx.Multiply(bRev, dcRv), Avx.Multiply(bImv, dcYv));
                                var bDcI = Avx.Add(Avx.Multiply(bRev, dcYv), Avx.Multiply(bImv, dcRv));
                                var newDrBla = Avx.Add(aDr, bDcR);
                                var newDiBla = Avx.Add(aDi, bDcI);
                                dr = BlendActive(dr, newDrBla, activeBla);
                                di = BlendActive(di, newDiBla, activeBla);

                                // Derivative: d_{n+L} = A·d_n + B (#1782: B carries
                                // the +1 constants over the L steps; dropping it put
                                // normals / DE off on about half the pixels).
                                var aDrv = Avx.Add(Avx.Subtract(Avx.Multiply(aRev, drv), Avx.Multiply(aImv, div)), bRev);
                                var aDiv = Avx.Add(Avx.Add(Avx.Multiply(aRev, div), Avx.Multiply(aImv, drv)), bImv);
                                drv = BlendActive(drv, aDrv, activeBla);
                                div = BlendActive(div, aDiv, activeBla);

                                // iterCount += L only for active lanes.
                                var lVec = Vector256.Create((double)bEntry.L);
                                iterCount = Avx.Add(iterCount, Avx.And(lVec, MaskToVector(activeBla)));
                                iter += bEntry.L - 1;    // for-loop ++iter restores net +L
                                m += bEntry.L;
                                rowBlaSkips++;
                                rowBlaIterSaved += bEntry.L - 1;
                                continue;
                            }
                        }
                    }
                }

                var Zrv = Vector256.Create(_refZr[m]);
                var Ziv = Vector256.Create(_refZi[m]);
                var zr = Avx.Add(Zrv, dr);
                var zi = Avx.Add(Ziv, di);

                var mag2 = Avx.Add(Avx.Multiply(zr, zr), Avx.Multiply(zi, zi));
                var escV = Avx.Compare(mag2, er2v,
                    FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling);
                int newEsc = Avx.MoveMask(escV);

                // Register escapes first so active excludes this iteration's escapes.
                // This matches the scalar convention: iterCount = N for escape at iter N.
                escapedMask |= newEsc;
                int active = ~escapedMask & 0b1111;
                if (active == 0) break;

                // #1613 — QD band with BLA on: the rebase test, |z| < |δ| (the reference
                // no longer anchors the pixel), and the pixel carries on from here in the
                // rebased loop. The absorption test below (Z + δ == Z) fires on nearly
                // every QD-band pixel while δ is still accurate, and its restart has no
                // BLA: 4-5x the frame time on a period-448 minibrot at 5e33. Without BLA
                // (bypass, the #1274 tests) the absorption test and restart stay as they were.
                if (resume)
                {
                    var dm2 = Avx.Add(Avx.Multiply(dr, dr), Avx.Multiply(di, di));
                    if ((Avx.MoveMask(Avx.Compare(mag2, dm2, FloatComparisonMode.OrderedLessThanNonSignaling)) & active) != 0)
                    { glitched = true; glitchIter = iter; glitchM = m; break; }
                }
                // Precision glitch at extreme zoom: δ absorbed by Z in double arithmetic.
                // Only check in QD range where dc << ULP(Z) always holds.
                else if (useQD && !glitched)
                {
                    double Zr_s = _refZr[m], Zi_s = _refZi[m];
                    for (int k = 0; k < 4; k++)
                    {
                        double drk = dr.GetElement(k), dik = di.GetElement(k);
                        if ((drk != 0.0 || dik != 0.0) &&
                            zr.GetElement(k) == Zr_s && zi.GetElement(k) == Zi_s)
                        { glitched = true; break; }
                    }
                    if (glitched) break;
                }

                iterCount = Avx.Add(iterCount, Avx.And(one, MaskToVector(active)));

                var newDrv = Avx.Add(
                    Avx.Multiply(two, Avx.Subtract(Avx.Multiply(zr, drv), Avx.Multiply(zi, div))),
                    one);
                var newDiv = Avx.Multiply(two,
                    Avx.Add(Avx.Multiply(zr, div), Avx.Multiply(zi, drv)));
                drv = BlendActive(drv, newDrv, active);
                div = BlendActive(div, newDiv, active);

                // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
                var a = Avx.Add(Avx.Multiply(two, Zrv), dr);
                var b = Avx.Add(Avx.Multiply(two, Ziv), di);
                var newDr = Avx.Add(Avx.Subtract(Avx.Multiply(a, dr), Avx.Multiply(b, di)), dcRv);
                var newDi = Avx.Add(Avx.Add(Avx.Multiply(a, di), Avx.Multiply(b, dr)), dcYv);
                dr = BlendActive(dr, newDr, active);
                di = BlendActive(di, newDi, active);
                m++;   // #1614
                rowSteps++;
            }

            // Bulk-extract lanes via Vector256.CopyTo (one vmovupd per vector)
            // instead of five GetElement(k) calls per k (each costs a lane-
            // extract). ~5× fewer cross-domain transitions for the tail.
            // Scratch spans hoisted above loop (see CA2014 fix).
            iterCount.CopyTo(icSpan);
            dr.CopyTo(drSpan);
            di.CopyTo(diSpan);
            drv.CopyTo(drvSpan);
            div.CopyTo(divSpan);

            for (int k = 0; k < 4; k++)
            {
                if (x + k < xBegin || x + k >= xEnd) continue;   // #1330: a lane off the range
                int idx = rowBase + x + k;
                // Glitched pixels that never escaped need full fallback.
                // At zoom > QDZoomThreshold, DD cannot distinguish adjacent pixels
                // (pixel spacing ~2e-32 < DD precision ~6e-32); use QD instead.
                if (glitched && ((escapedMask >> k) & 1) == 0)
                {
                    double colOffsetX = offX + x + k - halfW;
                    if (resume && glitchIter >= 0)
                    {
                        // #1613 — carry on from here (rebased, with BLA by reference index).
                        ResumePixelPTRebased(colOffsetX * scale + _refRecycleDx, dcY, glitchIter, glitchM,
                            drSpan[k], diSpan[k], drvSpan[k], divSpan[k], bla, maxIter, idx, colorMap);
                        continue;
                    }
                    if (AllowPtRebasing)
                    {
                        if (queueRebase) { rqCol[rqN] = colOffsetX; rqIdx[rqN++] = idx; }   // #1274
                        else ComputePixelPTRebased(
                            colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
                        continue;
                    }
                    if (useOD)
                    {
                        OD cx_od = OD.FromCenterOffset(
                            new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                                   CenterX4, CenterX5, CenterX6, CenterX7),
                            colOffsetX, scale);
                        ComputePixelOD(cx_od, cy_od, maxIter, idx, colorMap);
                    }
                    else if (useQD)
                    {
                        QD cx_qd = QD.FromCenterOffset(
                            new QD(CenterX, CenterXLo, CenterX2, CenterX3), colOffsetX, scale);
                        ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                    }
                    else
                    {
                        DD cx_dd = DD.FromCenterOffset(
                            new DD(CenterX, CenterXLo), colOffsetX, scale);
                        ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                    }
                    continue;
                }
                int iters = (int)icSpan[k];
                // Reconstruct z at escape: Z_iters + δ_iters (δ frozen when lane escaped)
                int ri = period > 0 ? iters % period : iters;   // #1614 — m at the escape
                double zrF = (ri <= refLen ? _refZr[ri] : 0.0) + drSpan[k];
                double ziF = (ri <= refLen ? _refZi[ri] : 0.0) + diSpan[k];
                IterationBuffer[idx] = iters;
                FillAuxAndColorHP(idx, iters, maxIter, zrF, ziF,
                    drvSpan[k], divSpan[k], colorMap);
            }
        }

        // Scalar tail (0–3 remaining pixels)
        for (; x < xEnd; x++)
        {
            if (!Marked(rowBase + x)) continue;   // #1344
            double colOffsetX = offX + x - halfW;
            double dcX = colOffsetX * scale + _refRecycleDx;  // Wave 3.5 recycle shift (0 on normal path)
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (AllowPtRebasing)
                {
                    if (queueRebase) { rqCol[rqN] = colOffsetX; rqIdx[rqN++] = idx; }   // #1274
                    else ComputePixelPTRebased(colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
                }
                else if (useOD)
                {
                    OD cx_od = OD.FromCenterOffset(
                        new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                               CenterX4, CenterX5, CenterX6, CenterX7),
                        colOffsetX, scale);
                    ComputePixelOD(cx_od, cy_od, maxIter, idx, colorMap);
                }
                else if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), colOffsetX, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), colOffsetX, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }

        if (rqN > 0)
            ComputePixelsPTRebased4(rqCol, rqIdx, rqN, rowOffsetY, scale, maxIter, colorMap);

        if (rowBlaSkips != 0)
        {
            Interlocked.Add(ref _blaSkipsTotal, rowBlaSkips);
            Interlocked.Add(ref _ptStepsTotal, rowSteps);
            Interlocked.Add(ref _blaIterSkippedTotal, rowBlaIterSaved);
        }
        if (rowSaApplied != 0)
        {
            Interlocked.Add(ref _saAppliedTotal, rowSaApplied);
            Interlocked.Add(ref _saIterSkippedTotal, rowSaIterSaved);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B-512 — 8-wide AVX-512 perturbation, mirrors ComputeRowPT4
    // Cross-platform Vector512 API: RyuJIT emits vaddpd/vmulpd zmm on
    // AVX512F-capable CPUs. SA prelude + per-lane BLA carry over unchanged
    // (per-lane scalar polynomial eval just runs 8 times instead of 4).
    // ─────────────────────────────────────────────────────────────────────────

    private void ComputeRowPT8<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        // D-6b1 — sub-rect aware (see ComputeRowPT4 head for details).
        double halfW = EffectiveImageWidth  * 0.5;
        double halfH = EffectiveImageHeight * 0.5;
        int    offX  = SubRectOffsetX;
        int    offY  = SubRectOffsetY;
        double rowOffsetY = offY + y - halfH;
        // Wave 3.5 — _refRecycleDy is the frame's centre shift from the cached
        // reference centre (zero unless the orbit was recycled), so dc is
        // measured from the actual reference orbit. `+ 0.0` on the common path
        // keeps this bit-identical to pre-3.5.
        double dcY = rowOffsetY * scale + _refRecycleDy;
        bool useOD = Zoom > ODZoomThreshold;
        bool useQD = Zoom > QDZoomThreshold && !useOD;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), rowOffsetY, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), rowOffsetY, scale)
            : QD.Zero;
        OD cy_od = useOD
            ? OD.FromCenterOffset(
                new OD(CenterY, CenterYLo, CenterY2, CenterY3,
                       CenterY4, CenterY5, CenterY6, CenterY7),
                rowOffsetY, scale)
            : OD.Zero;

        var er2v = Vector512.Create(EscapeRadius2);
        var one = Vector512.Create(1.0);
        var two = Vector512.Create(2.0);
        var dcYv = Vector512.Create(dcY);
        int refLen = _refOrbitLen;
        var bla = DisableAcceleration ? null : _blaTable;
        var sa = (DisableAcceleration || DisableSeriesApproximation) ? null : _sa;
        // #1613 — in the QD band with BLA on, a pixel that trips the rebase test
        // continues in the rebased loop from its state, with BLA (otherwise, and in
        // the DD / OD bands: as before — the absorption test in the QD band and the
        // #1274 restart through the queue / scalar loop).
        bool resume = bla != null && ResumesInQdBand;
        // #1614 — a periodic reference (_refPeriod): the rows index it by m, which
        // wraps at the period (δ += Z_p, the exact rebase at Z_p ≈ 0); without one
        // m == iter, as before.
        int period = _refPeriod;
        bool deepPeriodicity = DeepPeriodicity;   // #1802
        long rowBlaSkips = 0;
        long rowSteps = 0;   // #1614 — plain (non-BLA) vector steps
        long rowBlaIterSaved = 0;
        long rowSaApplied = 0;
        long rowSaIterSaved = 0;
        int x = 0;

        // #1274 — glitched pixels queue for the 4-wide rebased loop at the row end.
        bool queueRebase = AllowPtRebasing && UseSimdRebasing && !UseDdRebaseReference;
        var (rqCol, rqIdx) = queueRebase ? RebaseQueue(Width) : (Array.Empty<double>(), Array.Empty<int>());
        int rqN = 0;

        // Hoisted out of column loop to avoid CA2014 stack growth.
        Span<double> icSpan  = stackalloc double[8];
        Span<double> drSpan  = stackalloc double[8];
        Span<double> diSpan  = stackalloc double[8];
        Span<double> drvSpan = stackalloc double[8];
        Span<double> divSpan = stackalloc double[8];

        // #1330 — LatticeAligned: batches start on lattice multiples (the first may
        // begin left of the range) and run past the range end; lanes outside
        // [xBegin, xEnd) are computed and discarded, so a pixel's batch (and SA /
        // BLA / glitch decision) is the same at every frame offset. No scalar tail.
        bool lattice = LatticeAligned;
        x = lattice ? xBegin - LatticeMod(offX + xBegin, 8) : xBegin;
        for (; lattice ? x < xEnd : x + 8 <= xEnd; x += 8)
        {
            if (_mask != null && !TakeBatch(rowBase, x, 8, xBegin, xEnd)) continue;   // #1344 / #1394
            // Wave 3.5 — + _refRecycleDx: see ComputeRowPT4 (centre-shift for
            // orbit recycling; zero on the common path → bit-identical).
            double dcR0 = (offX + x     - halfW) * scale + _refRecycleDx;
            double dcR1 = (offX + x + 1 - halfW) * scale + _refRecycleDx;
            double dcR2 = (offX + x + 2 - halfW) * scale + _refRecycleDx;
            double dcR3 = (offX + x + 3 - halfW) * scale + _refRecycleDx;
            double dcR4 = (offX + x + 4 - halfW) * scale + _refRecycleDx;
            double dcR5 = (offX + x + 5 - halfW) * scale + _refRecycleDx;
            double dcR6 = (offX + x + 6 - halfW) * scale + _refRecycleDx;
            double dcR7 = (offX + x + 7 - halfW) * scale + _refRecycleDx;
            var dcRv = Vector512.Create(dcR0, dcR1, dcR2, dcR3, dcR4, dcR5, dcR6, dcR7);

            var dr = Vector512<double>.Zero;
            var di = Vector512<double>.Zero;
            var drv = one;
            var div = Vector512<double>.Zero;
            var iterCount = Vector512<double>.Zero;
            int escapedMask = 0;
            bool glitched = false;
            int glitchIter = -1;   // #1613 — iteration the rebase test tripped at
            int glitchM = -1;      // #1614 — and its reference index
            int iterStart = 0;

            // ── SA prelude (8 lanes) ───────────────────────────────────────
            if (sa != null && sa.SafeMax >= 4)
            {
                int k0 = sa.FindSkip(dcR0, dcY, SaTolerance, maxIter - 1);
                int k1 = sa.FindSkip(dcR1, dcY, SaTolerance, maxIter - 1);
                int k2 = sa.FindSkip(dcR2, dcY, SaTolerance, maxIter - 1);
                int k3 = sa.FindSkip(dcR3, dcY, SaTolerance, maxIter - 1);
                int k4 = sa.FindSkip(dcR4, dcY, SaTolerance, maxIter - 1);
                int k5 = sa.FindSkip(dcR5, dcY, SaTolerance, maxIter - 1);
                int k6 = sa.FindSkip(dcR6, dcY, SaTolerance, maxIter - 1);
                int k7 = sa.FindSkip(dcR7, dcY, SaTolerance, maxIter - 1);
                int k = k0;
                if (k1 < k) k = k1;
                if (k2 < k) k = k2;
                if (k3 < k) k = k3;
                if (k4 < k) k = k4;
                if (k5 < k) k = k5;
                if (k6 < k) k = k6;
                if (k7 < k) k = k7;
                if (period > 0 && k > period) k = period;   // #1614 — m starts at k
                if (k >= 4)
                {
                    sa.EvalDelta(k, dcR0, dcY, out double d0r, out double d0i);
                    sa.EvalDelta(k, dcR1, dcY, out double d1r, out double d1i);
                    sa.EvalDelta(k, dcR2, dcY, out double d2r, out double d2i);
                    sa.EvalDelta(k, dcR3, dcY, out double d3r, out double d3i);
                    sa.EvalDelta(k, dcR4, dcY, out double d4r, out double d4i);
                    sa.EvalDelta(k, dcR5, dcY, out double d5r, out double d5i);
                    sa.EvalDelta(k, dcR6, dcY, out double d6r, out double d6i);
                    sa.EvalDelta(k, dcR7, dcY, out double d7r, out double d7i);
                    dr = Vector512.Create(d0r, d1r, d2r, d3r, d4r, d5r, d6r, d7r);
                    di = Vector512.Create(d0i, d1i, d2i, d3i, d4i, d5i, d6i, d7i);

                    sa.EvalDDelta(k, dcR0, dcY, out double v0r, out double v0i);
                    sa.EvalDDelta(k, dcR1, dcY, out double v1r, out double v1i);
                    sa.EvalDDelta(k, dcR2, dcY, out double v2r, out double v2i);
                    sa.EvalDDelta(k, dcR3, dcY, out double v3r, out double v3i);
                    sa.EvalDDelta(k, dcR4, dcY, out double v4r, out double v4i);
                    sa.EvalDDelta(k, dcR5, dcY, out double v5r, out double v5i);
                    sa.EvalDDelta(k, dcR6, dcY, out double v6r, out double v6i);
                    sa.EvalDDelta(k, dcR7, dcY, out double v7r, out double v7i);
                    drv = Vector512.Create(v0r, v1r, v2r, v3r, v4r, v5r, v6r, v7r);
                    div = Vector512.Create(v0i, v1i, v2i, v3i, v4i, v5i, v6i, v7i);

                    iterCount = Vector512.Create((double)k);
                    iterStart = k;
                    rowSaApplied += 8;
                    rowSaIterSaved += 8L * k;
                }
            }

            int m = iterStart;   // #1614 — reference index (== iter without a period)
            // #1802 — the batch's state at its last snapshot wrap (ComputeRowPT4).
            var snapDr = Vector512<double>.Zero; var snapDi = Vector512<double>.Zero;
            int snapEsc = -1, wraps = 0, nextSnapWrap = 1;
            for (int iter = iterStart; iter < maxIter; iter++)
            {
                if (m == period && period > 0)
                {
                    // #1614 — back at Z_p ~ 0: restart the reference (δ := z for the
                    // lanes still iterating; escaped lanes keep their frozen δ).
                    int act = ~escapedMask & 0xFF;
                    dr = BlendActive512(dr, dr + Vector512.Create(_refZr[period]), act);
                    di = BlendActive512(di, di + Vector512.Create(_refZi[period]), act);
                    m = 0;
                    // #1802 — periodicity at the wrap (PeriodicAtWrap).
                    if (deepPeriodicity && maxIter - iter >= period)
                    {
                        if (SameBatchState(dr, di, escapedMask, snapDr, snapDi, snapEsc))
                        {
                            iterCount = BlendActive512(iterCount, Vector512.Create((double)maxIter), act);
                            Interlocked.Add(ref _deepPeriodicPixels, System.Numerics.BitOperations.PopCount((uint)act));
                            LogCaught(rowBase + x, act, 8);
                            break;
                        }
                        if (++wraps >= nextSnapWrap)
                        {
                            snapDr = dr; snapDi = di; snapEsc = escapedMask;
                            nextSnapWrap = wraps * 2;
                        }
                    }
                }
                if (m > refLen) { glitched = true; break; }

                // ── BLA skip (8-lane, per-lane via active mask) ────────────
                if (bla != null)
                {
                    int activeBla = ~escapedMask & 0xFF;
                    if (activeBla != 0)
                    {
                        var dmag2v = dr * dr + di * di;
                        double maxActiveDmag2 = HMaxMasked8(dmag2v, activeBla);
                        int blaIdx = bla.Lookup(m, maxActiveDmag2,
                            period > 0 ? Math.Min(period, m + (maxIter - iter)) : maxIter);   // #1614
                        if (blaIdx >= 0)
                        {
                            ref readonly var bEntry = ref bla.Data[blaIdx];
                            if (bEntry.L >= 2)
                            {
                                var aRev = Vector512.Create(bEntry.ARe);
                                var aImv = Vector512.Create(bEntry.AIm);
                                var bRev = Vector512.Create(bEntry.BRe);
                                var bImv = Vector512.Create(bEntry.BIm);

                                var aDr = aRev * dr - aImv * di;
                                var aDi = aRev * di + aImv * dr;
                                var bDcR = bRev * dcRv - bImv * dcYv;
                                var bDcI = bRev * dcYv + bImv * dcRv;
                                dr = BlendActive512(dr, aDr + bDcR, activeBla);
                                di = BlendActive512(di, aDi + bDcI, activeBla);

                                var aDrv = aRev * drv - aImv * div + bRev;   // d' = A·d + B (#1782)
                                var aDiv = aRev * div + aImv * drv + bImv;
                                drv = BlendActive512(drv, aDrv, activeBla);
                                div = BlendActive512(div, aDiv, activeBla);

                                var lVec = Vector512.Create((double)bEntry.L);
                                iterCount += lVec & MaskToVector512(activeBla);
                                iter += bEntry.L - 1;
                                m += bEntry.L;
                                rowBlaSkips++;
                                rowBlaIterSaved += bEntry.L - 1;
                                continue;
                            }
                        }
                    }
                }

                var Zrv = Vector512.Create(_refZr[m]);
                var Ziv = Vector512.Create(_refZi[m]);
                var zr = Zrv + dr;
                var zi = Ziv + di;

                var mag2 = zr * zr + zi * zi;
                var escV = Vector512.GreaterThanOrEqual(mag2, er2v);
                int newEsc = (int)escV.ExtractMostSignificantBits();

                escapedMask |= newEsc;
                int active = ~escapedMask & 0xFF;
                if (active == 0) break;

                // #1613 — the rebase test with BLA on, the absorption test without
                // (see ComputeRowPT4).
                if (resume)
                {
                    var dm2 = dr * dr + di * di;
                    if (((int)Vector512.LessThan(mag2, dm2).ExtractMostSignificantBits() & active) != 0)
                    { glitched = true; glitchIter = iter; glitchM = m; break; }
                }
                else if (useQD && !glitched)
                {
                    double Zr_s = _refZr[m], Zi_s = _refZi[m];
                    for (int k = 0; k < 8; k++)
                    {
                        double drk = dr.GetElement(k), dik = di.GetElement(k);
                        if ((drk != 0.0 || dik != 0.0) &&
                            zr.GetElement(k) == Zr_s && zi.GetElement(k) == Zi_s)
                        { glitched = true; break; }
                    }
                    if (glitched) break;
                }

                iterCount += one & MaskToVector512(active);

                var newDrv = two * (zr * drv - zi * div) + one;
                var newDiv = two * (zr * div + zi * drv);
                drv = BlendActive512(drv, newDrv, active);
                div = BlendActive512(div, newDiv, active);

                // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
                var a = two * Zrv + dr;
                var b = two * Ziv + di;
                var newDr = a * dr - b * di + dcRv;
                var newDi = a * di + b * dr + dcYv;
                dr = BlendActive512(dr, newDr, active);
                di = BlendActive512(di, newDi, active);
                m++;   // #1614
                rowSteps++;
            }

            // Scratch spans hoisted above loop (see CA2014 fix).
            iterCount.CopyTo(icSpan);
            dr.CopyTo(drSpan);
            di.CopyTo(diSpan);
            drv.CopyTo(drvSpan);
            div.CopyTo(divSpan);

            for (int k = 0; k < 8; k++)
            {
                if (x + k < xBegin || x + k >= xEnd) continue;   // #1330: a lane off the range
                int idx = rowBase + x + k;
                if (glitched && ((escapedMask >> k) & 1) == 0)
                {
                    double colOffsetX = offX + x + k - halfW;
                    if (resume && glitchIter >= 0)
                    {
                        // #1613 — carry on from here (rebased, with BLA by reference index).
                        ResumePixelPTRebased(colOffsetX * scale + _refRecycleDx, dcY, glitchIter, glitchM,
                            drSpan[k], diSpan[k], drvSpan[k], divSpan[k], bla, maxIter, idx, colorMap);
                        continue;
                    }
                    if (AllowPtRebasing)
                    {
                        if (queueRebase) { rqCol[rqN] = colOffsetX; rqIdx[rqN++] = idx; }   // #1274
                        else ComputePixelPTRebased(
                            colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
                        continue;
                    }
                    if (useOD)
                    {
                        OD cx_od = OD.FromCenterOffset(
                            new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                                   CenterX4, CenterX5, CenterX6, CenterX7),
                            colOffsetX, scale);
                        ComputePixelOD(cx_od, cy_od, maxIter, idx, colorMap);
                    }
                    else if (useQD)
                    {
                        QD cx_qd = QD.FromCenterOffset(
                            new QD(CenterX, CenterXLo, CenterX2, CenterX3), colOffsetX, scale);
                        ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                    }
                    else
                    {
                        DD cx_dd = DD.FromCenterOffset(
                            new DD(CenterX, CenterXLo), colOffsetX, scale);
                        ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                    }
                    continue;
                }
                int iters = (int)icSpan[k];
                int ri = period > 0 ? iters % period : iters;   // #1614 — m at the escape
                double zrF = (ri <= refLen ? _refZr[ri] : 0.0) + drSpan[k];
                double ziF = (ri <= refLen ? _refZi[ri] : 0.0) + diSpan[k];
                IterationBuffer[idx] = iters;
                FillAuxAndColorHP(idx, iters, maxIter, zrF, ziF,
                    drvSpan[k], divSpan[k], colorMap);
            }
        }

        // Scalar tail (0–7 remaining pixels)
        for (; x < xEnd; x++)
        {
            if (!Marked(rowBase + x)) continue;   // #1344
            double colOffsetX = offX + x - halfW;
            double dcX = colOffsetX * scale + _refRecycleDx;  // Wave 3.5 recycle shift (0 on normal path)
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (AllowPtRebasing)
                {
                    if (queueRebase) { rqCol[rqN] = colOffsetX; rqIdx[rqN++] = idx; }   // #1274
                    else ComputePixelPTRebased(colOffsetX, rowOffsetY, scale, maxIter, idx, colorMap);
                }
                else if (useOD)
                {
                    OD cx_od = OD.FromCenterOffset(
                        new OD(CenterX, CenterXLo, CenterX2, CenterX3,
                               CenterX4, CenterX5, CenterX6, CenterX7),
                        colOffsetX, scale);
                    ComputePixelOD(cx_od, cy_od, maxIter, idx, colorMap);
                }
                else if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), colOffsetX, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), colOffsetX, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }

        if (rqN > 0)
            ComputePixelsPTRebased4(rqCol, rqIdx, rqN, rowOffsetY, scale, maxIter, colorMap);

        if (rowBlaSkips != 0)
        {
            Interlocked.Add(ref _blaSkipsTotal, rowBlaSkips);
            Interlocked.Add(ref _ptStepsTotal, rowSteps);
            Interlocked.Add(ref _blaIterSkippedTotal, rowBlaIterSaved);
        }
        if (rowSaApplied != 0)
        {
            Interlocked.Add(ref _saAppliedTotal, rowSaApplied);
            Interlocked.Add(ref _saIterSkippedTotal, rowSaIterSaved);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Histogram equalization — adaptive contrast post-pass
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Builds a CDF over the smooth iteration distribution of escaped pixels,
    // then re-samples the active color map using an iteration value blended
    // between the original linear value and the equalized value.
    //
    //   strength = 0.0 → identity (recolor matches inline color pass)
    //   strength = 1.0 → full equalization (iteration density flattened)
    //
    // Operates on the SmoothBuffer / IterationBuffer that Calculate() already
    // fills, so it can be re-run at any strength without recomputing the
    // fractal.  Always overwrites ColorBuffer from scratch using the current
    // ColorMap, so successive calls with different strengths compose correctly.
    public void ApplyHistogramEqualization(double strength)
    {
        if (!BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter))
        {
            // No escaped pixels — fall back to plain recolor.
            RecolorFromBuffers();
            return;
        }
        ApplyHistogramEqualizationWithCdf(cdf!, bins, sourceMaxIter, strength);
    }

    /// <summary>
    /// Builds the equalization CDF for the current SmoothBuffer/IterationBuffer
    /// without applying it. Returns false (and zeroed outputs) when the view
    /// has no escaped pixels — caller should treat that as the identity case.
    /// Used by the video path to lock the CDF for the duration of a leg so
    /// per-frame palette mapping does not flicker as image statistics drift.
    /// </summary>
    public bool BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter)
    {
        sourceMaxIter = MaxIterations;
        return HistogramEqualizer.BuildCdf(
            Width, Height, MaxIterations, IterationBuffer, SmoothBuffer, out cdf, out bins);
    }

    /// <summary>
    /// Applies a previously-built equalization CDF to the current buffers.
    /// The smooth-iter → bin lookup is normalized against <paramref name="sourceMaxIter"/>
    /// (MaxIterations at build time) so a locked CDF stays valid across frames
    /// whose MaxIterations differs by tier auto-promote — pixels above the
    /// build-time iter ceiling fall into the last bin (saturating, no flicker).
    /// </summary>
    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, 0.0, out _, out _);

    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, ditherIterStrength, out _, out _);

    /// <summary>
    /// As above, but additionally applies a stable per-pixel spatial dither
    /// (in iteration units) to the smooth-iter value before palette lookup.
    /// Used by the video path to blur band boundaries so the per-frame shift
    /// across them becomes less visible. The dither is a function of (x, y)
    /// only, so it introduces no new temporal noise.
    ///
    /// Returns the count of escaped pixels and the count that saturated to the
    /// last CDF bin so the caller can detect when the locked CDF has drifted
    /// out of range (e.g. after tier auto-promote produced smooth-iter values
    /// above the build-time sourceMaxIter) and trigger a rebuild.
    /// </summary>
    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength, out long escapedCount, out long saturatedCount)
    {
        var st = new EscapeTimeColorState(
            Width, Height, MaxIterations, MapDistanceUnit(ColorMap).Scale, ColorMap,   // #1783: the map's unit
            IterationBuffer, SmoothBuffer, DistanceBuffer, NormalXBuffer, NormalYBuffer,
            FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer, ColorBuffer, _po,
            mapDistExp2: MapDistanceUnit(ColorMap).Exp2);
        HistogramEqualizer.ApplyWithCdf(
            in st, cdf, bins, sourceMaxIter, strength, ditherIterStrength,
            out escapedCount, out saturatedCount);
    }

    /// <summary>
    /// Recolors the ColorBuffer using the current SmoothBuffer + a stable
    /// spatial dither added to the smooth-iter value in iteration units.
    /// Used by the video path when band-edge dither is enabled but histogram
    /// equalization is not.
    /// </summary>
    public void ApplyBandDitherRecolor(double ditherIterStrength)
    {
        if (ditherIterStrength <= 0.0) { RecolorFromBuffers(); return; }

        int w = Width, h = Height;
        int maxIter = MaxIterations;
        if (w == 0 || h == 0 || maxIter <= 0) return;

        ColorMap.MaxIterations = maxIter;
        var (mapScale0, mapExp0) = MapDistanceUnit(ColorMap);   // #1783
        if (ColorMap is IColorMapWithPixelScale pxs0) pxs0.PixelScale = mapScale0;
        bool handlesInSet = ColorMap is IColorMapHandlesInSet;
        float ditherIter = (float)ditherIterStrength;

        _po.CancellationToken = CancellationToken.None;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        ColorBuffer[idx] = (uint)ColorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        ColorBuffer[idx] = ColorMap.InSetColor;
                    }
                    continue;
                }
                float s = SmoothBuffer[idx] + SpatialDither(x, y) * ditherIter;
                int iterArg = handlesInSet ? iters : maxIter;
                ColorBuffer[idx] = (uint)ColorMap.Map(
                    s, MapDist(DistanceBuffer[idx], mapExp0), iterArg,
                    NormalXBuffer[idx], NormalYBuffer[idx],
                    FinalZrBuffer[idx], FinalZiBuffer[idx],
                    MapDz(FinalDrBuffer[idx], mapExp0), MapDz(FinalDiBuffer[idx], mapExp0));   // #1843
            }
        });

        // Issue #96 — the recolor rewrote every in-set pixel opaque, so the
        // interior alpha stamp (applied at the end of Calculate) is gone. Re-run
        // it here or the interior turns opaque on a theme switch until the next
        // pan/zoom triggers a full Calculate.
        if (InteriorAlpha < 255) StampInteriorAlpha(CancellationToken.None);
    }

    // Stable spatial hash → [-0.5, 0.5). Pure function of (x, y), so the
    // dither pattern locks to image space and adds no temporal noise across
    // frames at a given pixel.
    private static float SpatialDither(int x, int y)
    {
        uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return ((h & 0xFFFFu) * (1.0f / 65535.0f)) - 0.5f;
    }

    // Recompute ColorBuffer from filled aux buffers without changing iteration
    // values.  Used as the strength=0 / no-escape fast path of equalization.
    private void RecolorFromBuffers()
    {
        int w = Width, h = Height;
        int maxIter = MaxIterations;
        ColorMap.MaxIterations = maxIter;
        var (mapScale, mapExp) = MapDistanceUnit(ColorMap);   // #1783
        if (ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = mapScale;
        bool handlesInSet = ColorMap is IColorMapHandlesInSet;
        _po.CancellationToken = CancellationToken.None;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        ColorBuffer[idx] = (uint)ColorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        ColorBuffer[idx] = ColorMap.InSetColor;
                    }
                }
                else
                {
                    int iterArg = handlesInSet ? iters : maxIter;
                    ColorBuffer[idx] = (uint)ColorMap.Map(
                        SmoothBuffer[idx], MapDist(DistanceBuffer[idx], mapExp), iterArg,
                        NormalXBuffer[idx], NormalYBuffer[idx],
                        FinalZrBuffer[idx], FinalZiBuffer[idx],
                        MapDz(FinalDrBuffer[idx], mapExp), MapDz(FinalDiBuffer[idx], mapExp));   // #1843
                }
            }
        });

        // Issue #96 — re-apply interior alpha after the recolor rewrote in-set
        // pixels opaque (see note in ApplyBandDitherRecolor).
        if (InteriorAlpha < 255) StampInteriorAlpha(CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // D-6b — shared reference-orbit support for cluster tile rendering
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Public DD-precision reference-orbit computation. Used by the
    /// cluster master to pre-compute the orbit once per deep-zoom job and
    /// ship it to every tile via <see cref="SeedReferenceOrbitDD"/>. The
    /// math mirrors the private <c>ComputeReferenceOrbit(DD,DD,int)</c>
    /// instance method; staying in lockstep with that path is enforced by
    /// the cluster pixel-parity test.</summary>
    public static OrbitDD ComputeReferenceOrbitDDPublic(
        double centreX, double centreXLo,
        double centreY, double centreYLo,
        int maxIter)
    {
        if (maxIter <= 0) throw new ArgumentOutOfRangeException(nameof(maxIter));
        var cx = new DD(centreX, centreXLo);
        var cy = new DD(centreY, centreYLo);
        int sz = maxIter + 1;
        var zr   = new double[sz];
        var zi   = new double[sz];
        var zrLo = new double[sz];
        var ziLo = new double[sz];

        DD z_r = DD.Zero, z_i = DD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            zr[n] = z_r.Hi;  zrLo[n] = z_r.Lo;
            zi[n] = z_i.Hi;  ziLo[n] = z_i.Lo;
            if (z_r.Hi * z_r.Hi + z_i.Hi * z_i.Hi >= EscapeRadius2) break;
            DD newZi = (z_r * z_i) * 2.0 + cy;
            z_r = z_r.Square() - z_i.Square() + cx;
            z_i = newZi;
        }
        zr[n] = z_r.Hi;  zrLo[n] = z_r.Lo;
        zi[n] = z_i.Hi;  ziLo[n] = z_i.Lo;

        return new OrbitDD
        {
            CentreX   = centreX,
            CentreXLo = centreXLo,
            CentreY   = centreY,
            CentreYLo = centreYLo,
            MaxIter   = maxIter,
            RefLen    = n,
            Escaped   = n < maxIter,
            Zr        = zr,
            Zi        = zi,
            ZrLo      = zrLo,
            ZiLo      = ziLo,
        };
    }

    /// <summary>Container for a DD-precision reference orbit produced by
    /// <see cref="ComputeReferenceOrbitDDPublic"/>. Arrays sized
    /// <c>RefLen + 1</c>; slot RefLen carries the post-escape Z value
    /// (mirrors the engine's storage shape).</summary>
    public sealed class OrbitDD
    {
        public required double CentreX   { get; init; }
        public required double CentreXLo { get; init; }
        public required double CentreY   { get; init; }
        public required double CentreYLo { get; init; }
        public required int    MaxIter   { get; init; }
        public required int    RefLen    { get; init; }
        public required bool   Escaped   { get; init; }
        public required double[] Zr      { get; init; }
        public required double[] Zi      { get; init; }
        public required double[] ZrLo    { get; init; }
        public required double[] ZiLo    { get; init; }
    }

    /// <summary>D-6b2 — public QD-precision reference-orbit computation.
    /// Mirrors the private <c>ComputeReferenceOrbitQD(QD,QD,int)</c>
    /// instance method bit-for-bit so a cluster master can pre-compute the
    /// orbit once per deep-zoom job (zoom &gt; 1e25) and ship it to every
    /// tile via <see cref="SeedReferenceOrbitQD"/>.</summary>
    public static OrbitQD ComputeReferenceOrbitQDPublic(
        double centreX,  double centreXLo, double centreX2, double centreX3,
        double centreY,  double centreYLo, double centreY2, double centreY3,
        int maxIter)
    {
        if (maxIter <= 0) throw new ArgumentOutOfRangeException(nameof(maxIter));
        var cx = new QD(centreX, centreXLo, centreX2, centreX3);
        var cy = new QD(centreY, centreYLo, centreY2, centreY3);
        int sz = maxIter + 1;
        var zr   = new double[sz]; var zi   = new double[sz];
        var zrLo = new double[sz]; var ziLo = new double[sz];
        var zrX2 = new double[sz]; var ziX2 = new double[sz];
        var zrX3 = new double[sz]; var ziX3 = new double[sz];

        QD z_r = QD.Zero, z_i = QD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            zr[n]   = z_r.X0; zi[n]   = z_i.X0;
            zrLo[n] = z_r.X1; ziLo[n] = z_i.X1;
            zrX2[n] = z_r.X2; ziX2[n] = z_i.X2;
            zrX3[n] = z_r.X3; ziX3[n] = z_i.X3;
            if (z_r.X0 * z_r.X0 + z_i.X0 * z_i.X0 >= EscapeRadius2) break;
            QD newZi = (z_r * z_i) * 2.0 + cy;
            z_r = z_r.Square() - z_i.Square() + cx;
            z_i = newZi;
        }
        zr[n]   = z_r.X0; zi[n]   = z_i.X0;
        zrLo[n] = z_r.X1; ziLo[n] = z_i.X1;
        zrX2[n] = z_r.X2; ziX2[n] = z_i.X2;
        zrX3[n] = z_r.X3; ziX3[n] = z_i.X3;

        return new OrbitQD
        {
            CentreX   = centreX,   CentreXLo = centreXLo,
            CentreX2  = centreX2,  CentreX3  = centreX3,
            CentreY   = centreY,   CentreYLo = centreYLo,
            CentreY2  = centreY2,  CentreY3  = centreY3,
            MaxIter   = maxIter,
            RefLen    = n,
            Escaped   = n < maxIter,
            Zr   = zr,   Zi   = zi,
            ZrLo = zrLo, ZiLo = ziLo,
            ZrX2 = zrX2, ZiX2 = ziX2,
            ZrX3 = zrX3, ZiX3 = ziX3,
        };
    }

    /// <summary>D-6b2 — container for a QD-precision reference orbit.
    /// Arrays sized <c>RefLen + 1</c>.</summary>
    public sealed class OrbitQD
    {
        public required double CentreX   { get; init; }
        public required double CentreXLo { get; init; }
        public required double CentreX2  { get; init; }
        public required double CentreX3  { get; init; }
        public required double CentreY   { get; init; }
        public required double CentreYLo { get; init; }
        public required double CentreY2  { get; init; }
        public required double CentreY3  { get; init; }
        public required int    MaxIter   { get; init; }
        public required int    RefLen    { get; init; }
        public required bool   Escaped   { get; init; }
        public required double[] Zr   { get; init; }
        public required double[] Zi   { get; init; }
        public required double[] ZrLo { get; init; }
        public required double[] ZiLo { get; init; }
        public required double[] ZrX2 { get; init; }
        public required double[] ZiX2 { get; init; }
        public required double[] ZrX3 { get; init; }
        public required double[] ZiX3 { get; init; }
    }

    /// <summary>D-6b2 — public OD-precision reference-orbit computation.
    /// Mirrors the private <c>ComputeReferenceOrbitOD(OD,OD,int)</c>
    /// instance method bit-for-bit so a cluster master can pre-compute the
    /// orbit once per ultra-deep-zoom job (zoom &gt; 1e50) and ship it to
    /// every tile via <see cref="SeedReferenceOrbitOD"/>.</summary>
    public static OrbitOD ComputeReferenceOrbitODPublic(
        double centreX,  double centreXLo, double centreX2, double centreX3,
        double centreX4, double centreX5,  double centreX6, double centreX7,
        double centreY,  double centreYLo, double centreY2, double centreY3,
        double centreY4, double centreY5,  double centreY6, double centreY7,
        int maxIter)
    {
        if (maxIter <= 0) throw new ArgumentOutOfRangeException(nameof(maxIter));
        var cx = new OD(centreX, centreXLo, centreX2, centreX3,
                        centreX4, centreX5, centreX6, centreX7);
        var cy = new OD(centreY, centreYLo, centreY2, centreY3,
                        centreY4, centreY5, centreY6, centreY7);
        int sz = maxIter + 1;
        var zr   = new double[sz]; var zi   = new double[sz];
        var zrLo = new double[sz]; var ziLo = new double[sz];
        var zrX2 = new double[sz]; var ziX2 = new double[sz];
        var zrX3 = new double[sz]; var ziX3 = new double[sz];
        var zrX4 = new double[sz]; var ziX4 = new double[sz];
        var zrX5 = new double[sz]; var ziX5 = new double[sz];
        var zrX6 = new double[sz]; var ziX6 = new double[sz];
        var zrX7 = new double[sz]; var ziX7 = new double[sz];

        OD z_r = OD.Zero, z_i = OD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            zr[n]   = z_r.X0; zi[n]   = z_i.X0;
            zrLo[n] = z_r.X1; ziLo[n] = z_i.X1;
            zrX2[n] = z_r.X2; ziX2[n] = z_i.X2;
            zrX3[n] = z_r.X3; ziX3[n] = z_i.X3;
            zrX4[n] = z_r.X4; ziX4[n] = z_i.X4;
            zrX5[n] = z_r.X5; ziX5[n] = z_i.X5;
            zrX6[n] = z_r.X6; ziX6[n] = z_i.X6;
            zrX7[n] = z_r.X7; ziX7[n] = z_i.X7;
            if (z_r.X0 * z_r.X0 + z_i.X0 * z_i.X0 >= EscapeRadius2) break;
            OD newZi = (z_r * z_i) * 2.0 + cy;
            z_r = z_r.Square() - z_i.Square() + cx;
            z_i = newZi;
        }
        zr[n]   = z_r.X0; zi[n]   = z_i.X0;
        zrLo[n] = z_r.X1; ziLo[n] = z_i.X1;
        zrX2[n] = z_r.X2; ziX2[n] = z_i.X2;
        zrX3[n] = z_r.X3; ziX3[n] = z_i.X3;
        zrX4[n] = z_r.X4; ziX4[n] = z_i.X4;
        zrX5[n] = z_r.X5; ziX5[n] = z_i.X5;
        zrX6[n] = z_r.X6; ziX6[n] = z_i.X6;
        zrX7[n] = z_r.X7; ziX7[n] = z_i.X7;

        return new OrbitOD
        {
            CentreX  = centreX,  CentreXLo = centreXLo,
            CentreX2 = centreX2, CentreX3  = centreX3,
            CentreX4 = centreX4, CentreX5  = centreX5,
            CentreX6 = centreX6, CentreX7  = centreX7,
            CentreY  = centreY,  CentreYLo = centreYLo,
            CentreY2 = centreY2, CentreY3  = centreY3,
            CentreY4 = centreY4, CentreY5  = centreY5,
            CentreY6 = centreY6, CentreY7  = centreY7,
            MaxIter  = maxIter,
            RefLen   = n,
            Escaped  = n < maxIter,
            Zr   = zr,   Zi   = zi,
            ZrLo = zrLo, ZiLo = ziLo,
            ZrX2 = zrX2, ZiX2 = ziX2,
            ZrX3 = zrX3, ZiX3 = ziX3,
            ZrX4 = zrX4, ZiX4 = ziX4,
            ZrX5 = zrX5, ZiX5 = ziX5,
            ZrX6 = zrX6, ZiX6 = ziX6,
            ZrX7 = zrX7, ZiX7 = ziX7,
        };
    }

    /// <summary>D-6b2 — container for an OD-precision reference orbit.
    /// Arrays sized <c>RefLen + 1</c>.</summary>
    public sealed class OrbitOD
    {
        public required double CentreX   { get; init; }
        public required double CentreXLo { get; init; }
        public required double CentreX2  { get; init; }
        public required double CentreX3  { get; init; }
        public required double CentreX4  { get; init; }
        public required double CentreX5  { get; init; }
        public required double CentreX6  { get; init; }
        public required double CentreX7  { get; init; }
        public required double CentreY   { get; init; }
        public required double CentreYLo { get; init; }
        public required double CentreY2  { get; init; }
        public required double CentreY3  { get; init; }
        public required double CentreY4  { get; init; }
        public required double CentreY5  { get; init; }
        public required double CentreY6  { get; init; }
        public required double CentreY7  { get; init; }
        public required int    MaxIter   { get; init; }
        public required int    RefLen    { get; init; }
        public required bool   Escaped   { get; init; }
        public required double[] Zr   { get; init; }
        public required double[] Zi   { get; init; }
        public required double[] ZrLo { get; init; }
        public required double[] ZiLo { get; init; }
        public required double[] ZrX2 { get; init; }
        public required double[] ZiX2 { get; init; }
        public required double[] ZrX3 { get; init; }
        public required double[] ZiX3 { get; init; }
        public required double[] ZrX4 { get; init; }
        public required double[] ZiX4 { get; init; }
        public required double[] ZrX5 { get; init; }
        public required double[] ZiX5 { get; init; }
        public required double[] ZrX6 { get; init; }
        public required double[] ZiX6 { get; init; }
        public required double[] ZrX7 { get; init; }
        public required double[] ZiX7 { get; init; }
    }

    /// <summary>Seed the internal ref-orbit cache with an externally-
    /// computed DD-precision orbit so the next <see cref="Calculate"/>
    /// short-circuits its own <c>ComputeReferenceOrbit</c> step. Caller
    /// MUST set <see cref="CenterX"/>/<see cref="CenterY"/> (+ Lo limbs)
    /// to match the orbit's centre and call this before
    /// <see cref="Calculate"/>; mismatch is caught by the calculator's
    /// centerSame check inside <c>ComputeReferenceOrbit</c>, which
    /// then ignores the seeded cache and recomputes — correct behaviour
    /// (no silent stale-orbit reuse) but a missed optimisation.
    ///
    /// Arrays are stored by reference; caller must not mutate them after
    /// the call. The calculator's HP fallback consumes the Lo limbs, so
    /// they must be the true DD Lo limbs (not zero placeholders).</summary>
    public void SeedReferenceOrbitDD(OrbitDD orbit)
    {
        if (orbit == null) throw new ArgumentNullException(nameof(orbit));
        if (orbit.RefLen < 0)
            throw new ArgumentException("RefLen must be >= 0", nameof(orbit));
        int sz = orbit.RefLen + 1;
        if (orbit.Zr.Length < sz || orbit.Zi.Length < sz
            || orbit.ZrLo.Length < sz || orbit.ZiLo.Length < sz)
            throw new ArgumentException(
                $"orbit arrays shorter than RefLen+1 ({sz})", nameof(orbit));

        // Allocate (or recycle) the per-limb arrays at maxIter capacity.
        // Existing EnsureRefOrbitCapacity grows-only; pass the orbit's
        // maxIter so a future re-render with a smaller cap reuses the
        // pre-grown arrays without reallocation.
        EnsureRefOrbitCapacity(orbit.MaxIter);

        // Copy supplied Hi/Lo limbs into the calculator's storage. QD/OD
        // limbs zero — DD-precision orbit doesn't carry those, and the HP
        // fallback's centerSame check requires zero for unused limbs.
        Array.Copy(orbit.Zr,   _refZr,   sz);
        Array.Copy(orbit.Zi,   _refZi,   sz);
        Array.Copy(orbit.ZrLo, _refZrLo, sz);
        Array.Copy(orbit.ZiLo, _refZiLo, sz);
        Array.Clear(_refZrX2, 0, sz);  Array.Clear(_refZiX2, 0, sz);
        Array.Clear(_refZrX3, 0, sz);  Array.Clear(_refZiX3, 0, sz);
        Array.Clear(_refZrX4, 0, sz);  Array.Clear(_refZiX4, 0, sz);
        Array.Clear(_refZrX5, 0, sz);  Array.Clear(_refZiX5, 0, sz);
        Array.Clear(_refZrX6, 0, sz);  Array.Clear(_refZiX6, 0, sz);
        Array.Clear(_refZrX7, 0, sz);  Array.Clear(_refZiX7, 0, sz);

        _refOrbitLen = orbit.RefLen;
        _refOrbitGen++;

        // Cache key — set so the next ComputeReferenceOrbit's centerSame
        // test hits. DD-precision: Hi + Lo limbs; remaining limbs zero
        // (matches the orbit's actual derivation centre).
        _refCxHi = orbit.CentreX; _refCxLo = orbit.CentreXLo;
        _refCx2 = 0; _refCx3 = 0; _refCx4 = 0; _refCx5 = 0; _refCx6 = 0; _refCx7 = 0;
        _refCyHi = orbit.CentreY; _refCyLo = orbit.CentreYLo;
        _refCy2 = 0; _refCy3 = 0; _refCy4 = 0; _refCy5 = 0; _refCy6 = 0; _refCy7 = 0;
        _refCachedMaxIter = orbit.MaxIter;
        _refCachedEscaped = orbit.Escaped;
        _refPeriod = 0;   // #1614 — a seeded orbit runs to its own length
        _refCachedTier = 0;  // DD (seeded)
    }

    /// <summary>D-6b2 — seed the internal ref-orbit cache with an
    /// externally-computed QD-precision orbit. Caller MUST set
    /// <see cref="CenterX"/>/<see cref="CenterX2"/>/etc. + Y limbs to
    /// match the orbit's centre and call this before
    /// <see cref="Calculate"/>. Arrays are stored by reference; caller
    /// must not mutate them after the call. OD limbs (X4..X7) zeroed to
    /// match what the QD path's centerSame check expects.</summary>
    public void SeedReferenceOrbitQD(OrbitQD orbit)
    {
        if (orbit == null) throw new ArgumentNullException(nameof(orbit));
        if (orbit.RefLen < 0)
            throw new ArgumentException("RefLen must be >= 0", nameof(orbit));
        int sz = orbit.RefLen + 1;
        if (orbit.Zr.Length < sz || orbit.Zi.Length < sz
            || orbit.ZrLo.Length < sz || orbit.ZiLo.Length < sz
            || orbit.ZrX2.Length < sz || orbit.ZiX2.Length < sz
            || orbit.ZrX3.Length < sz || orbit.ZiX3.Length < sz)
            throw new ArgumentException(
                $"orbit arrays shorter than RefLen+1 ({sz})", nameof(orbit));

        EnsureRefOrbitCapacity(orbit.MaxIter);

        Array.Copy(orbit.Zr,   _refZr,   sz);
        Array.Copy(orbit.Zi,   _refZi,   sz);
        Array.Copy(orbit.ZrLo, _refZrLo, sz);
        Array.Copy(orbit.ZiLo, _refZiLo, sz);
        Array.Copy(orbit.ZrX2, _refZrX2, sz);
        Array.Copy(orbit.ZiX2, _refZiX2, sz);
        Array.Copy(orbit.ZrX3, _refZrX3, sz);
        Array.Copy(orbit.ZiX3, _refZiX3, sz);
        Array.Clear(_refZrX4, 0, sz);  Array.Clear(_refZiX4, 0, sz);
        Array.Clear(_refZrX5, 0, sz);  Array.Clear(_refZiX5, 0, sz);
        Array.Clear(_refZrX6, 0, sz);  Array.Clear(_refZiX6, 0, sz);
        Array.Clear(_refZrX7, 0, sz);  Array.Clear(_refZiX7, 0, sz);

        _refOrbitLen = orbit.RefLen;
        _refOrbitGen++;

        _refCxHi = orbit.CentreX;  _refCxLo = orbit.CentreXLo;
        _refCx2  = orbit.CentreX2; _refCx3  = orbit.CentreX3;
        _refCx4 = 0; _refCx5 = 0; _refCx6 = 0; _refCx7 = 0;
        _refCyHi = orbit.CentreY;  _refCyLo = orbit.CentreYLo;
        _refCy2  = orbit.CentreY2; _refCy3  = orbit.CentreY3;
        _refCy4 = 0; _refCy5 = 0; _refCy6 = 0; _refCy7 = 0;
        _refCachedMaxIter = orbit.MaxIter;
        _refCachedEscaped = orbit.Escaped;
        _refPeriod = 0;   // #1614 — a seeded orbit runs to its own length
        _refCachedTier = 1;  // QD (seeded)
    }

    /// <summary>D-6b2 — seed the internal ref-orbit cache with an
    /// externally-computed OD-precision orbit. Caller MUST set the full
    /// 8-limb centre on <see cref="CenterX"/>..<see cref="CenterX7"/> +
    /// Y limbs and call this before <see cref="Calculate"/>. Arrays are
    /// stored by reference; caller must not mutate them after the call.</summary>
    public void SeedReferenceOrbitOD(OrbitOD orbit)
    {
        if (orbit == null) throw new ArgumentNullException(nameof(orbit));
        if (orbit.RefLen < 0)
            throw new ArgumentException("RefLen must be >= 0", nameof(orbit));
        int sz = orbit.RefLen + 1;
        if (orbit.Zr.Length < sz   || orbit.Zi.Length < sz
            || orbit.ZrLo.Length < sz || orbit.ZiLo.Length < sz
            || orbit.ZrX2.Length < sz || orbit.ZiX2.Length < sz
            || orbit.ZrX3.Length < sz || orbit.ZiX3.Length < sz
            || orbit.ZrX4.Length < sz || orbit.ZiX4.Length < sz
            || orbit.ZrX5.Length < sz || orbit.ZiX5.Length < sz
            || orbit.ZrX6.Length < sz || orbit.ZiX6.Length < sz
            || orbit.ZrX7.Length < sz || orbit.ZiX7.Length < sz)
            throw new ArgumentException(
                $"orbit arrays shorter than RefLen+1 ({sz})", nameof(orbit));

        EnsureRefOrbitCapacity(orbit.MaxIter);

        Array.Copy(orbit.Zr,   _refZr,   sz);
        Array.Copy(orbit.Zi,   _refZi,   sz);
        Array.Copy(orbit.ZrLo, _refZrLo, sz);
        Array.Copy(orbit.ZiLo, _refZiLo, sz);
        Array.Copy(orbit.ZrX2, _refZrX2, sz);
        Array.Copy(orbit.ZiX2, _refZiX2, sz);
        Array.Copy(orbit.ZrX3, _refZrX3, sz);
        Array.Copy(orbit.ZiX3, _refZiX3, sz);
        Array.Copy(orbit.ZrX4, _refZrX4, sz);
        Array.Copy(orbit.ZiX4, _refZiX4, sz);
        Array.Copy(orbit.ZrX5, _refZrX5, sz);
        Array.Copy(orbit.ZiX5, _refZiX5, sz);
        Array.Copy(orbit.ZrX6, _refZrX6, sz);
        Array.Copy(orbit.ZiX6, _refZiX6, sz);
        Array.Copy(orbit.ZrX7, _refZrX7, sz);
        Array.Copy(orbit.ZiX7, _refZiX7, sz);

        _refOrbitLen = orbit.RefLen;
        _refOrbitGen++;

        _refCxHi = orbit.CentreX;  _refCxLo = orbit.CentreXLo;
        _refCx2  = orbit.CentreX2; _refCx3  = orbit.CentreX3;
        _refCx4  = orbit.CentreX4; _refCx5  = orbit.CentreX5;
        _refCx6  = orbit.CentreX6; _refCx7  = orbit.CentreX7;
        _refCyHi = orbit.CentreY;  _refCyLo = orbit.CentreYLo;
        _refCy2  = orbit.CentreY2; _refCy3  = orbit.CentreY3;
        _refCy4  = orbit.CentreY4; _refCy5  = orbit.CentreY5;
        _refCy6  = orbit.CentreY6; _refCy7  = orbit.CentreY7;
        _refCachedMaxIter = orbit.MaxIter;
        _refCachedEscaped = orbit.Escaped;
        _refPeriod = 0;   // #1614 — a seeded orbit runs to its own length
        _refCachedTier = 2;  // OD (seeded)
    }
}