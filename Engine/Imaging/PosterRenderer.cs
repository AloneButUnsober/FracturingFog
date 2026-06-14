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

        public double Zoom { get; init; }
        public int MaxIterations { get; init; }
        public FractalType FractalType { get; init; }
        public IColorMap ColorMap { get; init; } = null!;
        public QualityPreset Quality { get; init; } = null!;
        public FractalParameters FractalParameters { get; init; } = new();

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
                // Mandelbrot path — preserve the full quad-precision centre.
                var calc = new MandelbrotCalculator(req.Width, req.Height)
                {
                    CenterX = req.CenterX,
                    CenterXLo = req.CenterXLo,
                    CenterX2 = req.CenterX2,
                    CenterX3 = req.CenterX3,
                    CenterY = req.CenterY,
                    CenterYLo = req.CenterYLo,
                    CenterY2 = req.CenterY2,
                    CenterY3 = req.CenterY3,
                    Zoom = req.Zoom,
                    MaxIterations = req.MaxIterations,
                    ColorMap = req.ColorMap,
                    Quality = req.Quality,
                };
                calc.Calculate(token);
                token.ThrowIfCancellationRequested();
                buffer = calc.ColorBuffer;
                w = calc.Width;
                h = calc.Height;
            }

            sw.Stop();

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
            // (= "Program vX YYYY"). The render struct can use those verbatim
            // for the default path, or substitute the custom watermark's
            // top-line + colours / placement / justify when supplied. Subtext
            // (program/version) is always req.SubText — the user can re-style
            // and re-place it but not edit or hide it.
            if (req.CustomWatermark != null)
            {
                return new WatermarkRender
                {
                    TopText = req.CustomWatermark.Text ?? string.Empty,
                    SubText = req.SubText ?? string.Empty,
                    TextColor = req.CustomWatermark.TextColor ?? new RgbDef(255, 255, 255),
                    HighlightColor = req.CustomWatermark.HighlightColor,
                    BackgroundColor = req.CustomWatermark.BackgroundColor,
                    Placement = req.CustomWatermark.Placement,
                    Justify = req.CustomWatermark.Justify,
                    IsCustom = true,
                };
            }

            return new WatermarkRender
            {
                TopText = req.Watermark ?? string.Empty,
                SubText = req.SubText ?? string.Empty,
                TextColor = new RgbDef(fontColor.R, fontColor.G, fontColor.B),
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
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
