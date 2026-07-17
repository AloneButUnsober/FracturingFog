// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PosterRenderer.cs
//
// Shell-neutral poster / high-resolution capture engine extracted from the
// MainForm WinForms partials (ImageCapture.cs: BuildAltCalculatorForCapture +
// TakePosterScreenshot). Renders a fresh offscreen calculator at an arbitrary
// resolution, optionally rotates 90° CW for portrait output, picks a
// contrast-aware watermark colour, and saves through ImageExport.
//
// Pure CPU + System.Drawing — no D3D, no WinForms, no MainForm fields. Both the
// legacy WinForms shell and the Avalonia shell construct a PosterRequest and
// call RenderToFile; the only UI-thread work the caller keeps is button-state
// and status text. The render itself is synchronous and cancellable, so callers
// wrap it in Task.Run with their own CancellationToken.

using System;
using System.Drawing;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Immutable description of a poster/high-res capture job. Carries
    /// the full quad-precision centre (Hi + 3 low limbs) so a Mandelbrot deep
    /// zoom survives the offscreen re-render at poster resolution.</summary>
    public sealed class PosterRequest
    {
        // Quad-precision centre — only the Mandelbrot path consumes the low
        // limbs; alt calculators read the Hi halves (CenterX / CenterY) only.
        public double CenterX { get; init; }
        public double CenterXLo { get; init; }
        public double CenterX2 { get; init; }
        public double CenterX3 { get; init; }
        public double CenterY { get; init; }
        public double CenterYLo { get; init; }
        public double CenterY2 { get; init; }
        public double CenterY3 { get; init; }

        // D-6b2 — OD limbs of the centre. Engaged only when the
        // Mandelbrot calculator runs the OD reference-orbit path (zoom
        // > 1e50). DD / QD renders leave them 0.
        public double CenterX4 { get; init; }
        public double CenterX5 { get; init; }
        public double CenterX6 { get; init; }
        public double CenterX7 { get; init; }
        public double CenterY4 { get; init; }
        public double CenterY5 { get; init; }
        public double CenterY6 { get; init; }
        public double CenterY7 { get; init; }

        public double Zoom { get; init; }
        public int MaxIterations { get; init; }
        public FractalType FractalType { get; init; }
        public IColorMap ColorMap { get; init; } = null!;
        public QualityPreset Quality { get; init; } = null!;
        public FractalParameters FractalParameters { get; init; } = new();

        // Post-FX (parity with the interactive ViewState sliders). Defaults =
        // identity. Brightness/Contrast are a BGRA post-pass; HistogramEq is
        // adaptive equalization strength applied on the calculator before the
        // colour buffer is read (Mandelbrot only).
        public int Brightness { get; init; }   // -100..100, 0 = none
        public int Contrast { get; init; }     // -100..100, 0 = none
        public int HistogramEq { get; init; }  //    0..100, 0 = none

        /// <summary>Landscape render dimensions. When <see cref="Rotate"/> is
        /// set the saved image is the 90°-rotated transpose of these.</summary>
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>Rotate the landscape render 90° clockwise before saving
        /// (portrait output / explicit rotate request).</summary>
        public bool Rotate { get; init; }

        public string Path { get; init; } = "";
        public ImageFileFormat Format { get; init; } = ImageFileFormat.Png;
        public string Watermark { get; init; } = "";
        public string SubText { get; init; } = "";

        /// <summary>Optional custom watermark to composite onto the saved
        /// poster. When non-null, replaces the default Watermark / SubText
        /// composition; the SubText (program/version) still appears beneath the
        /// user's top-line, built by <see cref="WatermarkResolver.Resolve"/>.</summary>
        public WatermarkDef? CustomWatermark { get; init; }

        /// <summary>Pixels-per-inch metadata stamped into the saved file. 0 =
        /// leave whatever the encoder defaults to (96 dpi). Set when the
        /// caller wants the print pipeline to honour a poster size — print
        /// drivers read this to scale the image to physical inches without
        /// the user having to type a size at print time.</summary>
        public float Dpi { get; init; }

        // ── D-6b — sub-rect rendering for cluster tile workers ─────────────
        // When non-zero, the worker renders only a sub-rect of a larger
        // image and derives per-pixel dc from the FULL image's centre +
        // dims, so every tile of the same image shares the master-shipped
        // reference orbit. Zero = legacy single-render / per-tile-centre.

        public int ImageWidth     { get; init; }
        public int ImageHeight    { get; init; }
        public int SubRectOffsetX { get; init; }
        public int SubRectOffsetY { get; init; }

        /// <summary>D-6b — master-computed DD reference orbit. When non-null
        /// (and the centre + maxIter match), the calculator's
        /// ComputeReferenceOrbit step short-circuits and the per-tile
        /// recompute is skipped. <c>null</c> = legacy compute-per-tile.</summary>
        public MandelbrotCalculator.OrbitDD? SeededOrbit { get; init; }

        /// <summary>D-6b2 — QD-precision shared reference orbit (zoom &gt;
        /// 1e25). Set by the host when the master ships a QD-limbs blob.
        /// At most one of SeededOrbit / SeededOrbitQD / SeededOrbitOD is
        /// non-null per render; the calculator's centerSame check
        /// enforces zero limbs for whichever isn't shipped.</summary>
        public MandelbrotCalculator.OrbitQD? SeededOrbitQD { get; init; }

        /// <summary>D-6b2 — OD-precision shared reference orbit (zoom &gt;
        /// 1e50). Same single-active-orbit constraint as
        /// <see cref="SeededOrbitQD"/>.</summary>
        public MandelbrotCalculator.OrbitOD? SeededOrbitOD { get; init; }
    }

    /// <summary>Outcome of a poster render — the on-disk pixel dimensions
    /// (post-rotation) and how long the calculation took.</summary>
    public readonly struct PosterResult
    {
        public PosterResult(int savedWidth, int savedHeight, long elapsedMs)
        {
            SavedWidth = savedWidth;
            SavedHeight = savedHeight;
            ElapsedMs = elapsedMs;
        }
        public int SavedWidth { get; }
        public int SavedHeight { get; }
        public long ElapsedMs { get; }
    }

    /// <summary>Offscreen high-resolution fractal renderer shared by both shells.</summary>
    public static class PosterRenderer
    {
        /// <summary>
        /// Render the request to an offscreen buffer and save it. Synchronous and
        /// cancellable — callers run this on a background thread. Throws on render
        /// or save failure (callers surface the message in their own UI).
        /// </summary>
        public static PosterResult RenderToFile(PosterRequest req, CancellationToken token)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.Width <= 0 || req.Height <= 0)
                throw new ArgumentException("Poster dimensions must be positive.", nameof(req));

            var sw = System.Diagnostics.Stopwatch.StartNew();

            uint[] buffer;
            int w, h;

            IFractalCalculator? alt = BuildCaptureCalculator(req);
            if (alt != null)
            {
                alt.Calculate(token);
                token.ThrowIfCancellationRequested();
                buffer = alt.ColorBuffer;
                w = alt.Width;
                h = alt.Height;
            }
            else
            {
                // Mandelbrot path — preserve the full octuple-precision centre
                // (DD/QD/OD limbs as supplied; unused limbs stay 0).
                var calc = new MandelbrotCalculator(req.Width, req.Height)
                {
                    CenterX = req.CenterX,
                    CenterXLo = req.CenterXLo,
                    CenterX2 = req.CenterX2,
                    CenterX3 = req.CenterX3,
                    CenterX4 = req.CenterX4,
                    CenterX5 = req.CenterX5,
                    CenterX6 = req.CenterX6,
                    CenterX7 = req.CenterX7,
                    CenterY = req.CenterY,
                    CenterYLo = req.CenterYLo,
                    CenterY2 = req.CenterY2,
                    CenterY3 = req.CenterY3,
                    CenterY4 = req.CenterY4,
                    CenterY5 = req.CenterY5,
                    CenterY6 = req.CenterY6,
                    CenterY7 = req.CenterY7,
                    Zoom = req.Zoom,
                    MaxIterations = req.MaxIterations,
                    ColorMap = req.ColorMap,
                    Quality = req.Quality,
                    // D-6b — sub-rect + seeded orbit for cluster tile workers.
                    // All four properties default to 0 (= legacy full-image render);
                    // any non-zero value engages the sub-rect dc geometry.
                    ImageWidth     = req.ImageWidth,
                    ImageHeight    = req.ImageHeight,
                    SubRectOffsetX = req.SubRectOffsetX,
                    SubRectOffsetY = req.SubRectOffsetY,
                };
                if (req.SeededOrbit != null)
                {
                    // Pre-fill the calculator's ref-orbit cache so its
                    // internal ComputeReferenceOrbit hits the centre-cache
                    // short-circuit. Mismatched centre / insufficient cap
                    // is detected by the calculator and falls back to
                    // per-tile compute — no silent stale-orbit reuse.
                    calc.SeedReferenceOrbitDD(req.SeededOrbit);
                }
                else if (req.SeededOrbitQD != null)
                {
                    // D-6b2 — QD orbit path (zoom > 1e25).
                    calc.SeedReferenceOrbitQD(req.SeededOrbitQD);
                }
                else if (req.SeededOrbitOD != null)
                {
                    // D-6b2 — OD orbit path (zoom > 1e50).
                    calc.SeedReferenceOrbitOD(req.SeededOrbitOD);
                }
                calc.Calculate(token);
                token.ThrowIfCancellationRequested();
                // Adaptive HE — Mandelbrot-only, applied on the calculator so
                // it recolours from the iteration histogram before read.
                if (req.HistogramEq > 0)
                    calc.ApplyHistogramEqualization(req.HistogramEq / 100.0);
                buffer = calc.ColorBuffer;
                w = calc.Width;
                h = calc.Height;
            }

            sw.Stop();

            // Brightness/Contrast BGRA post-pass (both calculator paths).
            ApplyBrightnessContrast(buffer, w * h, req.Brightness, req.Contrast);

            if (req.Rotate)
            {
                // 90° clockwise: landscape (w×h) becomes portrait (h×w).
                var rotated = new uint[buffer.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        rotated[x * h + (h - 1 - y)] = buffer[y * w + x];

                int savedW = h, savedH = w;
                var fontColor = ImageExport.ComputeContrastColor(
                    Color.White, watermark: true, pixels: rotated, imgW: savedW, imgH: savedH);
                var wm = BuildPosterWatermark(req, fontColor);
                ImageExport.SavePixelsToFile(
                    rotated, savedW, savedH, req.Path, req.Format,
                    wm, poster: true, dpi: req.Dpi);
                return new PosterResult(savedW, savedH, sw.ElapsedMilliseconds);
            }
            else
            {
                var fontColor = ImageExport.ComputeContrastColor(
                    Color.White, watermark: true, pixels: buffer, imgW: w, imgH: h);
                var wm = BuildPosterWatermark(req, fontColor);
                ImageExport.SavePixelsToFile(
                    buffer, w, h, req.Path, req.Format,
                    wm, poster: true, dpi: req.Dpi);
                return new PosterResult(w, h, sw.ElapsedMilliseconds);
            }
        }

        // In-place brightness/contrast BGRA post-pass. Same math as
        // FractalRenderHost.UploadProcessedBuffer so poster output matches the
        // interactive image: contrast pivots around mid-grey (127.5), then
        // brightness offsets in 0..255 space.
        private static void ApplyBrightnessContrast(uint[] buf, int n, int brightness, int contrast)
        {
            if (brightness == 0 && contrast == 0) return;
            float contrastFactor = 1f + contrast / 100f;
            float brightnessOffset255 = brightness / 100f * 255f;
            int len = Math.Min(n, buf.Length);
            for (int i = 0; i < len; i++)
            {
                uint p = buf[i];
                float r = (p >> 16) & 0xFF;
                float g = (p >> 8) & 0xFF;
                float b = p & 0xFF;
                r = (r - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                g = (g - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                b = (b - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                byte R = (byte)Math.Clamp(r, 0f, 255f);
                byte G = (byte)Math.Clamp(g, 0f, 255f);
                byte B = (byte)Math.Clamp(b, 0f, 255f);
                buf[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }

        private static WatermarkRender? BuildPosterWatermark(PosterRequest req, Color fontColor)
        {
            // No top-line + no sub-line + no custom override = nothing to draw.
            if (req.CustomWatermark == null
                && string.IsNullOrEmpty(req.Watermark)
                && string.IsNullOrEmpty(req.SubText))
            {
                return null;
            }

            // The caller (FractalRenderHost.CreatePosterRequest) pre-composes
            // req.Watermark (= "Region - Theme") and req.SubText
            // (= "Program vX YYYY"), so those go in as the already-formatted
            // default rather than being re-derived from region/theme here.
            // Precedence itself belongs to WatermarkResolver — the shell has
            // already collapsed the chain into req.CustomWatermark, so a
            // non-null value is by construction the active choice.
            var wm = WatermarkResolver.Resolve(
                activeCustom: req.CustomWatermark,
                regionEmbedded: null,
                overrideRegionWatermark: req.CustomWatermark != null,
                useCustomWatermark: req.CustomWatermark != null,
                regionName: req.Watermark ?? string.Empty,
                themeName: string.Empty,
                programName: string.Empty,
                programVersion: string.Empty,
                defaultTextColor: new RgbDef(fontColor.R, fontColor.G, fontColor.B));

            // Resolve composes its own program/version sub-line from the
            // program name/version it is handed; the poster path already has
            // the formatted string, so keep that one.
            return new WatermarkRender
            {
                TopText = wm.TopText,
                SubText = req.SubText ?? string.Empty,
                TextColor = wm.TextColor,
                HighlightColor = wm.HighlightColor,
                BackgroundColor = wm.BackgroundColor,
                Placement = wm.Placement,
                Justify = wm.Justify,
                IsCustom = wm.IsCustom,
            };
        }

        /// <summary>
        /// Build a fresh calculator at the request's resolution matching its
        /// fractal type + parameters. Returns null for Mandelbrot (the caller
        /// uses the MandelbrotCalculator branch to preserve QD-limb state).
        /// Mirrors the legacy MainForm.BuildAltCalculatorForCapture switch.
        /// </summary>
        public static IFractalCalculator? BuildCaptureCalculator(PosterRequest req)
        {
            int w = req.Width, h = req.Height;
            FractalType type = req.FractalType;

            IFractalCalculator? c = type switch
            {
                FractalType.Mandelbrot       => null,
                FractalType.Julia            => new EscapeTimeCalculator(w, h),
                FractalType.BurningShip      => new EscapeTimeCalculator(w, h),
                FractalType.Tricorn          => new EscapeTimeCalculator(w, h),
                FractalType.Multibrot        => new EscapeTimeCalculator(w, h),
                FractalType.Phoenix          => new EscapeTimeCalculator(w, h),
                FractalType.Magnet1          => new EscapeTimeCalculator(w, h),
                FractalType.Magnet2          => new EscapeTimeCalculator(w, h),
                FractalType.Glynn            => new EscapeTimeCalculator(w, h),
                FractalType.Spider           => new EscapeTimeCalculator(w, h),
                FractalType.Logistic         => new LogisticCalculator(w, h),
                FractalType.Halley           => new HalleyCalculator(w, h),
                FractalType.Secant           => new SecantCalculator(w, h),
                FractalType.IFS              => new IFSCalculator(w, h),
                FractalType.LSystem          => new LSystemCalculator(w, h),
                FractalType.StrangeAttractor => new AttractorCalculator(w, h),
                FractalType.BuddhaBrot       => new BuddhabrotCalculator(w, h),
                FractalType.Nebulabrot       => new NebulabrotCalculator(w, h),
                FractalType.AntiBuddhabrot   => new AntiBuddhabrotCalculator(w, h),
                FractalType.AntiNebulabrot   => new AntiNebulabrotCalculator(w, h),
                FractalType.Newton           => new NewtonCalculator(w, h),
                FractalType.Nova             => new NewtonCalculator(w, h),
                FractalType.UserEquation     => new UserEquationCalculator(w, h),
                FractalType.Mandelbulb       => new MandelbulbCalculator(w, h),
                FractalType.Mandelbox        => new MandelboxCalculator(w, h),
                FractalType.Kifs             => new KifsCalculator(w, h),
                FractalType.QuaternionJulia  => new QuatJuliaCalculator(w, h),
                FractalType.QuaternionMandelbrot => new QuatMandelbrotCalculator(w, h),
                FractalType.Plasma           => new PlasmaCalculator(w, h),
                FractalType.Apollonian       => new ApollonianCalculator(w, h),
                FractalType.Kleinian         => new KleinianCalculator(w, h),
                FractalType.BicomplexMandelbrot => new BicomplexMandelbrotCalculator(w, h),
                FractalType.Dla              => new DlaCalculator(w, h),
                FractalType.Flame            => new FlameRenderer(w, h),
                FractalType.Sandbox          => new SandboxCalculator(w, h),
                FractalType.UserBulb         => new UserBulbCalculator(w, h),
                _                            => null
            };
            if (c == null) return null;

            c.CenterX = req.CenterX;
            c.CenterY = req.CenterY;
            c.Zoom = req.Zoom;
            c.MaxIterations = req.MaxIterations;
            c.Quality = req.Quality;
            c.ColorMap = req.ColorMap;

            switch (c)
            {
                case EscapeTimeCalculator e:
                    e.FractalType = type;
                    e.FractalParameters = req.FractalParameters;
                    break;
                case IFSCalculator ifs:        ifs.FractalParameters = req.FractalParameters; break;
                case LSystemCalculator ls:     ls.FractalParameters = req.FractalParameters; break;
                case AttractorCalculator a:    a.FractalParameters = req.FractalParameters; break;
                case BuddhaFamilyCalculator b: b.FractalParameters = req.FractalParameters; break;
                case NewtonCalculator n:       n.FractalParameters = req.FractalParameters; break;
                case UserEquationCalculator u: u.FractalParameters = req.FractalParameters; break;
                case MandelbulbCalculator m:   m.FractalParameters = req.FractalParameters; break;
                case MandelboxCalculator mb:   mb.FractalParameters = req.FractalParameters; break;
                case KifsCalculator kf:        kf.FractalParameters = req.FractalParameters; break;
                case QuatJuliaCalculator qj:   qj.FractalParameters = req.FractalParameters; break;
                case QuatMandelbrotCalculator qm: qm.FractalParameters = req.FractalParameters; break;
                case PlasmaCalculator pl:      pl.FractalParameters = req.FractalParameters; break;
                case ApollonianCalculator ap:  ap.FractalParameters = req.FractalParameters; break;
                case KleinianCalculator kl:    kl.FractalParameters = req.FractalParameters; break;
                case BicomplexMandelbrotCalculator bc: bc.FractalParameters = req.FractalParameters; break;
                case DlaCalculator dl:         dl.FractalParameters = req.FractalParameters; break;
                case FlameRenderer fr:         fr.FractalParameters = req.FractalParameters; break;
                case SandboxCalculator sb:     sb.FractalParameters = req.FractalParameters; break;
                case UserBulbCalculator ub:    ub.FractalParameters = req.FractalParameters; break;
            }
            return c;
        }
    }
}
