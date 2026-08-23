// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Batch/BatchOptions.cs
// Parsed command-line options for headless --batch processing.
// See BatchEntry.PrintUsage for the supported flag grammar.

using System;
using System.Globalization;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Batch
{
    public enum BatchMode { Image, Video, Slideshow, Scene }

    /// <summary>Per-light point / spot override parsed from the <c>--lightN-*</c>
    /// flags (roadmap S8, #404). Every field is nullable so BatchRenderer applies
    /// only what the user actually passed onto <c>fp.Lighting.LightN</c>; unset
    /// fields keep the light's existing value. <see cref="HasAny"/> reports whether
    /// this light carried any override at all.</summary>
    public sealed class BatchLightOverride
    {
        public LightType? Type { get; set; }
        public double? Intensity { get; set; }         // 0..4 (0 = off)
        public double? Theta { get; set; }             // directional aim / spot cone axis
        public double? Phi { get; set; }
        public double? PosX { get; set; }              // world position (point / spot)
        public double? PosY { get; set; }
        public double? PosZ { get; set; }
        public double? Range { get; set; }             // 0..100 (0 = pure inverse-square)
        public double? SpotInnerDeg { get; set; }      // spot cone half-angles (degrees)
        public double? SpotOuterDeg { get; set; }

        public bool HasAny =>
            Type.HasValue || Intensity.HasValue || Theta.HasValue || Phi.HasValue
            || PosX.HasValue || PosY.HasValue || PosZ.HasValue || Range.HasValue
            || SpotInnerDeg.HasValue || SpotOuterDeg.HasValue;
    }

    /// <summary>
    /// Mirrors VideoDialog.LosslessEncodeChoice so the CL batch path offers
    /// the same lossless encoder presets the interactive Video dialog does.
    /// None = WMF H.264 MP4 (built-in Mp4Writer, no ffmpeg required).
    /// </summary>
    public enum BatchLossless
    {
        None,
        LosslessH264Mp4,
        Ffv1Mkv,
        HighQualityH264Mp4,
    }

    public sealed class BatchOptions
    {
        public BatchMode Mode { get; set; } = BatchMode.Image;

        // Region/coords source — Region takes precedence when set.
        public string? RegionName { get; set; }
        public FractalType FractalType { get; set; } = FractalType.Mandelbrot;
        public double? CenterX { get; set; }
        public double? CenterY { get; set; }
        public double? Zoom { get; set; }
        public int? Iterations { get; set; }

        // Color theme by name. Default = HSV.
        public string ThemeName { get; set; } = BatchDefaults.ThemeName;

        // Output dimensions.
        public int Width { get; set; } = BatchDefaults.Width;
        public int Height { get; set; } = BatchDefaults.Height;

        // Output path. Treated as folder when video, file when image.
        public string OutputPath { get; set; } = "";
        public string? OutputName { get; set; }

        // Quality preset name. Default Standard.
        public string QualityName { get; set; } = BatchDefaults.QualityName;

        // Video-specific.
        public double VideoSeconds { get; set; } = 20.0;
        public int VideoFps { get; set; } = 30;
        // Start zoom for video. Defaults to 0.5 (classic full view).
        public double VideoStartZoom { get; set; } = 0.5;
        // When true, render in reverse: start at target, animate back to full view.
        public bool VideoReverse { get; set; }

        // Lossless encoding preset for video mode. None → built-in WMF MP4 writer.
        public BatchLossless Lossless { get; set; } = BatchLossless.None;

        // ── Slideshow mode ────────────────────────────────────────────────
        /// <summary>Name of a saved SlideshowConfig preset in the library to
        /// drive timing + filters. When null, the active config is used.</summary>
        public string? SlideshowConfigName { get; set; }
        /// <summary>Cap the headless slideshow's total wall-clock duration in
        /// seconds (encoded playback length). 0 = single full pass over the
        /// resolved region/theme set without looping.</summary>
        public double SlideshowSeconds { get; set; } = 60.0;
        /// <summary>ffmpeg preset for the final encode in slideshow mode.
        /// Defaults to HighQualityH264Mp4 (CRF 18, broad-compat).</summary>
        public BatchLossless SlideshowEncode { get; set; } = BatchLossless.HighQualityH264Mp4;
        /// <summary>Synonym of the "Slideshow: More Colors" context-menu item:
        /// when true the cycler runs 8 themes per region (Color Focus, shorter
        /// per-theme duration) instead of the default 3 (Region Focus). Affects
        /// batch slideshow cadence only; interactive slideshow honours the
        /// shell-level FocusRegion toggle.</summary>
        public bool MoreColors { get; set; }

        // ── Scene mode (Scene Engine Roadmap S7) ──────────────────────────
        /// <summary>Name of a saved SceneData in scenes.json to render offline.
        /// Required in scene mode.</summary>
        public string? SceneName { get; set; }
        /// <summary>Accumulation motion-blur sub-frames per output frame (1 = off).
        /// Higher = smoother camera/param-motion blur at N× render cost.</summary>
        public int MotionBlurSubframes { get; set; } = 1;
        /// <summary>Open-shutter fraction of the frame interval for motion blur
        /// (0.5 ≈ a 180° shutter). Clamped to (0,1].</summary>
        public double ShutterFraction { get; set; } = 0.5;
        /// <summary>When true (the default), paint the watermark + program-name
        /// sub-line into every emitted frame across image / video / slideshow
        /// batch modes. The CLI flag inverts this: <c>--watermark</c> (alias
        /// <c>--no-watermark</c>) turns the watermark OFF. Batch parity with the
        /// interactive Save flow, which always watermarks. (Scene mode governs
        /// its own watermark through SceneRenderSettings, not this flag.)</summary>
        public bool Watermark { get; set; } = true;

        // Keep PNG frame folder after successful video encode. Defaults to false
        // when --lossless is used (frames are intermediate), true otherwise.
        public bool KeepFrames { get; set; }
        public bool KeepFramesSpecified { get; set; }

        public bool Verbose { get; set; }

        // ── Post-FX (parity with interactive ViewState post-processing) ───────
        // Null = "not specified on the command line". Slideshow mode falls back
        // to the named SlideshowConfig.PostFx block when a flag is null; the
        // flag, when present, overrides the preset. Image/Video modes read the
        // flags only (no preset). Brightness/Contrast are BGRA post-passes;
        // Adaptive is histogram-equalization strength applied on the calculator
        // before the colour buffer is read (Mandelbrot only).
        public int? Brightness { get; set; }   // -100..100, 0 = none
        public int? Contrast { get; set; }     // -100..100, 0 = none
        public int? Adaptive { get; set; }     //    0..100, 0 = none (HistogramEq)

        /// <summary>Output-stage view transform / tonemap (roadmap S2, #389).
        /// Null = leave the FractalViewState default (None = identity, byte-
        /// identical). Applied on the poster buffer after brightness/contrast/
        /// gamma, exactly like the interactive path (image mode only today —
        /// the raw video frame path has no post-buffer stage yet).</summary>
        public ViewTransform? ViewTransform { get; set; }

        /// <summary>Export a multi-layer AOV OpenEXR (beauty + normal/depth/AO/
        /// diffuse/specular/shadow/stepcount passes) instead of a flat image
        /// (roadmap S1, #389). Image mode only; forces the .exr writer. AOVs are
        /// meaningful for 3D / relief-raymarch renders (the CPU shade path).</summary>
        public bool AovExr { get; set; }

        /// <summary>Exposure in stops applied before the view transform (roadmap
        /// S2, #389). Null = neutral (0). Only meaningful alongside a non-None
        /// <see cref="ViewTransform"/>, but honoured on its own too.</summary>
        public double? ViewExposureEv { get; set; }

        /// <summary>Global interior (in-set) alpha, 0..255 (#96). 255 = opaque
        /// (default, legacy pixel-identical); below 255 the interior turns
        /// translucent over the theme's interior background. Null = leave the
        /// FractalParameters default. Mandelbrot 2D only.</summary>
        public int? InteriorAlpha { get; set; }

        // Optional fractal-parameter overrides plumbed into FractalParameters.
        // Default null means "leave the FractalParameters default in place".
        public double? BulbPower { get; set; }
        public int? MultibrotExponent { get; set; }
        public string? LSystemPresetName { get; set; }
        public int? LSystemDepth { get; set; }
        public double? PlasmaRoughness { get; set; }
        public int? PlasmaSeed { get; set; }
        public string? FlamePresetName { get; set; }
        public int? FlameIterations { get; set; }
        public double? FlameGamma { get; set; }
        public double? FlameVibrancy { get; set; }

        // Acid Warp static pattern knobs (#363). Requires --fractal AcidWarp.
        public int? AcidPattern { get; set; }        // 0..19
        public double? AcidFrequency { get; set; }
        public double? AcidWarpStrength { get; set; }
        public int? AcidSeed { get; set; }

        // Domain-warp post-fx (#363). --domain-warp turns it on; the strength /
        // frequency knobs tune it (and imply it on when supplied).
        public bool DomainWarp { get; set; }
        public double? DomainWarpStrength { get; set; }
        public double? DomainWarpFrequency { get; set; }

        // 2D heightfield relief — Tier-1 core (#363). Any relief flag sets
        // Relief (enabled). Raymarch camera + isolate knobs are a follow-up.
        public bool Relief { get; set; }
        public bool ReliefRaymarch { get; set; }
        public double? ReliefHeight { get; set; }          // > 0
        public double? ReliefStrength { get; set; }        // 0..1
        public double? ReliefLightAzimuth { get; set; }    // 0..360
        public double? ReliefLightElevation { get; set; }  // -90..90
        public double? ReliefShadow { get; set; }          // 0..1
        public bool ReliefAbsolute { get; set; }           // emboss abs-height mode

        // Relief raymarch camera (#363 follow-up). Any camera flag implies relief.
        public double? ReliefCameraAzimuth { get; set; }   // 0..360
        public double? ReliefCameraElevation { get; set; } // -90..90
        public double? ReliefCameraFov { get; set; }       // 1..179
        public double? ReliefCameraZoom { get; set; }      // > 0
        public bool ReliefCameraOrtho { get; set; }

        // Depth of field on the relief raymarch camera (roadmap S3, #389). Any
        // DOF flag implies relief + raymarch (perspective camera only).
        public double? ReliefDofAperture { get; set; }     // 0..1, 0 = pinhole
        public double? ReliefDofFocus { get; set; }        // >= 0, 0 = auto-focus origin

        // Froxel volumetrics (roadmap S6, #408). Implies relief + raymarch.
        public bool ReliefFroxel { get; set; }

        // Per-light fog contribution bitmask (roadmap S6, #408). null = leave default
        // (all lights fog). 0..7.
        public int? FogLightMask { get; set; }

        // S4 (#389) — guided À-Trous denoise on the relief raymarch.
        public int? ReliefDenoiseIterations { get; set; }  // 0 = off
        public double? ReliefDenoiseColorSigma { get; set; }
        public double? ReliefDenoiseNormalSigma { get; set; }
        public double? ReliefDenoiseDepthSigma { get; set; }

        // Relief isolate masking (#363 follow-up). Any isolate flag implies
        // relief + isolate on. NoDetail turns OFF the default detail isolation.
        public bool ReliefIsolate { get; set; }
        public bool ReliefIsolateNoDetail { get; set; }
        public double? ReliefIsolateThreshold { get; set; }  // 0..1
        public bool ReliefIsolateByColor { get; set; }
        public string? ReliefIsolateColors { get; set; }     // CSV hex/rgb
        public double? ReliefIsolateTolerance { get; set; }  // 0..1

        // Per-light point / spot overrides (roadmap S8, #404). Index 0..2 = the
        // three LightingFxData lights. Only the fields the user passed are set
        // (nullable); BatchRenderer applies each non-null onto fp.Lighting.LightN.
        // Any per-light flag implies relief + raymarch (positional lights are a
        // relief-raymarch feature). See BatchFlags.LightFlag for the grammar.
        public BatchLightOverride[] Lights { get; } =
            { new BatchLightOverride(), new BatchLightOverride(), new BatchLightOverride() };

        // ── Phase 3 remote rendering ──────────────────────────────────────
        /// <summary>True when --remote was passed; flips dispatch into the
        /// FFClientConnection path. Both --connection and --render become
        /// required, and the local rendering pipeline is bypassed.</summary>
        public bool Remote { get; set; }

        /// <summary>Required with --remote. Names a saved entry in
        /// %APPDATA%\FracturingFog\client-connections.json.</summary>
        public string? RemoteConnection { get; set; }

        /// <summary>Required with --remote. Names a saved preset in
        /// %APPDATA%\FracturingFog\client-render-presets.json.</summary>
        public string? RemotePreset { get; set; }

        public static bool TryParse(string[] args, int startIndex, out BatchOptions opts, out string? error)
        {
            opts = new BatchOptions();
            error = null;

            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--mode":
                    case "-m":
                        if (!Next(args, ref i, a, out string mv, out error)) return false;
                        if (string.Equals(mv, "image", StringComparison.OrdinalIgnoreCase)) opts.Mode = BatchMode.Image;
                        else if (string.Equals(mv, "video", StringComparison.OrdinalIgnoreCase)) opts.Mode = BatchMode.Video;
                        else if (string.Equals(mv, "slideshow", StringComparison.OrdinalIgnoreCase)) opts.Mode = BatchMode.Slideshow;
                        else if (string.Equals(mv, "scene", StringComparison.OrdinalIgnoreCase)) opts.Mode = BatchMode.Scene;
                        else { error = $"Unknown --mode '{mv}'. Use image|video|slideshow|scene."; return false; }
                        break;

                    case "--slideshow":
                        if (!Next(args, ref i, a, out string sname, out error)) return false;
                        opts.Mode = BatchMode.Slideshow;
                        opts.SlideshowConfigName = sname;
                        break;

                    case "--scene":
                        if (!Next(args, ref i, a, out string scName, out error)) return false;
                        opts.Mode = BatchMode.Scene;
                        opts.SceneName = scName;
                        break;

                    case "--motion-blur":
                    case "--subframes":
                        if (!NextInt(args, ref i, a, out int mbv, out error)) return false;
                        opts.MotionBlurSubframes = mbv;
                        break;

                    case "--shutter":
                        if (!NextDouble(args, ref i, a, out double shv, out error)) return false;
                        opts.ShutterFraction = shv;
                        break;

                    case "--encode":
                        if (!Next(args, ref i, a, out string evl, out error)) return false;
                        switch (evl.ToLowerInvariant())
                        {
                            case "h264hq":
                            case "hq":
                            case "highqualityh264mp4":
                                opts.SlideshowEncode = BatchLossless.HighQualityH264Mp4; break;
                            case "h264":
                            case "lossless-h264":
                            case "losslessh264mp4":
                                opts.SlideshowEncode = BatchLossless.LosslessH264Mp4; break;
                            case "ffv1":
                            case "ffv1mkv":
                                opts.SlideshowEncode = BatchLossless.Ffv1Mkv; break;
                            default:
                                error = $"Unknown --encode '{evl}'. Use h264hq|h264|ffv1.";
                                return false;
                        }
                        break;

                    case BatchFlags.Region:
                    case "-r":
                        if (!Next(args, ref i, a, out string rv, out error)) return false;
                        opts.RegionName = rv;
                        break;

                    case BatchFlags.Fractal:
                    case "-f":
                        if (!Next(args, ref i, a, out string fv, out error)) return false;
                        if (!Enum.TryParse<FractalType>(fv, ignoreCase: true, out var ft))
                        {
                            error = $"Unknown --fractal '{fv}'. Valid: {string.Join(", ", Enum.GetNames<FractalType>())}";
                            return false;
                        }
                        opts.FractalType = ft;
                        break;

                    case BatchFlags.X:
                        if (!NextDouble(args, ref i, a, out double xv, out error)) return false;
                        opts.CenterX = xv;
                        break;

                    case BatchFlags.Y:
                        if (!NextDouble(args, ref i, a, out double yv, out error)) return false;
                        opts.CenterY = yv;
                        break;

                    case "--z":
                    case BatchFlags.Zoom:
                        if (!NextDouble(args, ref i, a, out double zv, out error)) return false;
                        opts.Zoom = zv;
                        break;

                    case "--i":
                    case BatchFlags.Iter:
                    case "--iterations":
                        if (!NextInt(args, ref i, a, out int iv, out error)) return false;
                        opts.Iterations = iv;
                        break;

                    case BatchFlags.Theme:
                    case "-t":
                        if (!Next(args, ref i, a, out string tv, out error)) return false;
                        opts.ThemeName = tv;
                        break;

                    case BatchFlags.Width:
                    case "-w":
                        if (!NextInt(args, ref i, a, out int wv, out error)) return false;
                        opts.Width = wv;
                        break;

                    case BatchFlags.Height:
                    case "-h":
                        if (!NextInt(args, ref i, a, out int hv, out error)) return false;
                        opts.Height = hv;
                        break;

                    case BatchFlags.Out:
                    case "-o":
                        if (!Next(args, ref i, a, out string ov, out error)) return false;
                        opts.OutputPath = ov;
                        break;

                    case "--name":
                    case "-n":
                        if (!Next(args, ref i, a, out string nv, out error)) return false;
                        opts.OutputName = nv;
                        break;

                    case BatchFlags.Quality:
                    case "-q":
                        if (!Next(args, ref i, a, out string qv, out error)) return false;
                        opts.QualityName = qv;
                        break;

                    case "--seconds":
                    case "--secs":
                        if (!NextDouble(args, ref i, a, out double sv, out error)) return false;
                        opts.VideoSeconds = sv;
                        break;

                    case "--fps":
                        if (!NextInt(args, ref i, a, out int fpsv, out error)) return false;
                        opts.VideoFps = fpsv;
                        break;

                    case "--start-zoom":
                        if (!NextDouble(args, ref i, a, out double sz, out error)) return false;
                        opts.VideoStartZoom = sz;
                        break;

                    case "--reverse":
                        opts.VideoReverse = true;
                        break;

                    case "--lossless":
                    case "-l":
                        if (!Next(args, ref i, a, out string lv, out error)) return false;
                        switch (lv.ToLowerInvariant())
                        {
                            case "none":   opts.Lossless = BatchLossless.None; break;
                            case "h264":
                            case "lossless-h264":
                            case "losslessh264mp4":
                                opts.Lossless = BatchLossless.LosslessH264Mp4; break;
                            case "ffv1":
                            case "ffv1mkv":
                                opts.Lossless = BatchLossless.Ffv1Mkv; break;
                            case "h264hq":
                            case "hq":
                            case "highqualityh264mp4":
                                opts.Lossless = BatchLossless.HighQualityH264Mp4; break;
                            default:
                                error = $"Unknown --lossless '{lv}'. Use none|h264|ffv1|h264hq.";
                                return false;
                        }
                        break;

                    case "--keep-frames":
                        opts.KeepFrames = true;
                        opts.KeepFramesSpecified = true;
                        break;

                    case "--more-colors":
                    case "--more-colours":
                        opts.MoreColors = true;
                        break;

                    // #54: watermark is ON by default for every mode; the flag
                    // inverts to mean "turn it off". --no-watermark is a clearer
                    // alias for the same action.
                    case "--watermark":
                    case "--no-watermark":
                        opts.Watermark = false;
                        break;

                    case "--no-keep-frames":
                        opts.KeepFrames = false;
                        opts.KeepFramesSpecified = true;
                        break;

                    case "--verbose":
                    case "-v":
                        opts.Verbose = true;
                        break;

                    case BatchFlags.Brightness:
                        if (!NextInt(args, ref i, a, out int brv, out error)) return false;
                        opts.Brightness = brv;
                        break;

                    case BatchFlags.Contrast:
                        if (!NextInt(args, ref i, a, out int ctv, out error)) return false;
                        opts.Contrast = ctv;
                        break;

                    case BatchFlags.Adaptive:
                    case "--histogram-eq":
                        if (!NextInt(args, ref i, a, out int adv, out error)) return false;
                        opts.Adaptive = adv;
                        break;

                    case BatchFlags.InteriorAlpha:
                        if (!NextInt(args, ref i, a, out int iav, out error)) return false;
                        opts.InteriorAlpha = iav;
                        break;

                    case BatchFlags.ViewTransform:
                    case "--tonemap":
                        if (!Next(args, ref i, a, out string vtv, out error)) return false;
                        if (!TryParseViewTransform(vtv, out var vt))
                        {
                            error = $"Unknown --view-transform '{vtv}'. Use none|reinhard|aces|agx|filmic.";
                            return false;
                        }
                        opts.ViewTransform = vt;
                        break;

                    case BatchFlags.Exposure:
                        if (!NextDouble(args, ref i, a, out double exv, out error)) return false;
                        opts.ViewExposureEv = exv;
                        break;

                    case BatchFlags.AovExr:
                        opts.AovExr = true;
                        break;

                    case BatchFlags.BulbPower:
                        if (!NextDouble(args, ref i, a, out double bpv, out error)) return false;
                        opts.BulbPower = bpv;
                        break;

                    case BatchFlags.MultibrotExp:
                    case "--multibrot-power":
                        if (!NextInt(args, ref i, a, out int mev, out error)) return false;
                        opts.MultibrotExponent = mev;
                        break;

                    case BatchFlags.LSystemPreset:
                    case "--lsystem":
                        if (!Next(args, ref i, a, out string lspv, out error)) return false;
                        opts.LSystemPresetName = lspv;
                        break;

                    case BatchFlags.LSystemDepth:
                        if (!NextInt(args, ref i, a, out int lsdv, out error)) return false;
                        opts.LSystemDepth = lsdv;
                        break;

                    case BatchFlags.PlasmaRoughness:
                        if (!NextDouble(args, ref i, a, out double prv, out error)) return false;
                        opts.PlasmaRoughness = prv;
                        break;

                    case BatchFlags.PlasmaSeed:
                        if (!NextInt(args, ref i, a, out int psv, out error)) return false;
                        opts.PlasmaSeed = psv;
                        break;

                    case BatchFlags.FlamePreset:
                    case "--flame":
                        if (!Next(args, ref i, a, out string fpv, out error)) return false;
                        opts.FlamePresetName = fpv;
                        break;

                    case BatchFlags.FlameIter:
                        if (!NextInt(args, ref i, a, out int fiv, out error)) return false;
                        opts.FlameIterations = fiv;
                        break;

                    case BatchFlags.FlameGamma:
                        if (!NextDouble(args, ref i, a, out double fgv, out error)) return false;
                        opts.FlameGamma = fgv;
                        break;

                    case BatchFlags.FlameVibrancy:
                        if (!NextDouble(args, ref i, a, out double fvv, out error)) return false;
                        opts.FlameVibrancy = fvv;
                        break;

                    case BatchFlags.AcidPattern:
                        if (!NextInt(args, ref i, a, out int apv, out error)) return false;
                        opts.AcidPattern = apv;
                        break;

                    case BatchFlags.AcidFrequency:
                        if (!NextDouble(args, ref i, a, out double afv, out error)) return false;
                        opts.AcidFrequency = afv;
                        break;

                    case BatchFlags.AcidWarpStrength:
                        if (!NextDouble(args, ref i, a, out double awv, out error)) return false;
                        opts.AcidWarpStrength = awv;
                        break;

                    case BatchFlags.AcidSeed:
                        if (!NextInt(args, ref i, a, out int asv, out error)) return false;
                        opts.AcidSeed = asv;
                        break;

                    case BatchFlags.DomainWarp:
                        opts.DomainWarp = true;
                        break;

                    case BatchFlags.DomainWarpStrength:
                        if (!NextDouble(args, ref i, a, out double dwsv, out error)) return false;
                        opts.DomainWarpStrength = dwsv;
                        opts.DomainWarp = true;
                        break;

                    case BatchFlags.DomainWarpFrequency:
                        if (!NextDouble(args, ref i, a, out double dwfv, out error)) return false;
                        opts.DomainWarpFrequency = dwfv;
                        opts.DomainWarp = true;
                        break;

                    case BatchFlags.Relief:
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefRaymarch:
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefHeight:
                        if (!NextDouble(args, ref i, a, out double rhv, out error)) return false;
                        opts.ReliefHeight = rhv;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefStrength:
                        if (!NextDouble(args, ref i, a, out double rsv, out error)) return false;
                        opts.ReliefStrength = rsv;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefLightAzimuth:
                        if (!NextDouble(args, ref i, a, out double rlav, out error)) return false;
                        opts.ReliefLightAzimuth = rlav;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefLightElevation:
                        if (!NextDouble(args, ref i, a, out double rlev, out error)) return false;
                        opts.ReliefLightElevation = rlev;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefShadow:
                        if (!NextDouble(args, ref i, a, out double rshv, out error)) return false;
                        opts.ReliefShadow = rshv;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefAbsolute:
                        opts.ReliefAbsolute = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefCameraAzimuth:
                        if (!NextDouble(args, ref i, a, out double rcav, out error)) return false;
                        opts.ReliefCameraAzimuth = rcav;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefCameraElevation:
                        if (!NextDouble(args, ref i, a, out double rcev, out error)) return false;
                        opts.ReliefCameraElevation = rcev;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefCameraFov:
                        if (!NextDouble(args, ref i, a, out double rcfv, out error)) return false;
                        opts.ReliefCameraFov = rcfv;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefCameraZoom:
                        if (!NextDouble(args, ref i, a, out double rczv, out error)) return false;
                        opts.ReliefCameraZoom = rczv;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefCameraOrtho:
                        opts.ReliefCameraOrtho = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.DofAperture:
                        if (!NextDouble(args, ref i, a, out double dofa, out error)) return false;
                        opts.ReliefDofAperture = dofa;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.DofFocus:
                        if (!NextDouble(args, ref i, a, out double doff, out error)) return false;
                        opts.ReliefDofFocus = doff;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefFroxel:
                        opts.ReliefFroxel = true;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.FogLightMask:
                        if (!NextInt(args, ref i, a, out int flm, out error)) return false;
                        opts.FogLightMask = flm;
                        break;

                    case BatchFlags.Denoise:
                        if (!NextInt(args, ref i, a, out int dni, out error)) return false;
                        opts.ReliefDenoiseIterations = dni;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.DenoiseColorSigma:
                        if (!NextDouble(args, ref i, a, out double dncs, out error)) return false;
                        opts.ReliefDenoiseColorSigma = dncs;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.DenoiseNormalSigma:
                        if (!NextDouble(args, ref i, a, out double dnns, out error)) return false;
                        opts.ReliefDenoiseNormalSigma = dnns;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.DenoiseDepthSigma:
                        if (!NextDouble(args, ref i, a, out double dnds, out error)) return false;
                        opts.ReliefDenoiseDepthSigma = dnds;
                        opts.ReliefRaymarch = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolate:
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolateNoDetail:
                        opts.ReliefIsolateNoDetail = true;
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolateThreshold:
                        if (!NextDouble(args, ref i, a, out double ritv, out error)) return false;
                        opts.ReliefIsolateThreshold = ritv;
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolateByColor:
                        opts.ReliefIsolateByColor = true;
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolateColors:
                        if (!Next(args, ref i, a, out string ricv, out error)) return false;
                        opts.ReliefIsolateColors = ricv;
                        opts.ReliefIsolateByColor = true;
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case BatchFlags.ReliefIsolateTolerance:
                        if (!NextDouble(args, ref i, a, out double ricotv, out error)) return false;
                        opts.ReliefIsolateTolerance = ricotv;
                        opts.ReliefIsolate = true;
                        opts.Relief = true;
                        break;

                    case "--remote":
                        opts.Remote = true;
                        break;

                    case "--connection":
                        if (!Next(args, ref i, a, out string conn, out error)) return false;
                        opts.RemoteConnection = conn;
                        break;

                    case "--render":
                        if (!Next(args, ref i, a, out string preset, out error)) return false;
                        opts.RemotePreset = preset;
                        break;

                    case "--help":
                    case "-?":
                        error = "__help__";
                        return false;

                    default:
                        if (!TryConsumeLightFlag(args, ref i, a, opts, out bool matched, out error))
                            return false;   // matched --lightN-*, but its value was invalid
                        if (!matched) { error = $"Unknown argument: {a}"; return false; }
                        break;
                }
            }

            // Validation
            if (opts.Remote)
            {
                if (string.IsNullOrWhiteSpace(opts.RemoteConnection))
                    { error = "--remote requires --connection NAME"; return false; }
                if (string.IsNullOrWhiteSpace(opts.RemotePreset))
                    { error = "--remote requires --render NAME"; return false; }
                if (string.IsNullOrWhiteSpace(opts.OutputPath))
                    { error = "--remote requires --out PATH for the returned bytes"; return false; }
                // All other render-shape validation is owned by the saved preset
                // + the server's RequestLimits; nothing more to check here.
                return true;
            }

            // Slideshow + scene modes pull their region/theme set from the named
            // config / scene shots — no region/coord requirement.
            if (opts.Mode != BatchMode.Slideshow && opts.Mode != BatchMode.Scene
                && string.IsNullOrWhiteSpace(opts.RegionName))
            {
                if (opts.CenterX == null || opts.CenterY == null || opts.Zoom == null)
                {
                    error = "Must specify --region NAME, or all of --x --y --zoom (and optionally --iter).";
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(opts.OutputPath))
            {
                error = "Missing --out PATH.";
                return false;
            }

            if (opts.Width < 16 || opts.Height < 16)
            {
                error = "Width/height must be at least 16.";
                return false;
            }

            // Post-FX range checks (parity with the interactive sliders).
            if (opts.Brightness is < -100 or > 100)
                { error = "--brightness must be -100..100."; return false; }
            if (opts.Contrast is < -100 or > 100)
                { error = "--contrast must be -100..100."; return false; }
            if (opts.Adaptive is < 0 or > 100)
                { error = "--adaptive must be 0..100."; return false; }
            if (opts.InteriorAlpha is < 0 or > 255)
                { error = "--interior-alpha must be 0..255."; return false; }
            if (opts.ViewExposureEv is < -16.0 or > 16.0)
                { error = "--exposure must be -16..16 (stops)."; return false; }
            if (opts.AovExr && opts.Mode != BatchMode.Image)
                { error = "--aov-exr is image mode only."; return false; }
            if (opts.AcidPattern is < 0 or >= FractalParameters.AcidWarpPatternCount)
                { error = $"--acid-pattern must be 0..{FractalParameters.AcidWarpPatternCount - 1}."; return false; }
            if (opts.ReliefHeight is <= 0)
                { error = "--relief-height must be > 0."; return false; }
            if (opts.ReliefStrength is < 0 or > 1)
                { error = "--relief-strength must be 0..1."; return false; }
            if (opts.ReliefShadow is < 0 or > 1)
                { error = "--relief-shadow must be 0..1."; return false; }
            if (opts.ReliefLightAzimuth is < 0 or > 360)
                { error = "--relief-light-azimuth must be 0..360."; return false; }
            if (opts.ReliefLightElevation is < -90 or > 90)
                { error = "--relief-light-elevation must be -90..90."; return false; }
            if (opts.ReliefCameraAzimuth is < 0 or > 360)
                { error = "--relief-camera-azimuth must be 0..360."; return false; }
            if (opts.ReliefCameraElevation is < -90 or > 90)
                { error = "--relief-camera-elevation must be -90..90."; return false; }
            if (opts.ReliefCameraFov is < 1 or > 179)
                { error = "--relief-camera-fov must be 1..179."; return false; }
            if (opts.ReliefCameraZoom is <= 0)
                { error = "--relief-camera-zoom must be > 0."; return false; }
            if (opts.ReliefDofAperture is < 0 or > 1)
                { error = "--dof-aperture must be 0..1."; return false; }
            if (opts.ReliefDofFocus is < 0)
                { error = "--dof-focus must be >= 0."; return false; }
            if (opts.ReliefDenoiseIterations is < 0 or > 8)
                { error = "--denoise must be 0..8 (passes)."; return false; }
            if (opts.ReliefDenoiseColorSigma is <= 0)
                { error = "--denoise-color-sigma must be > 0."; return false; }
            if (opts.ReliefDenoiseNormalSigma is <= 0)
                { error = "--denoise-normal-sigma must be > 0."; return false; }
            if (opts.ReliefDenoiseDepthSigma is <= 0)
                { error = "--denoise-depth-sigma must be > 0."; return false; }
            if (opts.ReliefIsolateThreshold is < 0 or > 1)
                { error = "--relief-isolate-threshold must be 0..1."; return false; }
            if (opts.ReliefIsolateTolerance is < 0 or > 1)
                { error = "--relief-isolate-tolerance must be 0..1."; return false; }

            if (opts.FogLightMask is < 0 or > 7)
                { error = "--fog-light-mask must be 0..7 (bit n = light n+1 lights the fog)."; return false; }

            // Per-light point / spot overrides (roadmap S8, #404).
            for (int li = 0; li < opts.Lights.Length; li++)
            {
                var L = opts.Lights[li];
                int n = li + 1;
                if (L.Intensity is < 0 or > 4)
                    { error = $"--light{n}-intensity must be 0..4."; return false; }
                if (L.Range is < 0 or > 100)
                    { error = $"--light{n}-range must be 0..100."; return false; }
                if (L.SpotInnerDeg is < 0 or > 90)
                    { error = $"--light{n}-cone inner must be 0..90 (degrees)."; return false; }
                if (L.SpotOuterDeg is < 0 or > 90)
                    { error = $"--light{n}-cone outer must be 0..90 (degrees)."; return false; }
            }

            if (opts.Mode == BatchMode.Slideshow)
            {
                // VideoSeconds reuses --seconds parser; mirror into slideshow.
                opts.SlideshowSeconds = opts.VideoSeconds;
                if (opts.SlideshowSeconds < 0.0 || opts.SlideshowSeconds > 7_200.0)
                    { error = "--seconds for slideshow must be 0..7200 (2 hours)."; return false; }
                if (opts.VideoFps < 1 || opts.VideoFps > 240)
                    { error = "--fps must be 1..240."; return false; }
            }

            if (opts.Mode == BatchMode.Video)
            {
                if (opts.VideoSeconds < 0.5 || opts.VideoSeconds > 600.0)
                    { error = "--seconds must be 0.5..600."; return false; }
                if (opts.VideoFps < 1 || opts.VideoFps > 240)
                    { error = "--fps must be 1..240."; return false; }
                if (!opts.KeepFramesSpecified)
                    opts.KeepFrames = opts.Lossless == BatchLossless.None;
            }

            if (opts.Mode == BatchMode.Scene)
            {
                if (string.IsNullOrWhiteSpace(opts.SceneName))
                    { error = "Scene mode requires --scene NAME."; return false; }
                if (opts.VideoFps < 1 || opts.VideoFps > 240)
                    { error = "--fps must be 1..240."; return false; }
                if (opts.MotionBlurSubframes < 1 || opts.MotionBlurSubframes > 64)
                    { error = "--motion-blur must be 1..64."; return false; }
                if (opts.ShutterFraction <= 0.0 || opts.ShutterFraction > 1.0)
                    { error = "--shutter must be in (0, 1]."; return false; }
            }

            return true;
        }

        /// <summary>Map a friendly --view-transform name to the enum. Accepts the
        /// short aliases the interactive selector shows (aces, agx) as well as the
        /// exact enum names.</summary>
        internal static bool TryParseViewTransform(string s, out FracturingFog.Imaging.ViewTransform vt)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "none":     vt = FracturingFog.Imaging.ViewTransform.None; return true;
                case "reinhard": vt = FracturingFog.Imaging.ViewTransform.Reinhard; return true;
                case "aces":
                case "acesfilmic": vt = FracturingFog.Imaging.ViewTransform.AcesFilmic; return true;
                case "agx":      vt = FracturingFog.Imaging.ViewTransform.AgX; return true;
                case "filmic":
                case "hable":    vt = FracturingFog.Imaging.ViewTransform.Filmic; return true;
                default:
                    // Fall back to the exact enum spelling for forward-compat.
                    return Enum.TryParse(s, ignoreCase: true, out vt)
                        && Enum.IsDefined(vt);
            }
        }

        /// <summary>Parse a <c>--lightN-field</c> flag (roadmap S8, #404) onto
        /// <paramref name="opts"/>.Lights[N-1]. <paramref name="matched"/> is true
        /// when <paramref name="a"/> was a per-light flag (whether or not the value
        /// parsed). Returns false only when a per-light flag matched but its value
        /// was invalid (<paramref name="err"/> set). Any per-light flag implies
        /// relief + raymarch (positional lights are a relief-raymarch feature).</summary>
        private static bool TryConsumeLightFlag(string[] args, ref int i, string a,
            BatchOptions opts, out bool matched, out string? err)
        {
            matched = false; err = null;
            string s = a.ToLowerInvariant();
            // Grammar: --light<N>-<field>, N ∈ 1..3.
            if (!s.StartsWith("--light", StringComparison.Ordinal) || s.Length < 9) return true;
            char digit = s[7];
            if (digit < '1' || digit > '3' || s[8] != '-') return true;
            int idx = digit - '1';
            string field = s.Substring(9);
            var light = opts.Lights[idx];

            switch (field)
            {
                case BatchFlags.LightFieldType:
                    if (!Next(args, ref i, a, out string tv, out err)) { matched = true; return false; }
                    matched = true;
                    switch (tv.Trim().ToLowerInvariant())
                    {
                        case "directional": case "dir": light.Type = LightType.Directional; break;
                        case "point":       light.Type = LightType.Point; break;
                        case "spot":        light.Type = LightType.Spot; break;
                        default: err = $"{a} expected directional|point|spot, got '{tv}'."; return false;
                    }
                    break;

                case BatchFlags.LightFieldIntensity:
                    if (!NextDouble(args, ref i, a, out double iv, out err)) { matched = true; return false; }
                    matched = true; light.Intensity = iv;
                    break;

                case BatchFlags.LightFieldDir:
                    if (!Next(args, ref i, a, out string dv, out err)) { matched = true; return false; }
                    matched = true;
                    if (!TryParseCsvDoubles(dv, 2, out double[] dvv)) { err = $"{a} expected \"theta,phi\", got '{dv}'."; return false; }
                    light.Theta = dvv[0]; light.Phi = dvv[1];
                    break;

                case BatchFlags.LightFieldPos:
                    if (!Next(args, ref i, a, out string pv, out err)) { matched = true; return false; }
                    matched = true;
                    if (!TryParseCsvDoubles(pv, 3, out double[] pvv)) { err = $"{a} expected \"x,y,z\", got '{pv}'."; return false; }
                    light.PosX = pvv[0]; light.PosY = pvv[1]; light.PosZ = pvv[2];
                    break;

                case BatchFlags.LightFieldRange:
                    if (!NextDouble(args, ref i, a, out double rv, out err)) { matched = true; return false; }
                    matched = true; light.Range = rv;
                    break;

                case BatchFlags.LightFieldCone:
                    if (!Next(args, ref i, a, out string cv, out err)) { matched = true; return false; }
                    matched = true;
                    if (!TryParseCsvDoubles(cv, 2, out double[] cvv)) { err = $"{a} expected \"inner,outer\", got '{cv}'."; return false; }
                    light.SpotInnerDeg = cvv[0]; light.SpotOuterDeg = cvv[1];
                    break;

                default:
                    return true;   // --lightN- prefix but unknown field → not ours
            }

            // Positional lights only render on the relief raymarch path.
            opts.Relief = true;
            opts.ReliefRaymarch = true;
            return true;
        }

        /// <summary>Parse exactly <paramref name="count"/> comma-separated invariant
        /// doubles. Returns false on the wrong count or an unparseable field.</summary>
        private static bool TryParseCsvDoubles(string s, int count, out double[] values)
        {
            values = Array.Empty<double>();
            var parts = (s ?? string.Empty).Split(',');
            if (parts.Length != count) return false;
            var outv = new double[count];
            for (int k = 0; k < count; k++)
                if (!double.TryParse(parts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out outv[k]))
                    return false;
            values = outv;
            return true;
        }

        private static bool Next(string[] a, ref int i, string flag, out string v, out string? err)
        {
            if (i + 1 >= a.Length) { v = ""; err = $"{flag} requires a value."; return false; }
            v = a[++i]; err = null; return true;
        }

        private static bool NextInt(string[] a, ref int i, string flag, out int v, out string? err)
        {
            if (!Next(a, ref i, flag, out string s, out err)) { v = 0; return false; }
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                { err = $"{flag} expected integer, got '{s}'."; return false; }
            return true;
        }

        private static bool NextDouble(string[] a, ref int i, string flag, out double v, out string? err)
        {
            if (!Next(a, ref i, flag, out string s, out err)) { v = 0; return false; }
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                { err = $"{flag} expected number, got '{s}'."; return false; }
            return true;
        }
    }
}
