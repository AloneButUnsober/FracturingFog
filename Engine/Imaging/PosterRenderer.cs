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

        // S6 (#408) — optional persistent froxel temporal-reprojection history. A
        // sequence renderer (e.g. SceneVideoRenderer) hands the SAME instance to every
        // frame so animated fog blends across frames (needs Relief2DFroxelTemporal on).
        // Null (the default) = single-frame froxel, byte-identical to before.
        public FracturingFog.Rendering.Lighting.FroxelHistory? FroxelHistory { get; init; }

        // S1 (#398) — optional previous-frame relief camera for the motion-vector AOV.
        // A sequence renderer captures frame N's camera (the aovCapture's CurrentCamera,
        // set by the render) and hands it back here on frame N+1 so the render fills the
        // Motion channel of a supplied capture-motion aovCapture. Null (the default) =
        // no motion fill (byte-identical); only meaningful with a motion-capturing AOV.
        public FracturingFog.Rendering.Lighting.ReliefMotionVector.CameraView? PreviousCamera { get; init; }

        // S4 (#402) — optional persistent SVGF denoise history. A sequence renderer hands
        // the SAME instance to every frame so the relief denoise accumulates temporally
        // (reproject + variance-guided À-Trous) when Relief2DDenoiseTemporal is on. The
        // pass updates the history's PrevCamera each frame, which the caller threads back
        // as PreviousCamera. Null (the default) = the plain single-frame denoise.
        public SvgfHistory? SvgfHistory { get; init; }

        // #508 — the interactive view's dedicated HI-RES relief field (the host's
        // _reliefFieldCalc floor field, Relief2DHiResField). When supplied, the
        // offscreen relief RAYMARCH uses this field instead of re-deriving the
        // calculator's coarser SmoothBuffer, so a poster / wallpaper's Relief 3D
        // matches the on-screen frame (WYSIWYG). Null → fall back to SmoothBuffer
        // (batch / no host field), the pre-#508 behaviour. Dims are the FIELD grid,
        // decoupled from the output size (HeightDe samples by normalised coords).
        public float[]? ReliefField { get; init; }
        public int ReliefFieldW { get; init; }
        public int ReliefFieldH { get; init; }

        // Post-FX (parity with the interactive ViewState sliders). Defaults =
        // identity. Brightness/Contrast are a BGRA post-pass; HistogramEq is
        // adaptive equalization strength applied on the calculator before the
        // colour buffer is read (Mandelbrot only).
        public int Brightness { get; init; }   // -100..100, 0 = none
        public int Contrast { get; init; }     // -100..100, 0 = none
        // F6 part 2 — image gamma slider parity with the interactive present.
        // Same encoding as ViewState.Gamma (−100..100, 0 = none); gamma =
        // 2^(slider/100). Without this the offscreen poster silently dropped the
        // live gamma the on-screen frame applied (FractalRenderHost.UploadProcessedBuffer).
        public int Gamma { get; init; }        // -100..100, 0 = none
        public int HistogramEq { get; init; }  //    0..100, 0 = none

        /// <summary>S2 (#389) — output-stage view transform / tonemap, parity with
        /// <c>ViewState.ViewTransform</c>. Default None = identity (byte-identical).</summary>
        public ViewTransform ViewTransform { get; init; } = ViewTransform.None;
        /// <summary>S2 (#389) — exposure in stops before the view transform; 0 =
        /// neutral. Parity with <c>ViewState.ViewExposureEv</c>.</summary>
        public float ViewExposureEv { get; init; }

        /// <summary>F11 ordered-dither deband of the palette float→byte quantise
        /// (CPU F11a + GPU F11b). Off = plain truncate/round. The colour pipeline
        /// reads this through process-global <see cref="GradientColorMap"/> statics,
        /// which <see cref="PosterRenderer.RenderToFile"/> sets from this request
        /// (and restores afterwards) so a headless / batch render is deterministic
        /// regardless of whatever the last interactive frame left in those globals.</summary>
        public bool BandDither { get; init; }

        /// <summary>Ordered-dither amplitude in [0,100]; 100 = full ±0.5-LSB spread.
        /// Only consulted when <see cref="BandDither"/> is on.</summary>
        public int BandDitherStrength { get; init; } = 100;

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

        /// <summary>Compression for a <c>.exr</c> save (roadmap S7, #394).
        /// Default None = uncompressed / byte-stable; Zip = smaller, lossless,
        /// not byte-stable. Ignored for non-EXR formats.</summary>
        public ExrCompression ExrCompression { get; init; } = ExrCompression.None;

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
        /// <summary>#189 (performance safety) — a conservative estimate of the peak
        /// managed memory (bytes) a poster render of <paramref name="width"/> ×
        /// <paramref name="height"/> holds live at once: the final ARGB image, the
        /// calculator's colour + iteration/smooth aux buffers, the relief field +
        /// colour scratch when relief is on, and the rotation copy when the output
        /// is rotated. Deliberately high so the shell warns before an out-of-memory
        /// render rather than after. Overflow-safe (uses long math).</summary>
        public static long EstimatePeakBytes(int width, int height, bool relief, bool rotate)
        {
            long px = Math.Max(0L, (long)width) * Math.Max(0, height);
            // ARGB colour buffer = 4 B/px. Mandelbrot keeps iteration (int) +
            // smooth (double) + escape aux alongside the colour buffer, so budget
            // ~5× the colour buffer for a flat render. Relief adds the (possibly
            // hi-res) height field + a colour scratch + per-hit work: ~8×.
            long perPixel = relief ? 8L * 4L : 5L * 4L;
            long bytes = px * perPixel;
            if (rotate) bytes += px * 4L;   // separate rotated destination buffer
            return bytes;
        }

        /// <summary>#189 — total physical memory the runtime currently believes is
        /// available to the process, for comparing against
        /// <see cref="EstimatePeakBytes"/>.</summary>
        public static long AvailableMemoryBytes()
        {
            var info = GC.GetGCMemoryInfo();
            long avail = info.TotalAvailableMemoryBytes;
            return avail > 0 ? avail : 0;
        }

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

            // S2 (#396) — CORE true-linear intermediate producer wiring. When a view
            // transform is active on a relief-RAYMARCH poster with a NEUTRAL grade,
            // capture the render's pre-clamp HDR beauty so the transform tonemaps real
            // highlight headroom instead of the clamped 8-bit buffer. Gated on a
            // neutral brightness/contrast/gamma because that grade is applied to the
            // 8-bit buffer BEFORE the transform — with it neutral the fallback buffer
            // equals the raw beauty, so terrain-HDR and sky-fallback pixels stay
            // consistent. Any other case falls through to the unchanged 8-bit path.
            bool neutralGrade = req.Brightness == 0 && req.Contrast == 0 && req.Gamma == 0;
            // S12.1 (#652) — relief stage-2 Tone Map / Bloom consume the same HDR beauty,
            // so capture it when an FX tonemap/bloom is active too, not just a view
            // transform (exposure alone is a pre-tonemap multiply, inert on its own).
            bool fxTonemap = req.FractalParameters is not null
                && (req.FractalParameters.Lighting.ToneMap != FracturingFog.Rendering.Lighting.ToneMapOperator.None
                    || req.FractalParameters.Lighting.BloomStrength > 0.0);
            bool wantHdr = (req.ViewTransform != ViewTransform.None || fxTonemap) && neutralGrade
                && req.FractalParameters is { Relief2DEnabled: true, Relief2DRaymarch: true }
                // The HDR plane is the PRE-denoise beauty; tonemapping it would drop a
                // guided denoise, so the HDR headroom path is used only when denoise is
                // off. Denoise + transform keeps the (denoised) 8-bit tonemap.
                && !ReliefDenoisePass.Enabled(req.FractalParameters);
                // S12 (#655/#652) — froxel-in-HDR: the froxel post-pass now composites its
                // volume into the captured HDR beauty too (HeightfieldRaymarch2D →
                // FroxelCameraVolume.Apply(..., aov.HdrBeauty)), so froxel fog survives the
                // tonemap and HDR capture is no longer gated off under froxel.
            // S12.3/S12.4 (#652) — SSAO + edge ink key on the relief normal + depth
            // G-buffer (always allocated on any capture; the GPU kernel emits it, so it
            // doesn't force the CPU trace). Capture whenever either is active, even if
            // the HDR gate is off (geometry needs no headroom / froxel-free beauty).
            bool wantGeom = false;
            if (req.FractalParameters is { Relief2DEnabled: true, Relief2DRaymarch: true })
            {
                var geomFx = req.FractalParameters.Lighting;
                // S12.5 (#652) — relief stereo (depth-parallax SBS) reads the same depth
                // G-buffer, so arm the geom capture when stereo is wanted too.
                wantGeom = ReliefScreenSpacePost.WantsGeom(in geomFx)
                    || ReliefScreenSpacePost.WantsStereo(in geomFx);
            }
            var hdrAov = (wantHdr || wantGeom)
                ? new FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers(
                    req.Width, req.Height, false, false, captureHdr: wantHdr)
                : null;

            uint[] buffer = RenderComposedBuffer(req, token, out int w, out int h, hdrAov);

            sw.Stop();

            // Brightness/Contrast/Gamma BGRA post-pass (both calculator paths).
            // Alpha is PRESERVED here (F10.3) so the interior-alpha composite below
            // still sees the authored coverage byte.
            ApplyBrightnessContrastGamma(buffer, w * h, req.Brightness, req.Contrast, req.Gamma);

            // S12 (#652) — relief STAGE-2 post chain, matching the live path
            // (FractalRenderHost): Tone Map + Exposure + Bloom (S12.1, over the captured
            // HDR beauty) + Lens (S12.2) + SSAO (S12.4) + Edge ink (S12.3, over the
            // normal+depth G-buffer). When any pass runs the HDR view-transform path is
            // dropped (it would overwrite these display-space passes) — the view
            // transform below then stacks on the 8-bit buffer.
            bool reliefStage2Applied = false;
            if (req.FractalParameters is { Relief2DEnabled: true, Relief2DRaymarch: true })
            {
                var reliefFx = req.FractalParameters.Lighting;
                reliefStage2Applied = ReliefScreenSpacePost.ApplyStage2(buffer, hdrAov, w, h, in reliefFx);
            }

            // S2 (#389/#396) — output-stage view transform (tonemap), layered on the
            // b/c/gamma post-pass exactly as the live path does, so a poster matches
            // the on-screen frame. None = no-op (byte-identical). When the relief HDR
            // beauty was captured (wantHdr above), tonemap the true-linear intermediate
            // (headroom recovered); else the plain 8-bit path. A buffer with no HDR
            // sample decodes identically to the 8-bit path (FromHdrByteScale contract),
            // so the HDR branch never regresses a non-relief pixel.
            // When a linear composite runs below the interior backdrop is folded in
            // there (in linear light) and the trailing 8-bit composite is skipped.
            bool linearComposited = false;
            if (req.ViewTransform != ViewTransform.None)
            {
                if (!reliefStage2Applied && hdrAov?.HdrBeauty != null && hdrAov.HdrBeauty.Length == (long)w * h * 3)
                    buffer = LinearFloatImage
                        .FromHdrByteScale(hdrAov.HdrBeauty, buffer, w, h)
                        .ApplyViewTransform(req.ViewTransform, req.ViewExposureEv)
                        .ToBgra();
                else
                {
                    // S2 (#396) — FULL-FLOAT 2D composite. Decode the graded buffer to
                    // a linear-light image, composite the interior backdrop IN LINEAR
                    // (so the backdrop is tonemapped with the fractal, not injected
                    // untonemapped after), then apply the view transform on the whole
                    // composited image. Opaque / no-backdrop frames leave the image
                    // untouched, so this reduces to FromBgra → transform → ToBgra,
                    // byte-identical to the old 8-bit ViewTransformOps.Apply (parity
                    // anchor), and the 8-bit composite below still runs as a no-op.
                    var img = LinearFloatImage.FromBgra(buffer, w, h);
                    linearComposited = FracturingFog.Rendering.Interior2DBackgroundCompositor.CompositeLinear(
                        img, buffer, req.FractalParameters,
                        req.ColorMap?.InSetColor ?? 0xFF000000u, alphaPreview: false);
                    buffer = img
                        .ApplyViewTransform(req.ViewTransform, req.ViewExposureEv)
                        .ToBgra();
                }
            }

            // Interior-alpha composite — the SAME shared helper the live path
            // (FractalRenderHost.UploadProcessedBuffer) calls, so a poster/wallpaper
            // matches the on-screen window pixel-for-pixel. The D3D present ignores
            // the alpha channel, so authored translucency (interior alpha + per-stop
            // exterior alpha) only shows once composited over the chosen
            // Interior2DBackground; without it the offscreen render wrote a straight-
            // alpha PNG that washed out over a viewer's white background. In-place:
            // the b/c/gamma pass above preserved the coverage byte, so the same buffer
            // supplies both RGB and coverage. Transparent mode keeps straight alpha.
            // Skipped when the full-float path above already composited in linear.
            if (!linearComposited)
                FracturingFog.Rendering.Interior2DBackgroundCompositor.Composite(
                    buffer, buffer, w, h, req.FractalParameters,
                    req.ColorMap?.InSetColor ?? 0xFF000000u,
                    alphaPreview: false, srcAlreadyProcessed: false);

            // S12.5 (#652) — relief STEREO (depth-parallax side-by-side), matching the
            // live path (FractalRenderHost). Runs LAST, on the fully composited display
            // buffer, and doubles the poster dims (Full-SBS) or keeps them (Half-SBS).
            if (req.FractalParameters is { Relief2DEnabled: true, Relief2DRaymarch: true })
            {
                var stereoFx = req.FractalParameters.Lighting;
                var sbs = ReliefScreenSpacePost.ApplyStereo(buffer, hdrAov, w, h, in stereoFx,
                    out int stereoW, out int stereoH);
                if (sbs != null) { buffer = sbs; w = stereoW; h = stereoH; }
            }

            return WritePoster(req, buffer, w, h, sw.ElapsedMilliseconds);
        }

        /// <summary>Render the composed scene buffer — calculator colour + #102
        /// heightfield relief — but WITHOUT the output post-pipeline (brightness /
        /// contrast / gamma, S2 view transform, interior-alpha composite). This is
        /// the raw pass the AOV export orchestrator captures per <c>DebugAov</c>
        /// (roadmap S1, #389): grading + tonemap would corrupt data AOVs (normals /
        /// depth), so they are applied only on the file path above.</summary>
        internal static uint[] RenderComposedBuffer(PosterRequest req, CancellationToken token, out int w, out int h,
            FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers? aovCapture = null)
        {
            uint[] buffer;

            // F11 deband — the colour pipeline reads dither state from process-global
            // GradientColorMap statics, so set them from this request for the render
            // and restore afterwards. Explicit here (not inherited from ambient global)
            // so a headless / batch / server render debands deterministically instead
            // of depending on whatever the last interactive frame happened to leave set.
            bool prevDither = GradientColorMap.DitherEnabled;
            float prevDitherStrength = GradientColorMap.DitherStrength;
            GradientColorMap.DitherEnabled = req.BandDither;
            GradientColorMap.DitherStrength = Math.Clamp(req.BandDitherStrength, 0, 100) / 100f;
            try
            {

            // #656 — resolve an aspect-correct relief field ONCE (the caller snapshot
            // when its aspect matches the output, else a field recomputed at the output
            // aspect); shared by the alt and Mandelbrot relief calls below. null → the
            // relief path uses the height source's own (output-dims) SmoothBuffer.
            float[]? reliefField = ResolveReliefField(req, req.Width, req.Height, token, out int reliefFw, out int reliefFh);

            IFractalCalculator? alt = BuildCaptureCalculator(req);
            if (alt != null)
            {
                alt.Calculate(token);
                token.ThrowIfCancellationRequested();
                // Adaptive HE — #145: escape-time alt calculators (Julia,
                // BurningShip, Tricorn, Multibrot, Phoenix, Magnet1/2, Glynn,
                // Spider) equalize through the shared core just like Mandelbrot.
                // Non-escape-time families don't implement the capability and
                // fall through unchanged.
                if (req.HistogramEq > 0 && alt is ISupportsHistogramEq heAlt)
                    heAlt.ApplyHistogramEqualization(req.HistogramEq / 100.0);
                buffer = alt.ColorBuffer;
                w = alt.Width;
                h = alt.Height;
                // #102 heightfield relief for non-Mandelbrot families. The
                // escape-time alt calculators (Julia, BurningShip, Tricorn,
                // Multibrot, Phoenix, Magnet, Glynn, Spider, Newton, …) expose
                // the same SmoothBuffer height field as Mandelbrot, so a poster
                // of a Relief 3D scene must apply relief here too — otherwise it
                // silently falls back to the flat 2D themed colour. No-op when
                // relief is off or the calc exposes no field.
                buffer = ApplyReliefIfEnabled(buffer, alt as IHeightFieldSource, w, h, req.FractalParameters, alt?.ColorMap, aovCapture, req.FroxelHistory, reliefField, reliefFw, reliefFh, req.PreviousCamera, req.SvgfHistory);
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
                    // #384: honor the global interior-alpha knob on export. The
                    // live path sets _calculator.InteriorAlpha; PosterRenderer
                    // dropped it, so image/poster export ignored the slider for
                    // every type (interiors rendered opaque even when the window
                    // showed them translucent). Theme InSetColor.A already worked
                    // — it is baked into the buffer at the in-set write.
                    // NB: no ?. — PosterRequest.FractalParameters is non-nullable
                    // (= new()); a null-conditional here poisons the flow state
                    // and trips CS8604 at the ApplyReliefIfEnabled call below.
                    InteriorAlpha = req.FractalParameters.InteriorAlpha,
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

                // #102 heightfield relief — modulate the flat themed colour with
                // real raised 3D relief so a poster / wallpaper matches the
                // on-screen Relief 3D frame (UploadProcessedBuffer does the same
                // for the interactive path). The non-Mandelbrot alt families are
                // handled by the same call in the alt branch above. Field ==
                // output dims here: a poster is already high-res, so the
                // display-size undersampling the hi-res field works around does
                // not apply — BUT the dedicated hi-res relief field is a separate,
                // higher-quality field, not just a resolution bump, so a supplied
                // req.ReliefField (the interactive one) is preferred (#508).
                buffer = ApplyReliefIfEnabled(buffer, calc, w, h, req.FractalParameters, calc.ColorMap, aovCapture, req.FroxelHistory, reliefField, reliefFw, reliefFh, req.PreviousCamera, req.SvgfHistory);
            }

            }
            finally
            {
                GradientColorMap.DitherEnabled = prevDither;
                GradientColorMap.DitherStrength = prevDitherStrength;
            }

            return buffer;
        }

        /// <summary>Render the composed scene buffer as a straight pixel array —
        /// calculator colour + relief, no output post-pipeline — cloned so the
        /// caller owns it (roadmap S1, #389; the AOV export orchestrator captures
        /// one of these per <c>DebugAov</c>).</summary>
        public static uint[] RenderToPixels(PosterRequest req, CancellationToken token, out int w, out int h)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.Width <= 0 || req.Height <= 0)
                throw new ArgumentException("Poster dimensions must be positive.", nameof(req));
            return (uint[])RenderComposedBuffer(req, token, out w, out h).Clone();
        }

        /// <summary>Render the composed scene buffer AND fill <paramref name="aovCapture"/>
        /// (world-space float normal + world-units depth) from the SAME pass (roadmap
        /// S1/S7, #389). Supplying a non-null capture forces the relief CPU trace (the
        /// GPU kernel emits no AOVs), so the geometry planes are the render's own float
        /// data. Only meaningful on the oblique relief-raymarch path; a flat / non-
        /// raymarch render leaves the capture zero-filled.</summary>
        public static uint[] RenderToPixels(PosterRequest req, CancellationToken token,
            out int w, out int h,
            FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers? aovCapture)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.Width <= 0 || req.Height <= 0)
                throw new ArgumentException("Poster dimensions must be positive.", nameof(req));
            return (uint[])RenderComposedBuffer(req, token, out w, out h, aovCapture).Clone();
        }

        /// <summary>Watermark + write the composed poster buffer to the request path.</summary>
        private static PosterResult WritePoster(PosterRequest req, uint[] buffer, int w, int h, long elapsedMs)
        {
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
                    wm, poster: true, dpi: req.Dpi, exrCompression: req.ExrCompression);
                return new PosterResult(savedW, savedH, elapsedMs);
            }
            else
            {
                var fontColor = ImageExport.ComputeContrastColor(
                    Color.White, watermark: true, pixels: buffer, imgW: w, imgH: h);
                var wm = BuildPosterWatermark(req, fontColor);
                ImageExport.SavePixelsToFile(
                    buffer, w, h, req.Path, req.Format,
                    wm, poster: true, dpi: req.Dpi, exrCompression: req.ExrCompression);
                return new PosterResult(w, h, elapsedMs);
            }
        }

        // #102 — apply heightfield relief to a freshly rendered colour buffer
        // (Mandelbrot or any escape-time alt family that exposes a height
        // field), matching FractalRenderHost.UploadProcessedBuffer so a poster /
        // wallpaper carries the same raised 3D relief as the screen. Returns the
        // input buffer unchanged when relief is off or the calc exposes no
        // height field. Field dims == output dims (poster is hi-res).
        private static uint[] ApplyReliefIfEnabled(
            uint[] buffer, IHeightFieldSource? heightSource, int w, int h, FractalParameters p,
            IColorMap? colorMap = null,
            FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers? aovCapture = null,
            FracturingFog.Rendering.Lighting.FroxelHistory? froxelHistory = null,
            float[]? hiResField = null, int hiResW = 0, int hiResH = 0,
            FracturingFog.Rendering.Lighting.ReliefMotionVector.CameraView? previousCamera = null,
            SvgfHistory? svgfHistory = null)
        {
            if (p == null || !p.Relief2DEnabled) return buffer;
            int n = w * h;
            if (n <= 0 || buffer.Length < n) return buffer;

            var dst = new uint[n];
            if (p.Relief2DRaymarch)
            {
                // #508 — prefer the interactive HI-RES relief field the on-screen
                // raymarch uses; the calculator's own SmoothBuffer (output-res) is a
                // COARSER field → a "flattened" poster. Fall back to SmoothBuffer when
                // no hi-res field was supplied (batch / no host). The field grid dims
                // (fw,fh) are decoupled from the output size — HeightDe samples by
                // normalised coords.
                float[]? field; int fw, fh;
                // S11 (#592) — the orbit-trap height source reads the calc's display-res
                // TrapBuffer, which the hi-res field does not carry, so a trap / blend
                // source uses the display-res smooth+trap field (hi-res trap is a
                // follow-up). Smooth still prefers the hi-res field for WYSIWYG.
                bool trapSource = p.Relief2DHeightSource != ReliefHeightSource.Smooth;
                if (!trapSource && hiResField != null && hiResW > 2 && hiResH > 2
                    && hiResField.Length >= hiResW * hiResH)
                {
                    field = hiResField; fw = hiResW; fh = hiResH;
                }
                else
                {
                    var smooth = heightSource?.SmoothBuffer; fw = w; fh = h;
                    if (smooth == null || smooth.Length < fw * fh) return buffer;
                    // Trap = the Mandelbrot calc's TrapBuffer (null for non-Mandelbrot or
                    // a non-orbit theme → Build returns smooth unchanged).
                    float[]? trap = trapSource ? (heightSource as MandelbrotCalculator)?.TrapBuffer : null;
                    field = FracturingFog.Rendering.Lighting.ReliefHeightField.Build(
                        smooth, trap, fw * fh, p.Relief2DHeightSource, p.Relief2DHeightBlend);
                }

                // #185 (slice D) — bake the active theme ramp so the export's
                // volumetric in-scatter is palette-mapped identically to the screen.
                // No-op unless VolumePaletteStrength > 0. Runtime-only LUT.
                // #508 — bake onto a LOCAL copy and pass it to Render as an explicit
                // lighting override; never write it back onto the shared (live)
                // FractalParameters — a background-thread mutation of the live params
                // raced the render loop (the on-screen "flip" during a poster save).
                var fx = p.Lighting;
                FracturingFog.Rendering.Lighting.VolumePaletteBaker.Bake(ref fx, colorMap);
                // S4 (#389) — capture the float normal/depth AOVs and denoise iff
                // the guided À-Trous pass is on. MakeCapture is null when off, so
                // this is byte-identical by default (Render keeps its GPU path).
                // S1/S7 (#389) — an EXTERNAL capture target (the AOV-EXR orchestrator
                // wanting the float normal/depth planes even with denoise off) wins;
                // it likewise forces the CPU trace so the planes are the render's own
                // float data. When neither denoise nor export asks, aov stays null and
                // the GPU fast path + byte-identical beauty are preserved.
                var aov = aovCapture ?? ReliefDenoisePass.MakeCapture(p, w, h);
                // S1 (#398) — vector motion blur needs the motion-vector AOV, which only
                // fills when a previous-frame camera is supplied. Ensure a Motion-capable
                // capture (only when no external capture target already owns the aov, so
                // an AOV-EXR export's clean geometry planes are never disturbed).
                bool wantMotionBlur = p.Relief2DMotionBlurStrength > 0.0 && previousCamera.HasValue;
                if (wantMotionBlur && aovCapture == null && (aov == null || aov.Motion == null))
                    aov = new FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers(
                        w, h, aov?.Components != null, true);
                FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.Render(
                    buffer, field, w, h, fw, fh, p, dst, out _, null, aov, null, froxelHistory, fx, previousCamera);
                // S4 (#402) — SVGF temporal denoise when a persistent history is supplied
                // and the temporal toggle is on; else the plain single-frame denoise. Both
                // are no-ops when the denoise is off, so this stays byte-identical by default.
                if (svgfHistory != null && ReliefDenoisePass.EnabledTemporal(p))
                    ReliefDenoisePass.ApplySvgf(dst, aov, w, h, p, svgfHistory);
                else
                    ReliefDenoisePass.Apply(dst, aov, w, h, p);
                if (wantMotionBlur && aov?.Motion != null)
                    dst = MotionBlurFromVectors.Apply(dst, aov.Motion, w, h,
                        p.Relief2DMotionBlurStrength, Math.Clamp(p.Relief2DMotionBlurSamples, 2, 64));
            }
            else
            {
                // Phase-1 hillshade (emboss) is screen-space: the field must match the
                // output grid, so it always uses the calc's SmoothBuffer (the hi-res
                // field is a raymarch-only concern).
                var field = heightSource?.SmoothBuffer;
                if (field == null || field.Length < n) return buffer;
                FracturingFog.Rendering.Lighting.HeightfieldRelief2D.Apply(
                    buffer, dst, field, w, h, p);
            }
            return dst;
        }

        /// <summary>#656 — resolve the relief height FIELD to raymarch for this output,
        /// guaranteeing the field and the (recomputed) albedo cover the SAME complex
        /// view at ANY aspect. The calculator maps the complex plane by pixel aspect
        /// (scale = 3.5/max(W,H)/Zoom), so a poster/wallpaper at a different aspect than
        /// the on-screen window covers a different complex rectangle. The caller's
        /// snapshot field (<see cref="PosterRequest.ReliefField"/>) is at the ON-SCREEN
        /// aspect; reusing it here stretches the field vs the albedo and desyncs colour
        /// from relief (the reported bug). Resolution:
        /// <list type="bullet">
        /// <item>Snapshot aspect ≈ output aspect → honour the snapshot (WYSIWYG poster at
        /// the on-screen aspect, e.g. the same-dims preview) — byte-identical to #508.</item>
        /// <item>Otherwise recompute an aspect-correct field at the OUTPUT aspect. At/above
        /// the field floor the albedo calc's own <c>SmoothBuffer</c> (output dims) already
        /// IS a hi-res, aspect-correct field, so return null to let
        /// <see cref="ApplyReliefIfEnabled"/> use the height source.</item>
        /// <item>Below the floor (small output), upsample a DEDICATED field calc at the
        /// output aspect so a small cross-aspect poster keeps the on-screen hi-res look.</item>
        /// </list>
        /// Returns the field to use (snapshot or freshly computed), or null to fall back
        /// to the height source's SmoothBuffer.</summary>
        public static float[]? ResolveReliefField(
            PosterRequest req, int w, int h, CancellationToken token, out int fw, out int fh)
        {
            fw = 0; fh = 0;
            var p = req.FractalParameters;
            if (p == null || !p.Relief2DEnabled || !p.Relief2DRaymarch) return null;
            if (w <= 2 || h <= 2) return null;

            double outAspect = (double)w / h;

            // Honour the caller snapshot only when its aspect matches the output.
            if (req.ReliefField is { } snap && req.ReliefFieldW > 2 && req.ReliefFieldH > 2
                && snap.Length >= (long)req.ReliefFieldW * req.ReliefFieldH)
            {
                double snapAspect = (double)req.ReliefFieldW / req.ReliefFieldH;
                if (Math.Abs(snapAspect - outAspect) <= 0.01 * outAspect)
                {
                    fw = req.ReliefFieldW; fh = req.ReliefFieldH;
                    return snap;
                }
            }

            // Aspect mismatch (or no snapshot). At/above the floor the output-dims
            // SmoothBuffer is already an aspect-correct hi-res field.
            int floor = Math.Clamp(p.Relief2DFieldFloor, 480, 2160);
            if (Math.Min(w, h) >= floor) return null;
            if (!FracturingFog.Rendering.FractalRenderHost.SupportsHiResReliefField(req.FractalType))
                return null;

            // Below the floor — upsample a dedicated field at the OUTPUT aspect (mirror
            // FractalRenderHost.TryCaptureHiResReliefField: short axis → floor, long axis
            // capped so a wide span doesn't blow the field render up).
            double s = floor / (double)Math.Min(w, h);
            int nfw = (int)Math.Round(w * s), nfh = (int)Math.Round(h * s);
            const int MaxLong = 3840;
            if (Math.Max(nfw, nfh) > MaxLong)
            {
                double s2 = MaxLong / (double)Math.Max(nfw, nfh);
                nfw = (int)Math.Round(nfw * s2); nfh = (int)Math.Round(nfh * s2);
            }
            nfw = Math.Max(4, nfw); nfh = Math.Max(4, nfh);

            var fieldSrc = BuildFieldCalculator(req, nfw, nfh, token);
            token.ThrowIfCancellationRequested();
            var sb = fieldSrc?.SmoothBuffer;
            if (sb == null || sb.Length < (long)nfw * nfh) return null;

            // Copy — own the field independent of the transient field calc.
            var outF = new float[nfw * nfh];
            Array.Copy(sb, outF, outF.Length);
            fw = nfw; fh = nfh;
            return outF;
        }

        /// <summary>#656 — build a dedicated relief-FIELD calculator at an explicit grid
        /// (decoupled from the output size) matching the request's view + params. Its
        /// <c>SmoothBuffer</c> is the aspect-correct height field. Mandelbrot uses its own
        /// calculator twin (it is not an <see cref="IFractalCalculator"/>, only an
        /// <see cref="IHeightFieldSource"/>); every other supported family routes through
        /// <see cref="BuildCaptureCalculator(PosterRequest,int,int)"/>. The calculator is
        /// run here and returned as its height-field source, or null when the type has no
        /// supersamplable field.</summary>
        private static IHeightFieldSource? BuildFieldCalculator(PosterRequest req, int fw, int fh, CancellationToken token)
        {
            if (req.FractalType == FractalType.Mandelbrot)
            {
                var m = new MandelbrotCalculator(fw, fh)
                {
                    CenterX = req.CenterX, CenterXLo = req.CenterXLo, CenterX2 = req.CenterX2, CenterX3 = req.CenterX3,
                    CenterX4 = req.CenterX4, CenterX5 = req.CenterX5, CenterX6 = req.CenterX6, CenterX7 = req.CenterX7,
                    CenterY = req.CenterY, CenterYLo = req.CenterYLo, CenterY2 = req.CenterY2, CenterY3 = req.CenterY3,
                    CenterY4 = req.CenterY4, CenterY5 = req.CenterY5, CenterY6 = req.CenterY6, CenterY7 = req.CenterY7,
                    Zoom = req.Zoom,
                    MaxIterations = req.MaxIterations,
                    ColorMap = req.ColorMap,
                    Quality = req.Quality,
                    InteriorAlpha = req.FractalParameters.InteriorAlpha,
                };
                m.Calculate(token);
                return m;
            }

            var alt = BuildCaptureCalculator(req, fw, fh);
            if (alt == null) return null;
            alt.Calculate(token);
            return alt as IHeightFieldSource;
        }

        // In-place brightness/contrast/gamma BGRA post-pass. Same math as
        // FractalRenderHost.UploadProcessedBuffer so poster output matches the
        // interactive image: contrast pivots around mid-grey (127.5), then
        // brightness offsets in 0..255 space, then the gamma LUT is applied last.
        private static void ApplyBrightnessContrastGamma(
            uint[] buf, int n, int brightness, int contrast, int gamma)
        {
            if (brightness == 0 && contrast == 0 && gamma == 0) return;
            float contrastFactor = 1f + contrast / 100f;
            float brightnessOffset255 = brightness / 100f * 255f;
            byte[]? gammaLut = gamma != 0 ? BuildGammaLut(gamma) : null;
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
                if (gammaLut != null)
                {
                    R = gammaLut[R];
                    G = gammaLut[G];
                    B = gammaLut[B];
                }
                // F10.3 — preserve the source alpha byte (was forced 0xFF, which
                // clobbered per-stop coverage on any brightness/contrast export).
                // Opaque pixels keep 0xFF, so pre-F10 output is byte-identical.
                buf[i] = (p & 0xFF000000u) | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }

        // Builds the 256-entry byte gamma LUT for the live image gamma slider
        // (F6 part 2), matching FractalRenderHost.BuildGammaLut exactly: slider
        // maps to gamma = 2^(slider/100) and the LUT stores round(pow(v/255, 1/gamma) * 255).
        private static byte[] BuildGammaLut(int gammaSlider)
        {
            double gammaValue = Math.Pow(2.0, gammaSlider / 100.0);
            double exp = 1.0 / gammaValue;
            var lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                double outN = Math.Pow(v / 255.0, exp);
                int o = (int)(outN * 255.0 + 0.5);
                lut[v] = (byte)Math.Clamp(o, 0, 255);
            }
            return lut;
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
            => BuildCaptureCalculator(req, req.Width, req.Height);

        /// <summary>As <see cref="BuildCaptureCalculator(PosterRequest)"/> but at an
        /// explicit grid size — used by the #656 aspect-correct relief-field recompute
        /// to build a dedicated FIELD calculator (its <c>SmoothBuffer</c>) at the
        /// output aspect, decoupled from the albedo/output resolution.</summary>
        public static IFractalCalculator? BuildCaptureCalculator(PosterRequest req, int w, int h)
        {
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
                FractalType.AcidWarp         => new AcidWarpCalculator(w, h),
                FractalType.Apollonian       => new ApollonianCalculator(w, h),
                FractalType.ChaoticBilliard  => new ChaoticBilliardCalculator(w, h),
                FractalType.PrecisionField   => new PrecisionFieldCalculator(w, h),
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
                case UserEquationCalculator u:
                    u.FractalParameters = req.FractalParameters;
                    u.InteriorAlpha = req.FractalParameters?.InteriorAlpha ?? 255;  // #382
                    break;
                case MandelbulbCalculator m:   m.FractalParameters = req.FractalParameters; break;
                case MandelboxCalculator mb:   mb.FractalParameters = req.FractalParameters; break;
                case KifsCalculator kf:        kf.FractalParameters = req.FractalParameters; break;
                case QuatJuliaCalculator qj:   qj.FractalParameters = req.FractalParameters; break;
                case QuatMandelbrotCalculator qm: qm.FractalParameters = req.FractalParameters; break;
                case PlasmaCalculator pl:      pl.FractalParameters = req.FractalParameters; break;
                case AcidWarpCalculator aw:    aw.FractalParameters = req.FractalParameters; break;
                case ApollonianCalculator ap:  ap.FractalParameters = req.FractalParameters; break;
                case KleinianCalculator kl:    kl.FractalParameters = req.FractalParameters; break;
                case BicomplexMandelbrotCalculator bc: bc.FractalParameters = req.FractalParameters; break;
                case DlaCalculator dl:         dl.FractalParameters = req.FractalParameters; break;
                case ChaoticBilliardCalculator cb: cb.FractalParameters = req.FractalParameters; break;
                case PrecisionFieldCalculator pf: pf.FractalParameters = req.FractalParameters; break;
                case FlameRenderer fr:         fr.FractalParameters = req.FractalParameters; break;
                case SandboxCalculator sb:
                    sb.FractalParameters = req.FractalParameters;
                    sb.InteriorAlpha = req.FractalParameters?.InteriorAlpha ?? 255;  // #382
                    break;
                case UserBulbCalculator ub:    ub.FractalParameters = req.FractalParameters; break;
            }
            return c;
        }
    }
}
