// Batch/BatchOptions.cs
// Parsed command-line options for headless --batch processing.
// See BatchEntry.PrintUsage for the supported flag grammar.

using System;
using System.Globalization;
using FracturingFog.Models;

namespace FracturingFog.Batch
{
    public enum BatchMode { Image, Video, Slideshow, Scene }

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
        public string ThemeName { get; set; } = "HSV";

        // Output dimensions.
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;

        // Output path. Treated as folder when video, file when image.
        public string OutputPath { get; set; } = "";
        public string? OutputName { get; set; }

        // Quality preset name. Default Standard.
        public string QualityName { get; set; } = "Standard";

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
        /// <summary>When true, paint the watermark + program-name sub-line into
        /// every emitted frame across image / video / slideshow batch modes.
        /// Image mode already watermarks unconditionally for parity with the
        /// interactive Save flow; the flag also gates Video + Slideshow.</summary>
        public bool Watermark { get; set; }

        // Keep PNG frame folder after successful video encode. Defaults to false
        // when --lossless is used (frames are intermediate), true otherwise.
        public bool KeepFrames { get; set; }
        public bool KeepFramesSpecified { get; set; }

        public bool Verbose { get; set; }

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

                    case "--region":
                    case "-r":
                        if (!Next(args, ref i, a, out string rv, out error)) return false;
                        opts.RegionName = rv;
                        break;

                    case "--fractal":
                    case "-f":
                        if (!Next(args, ref i, a, out string fv, out error)) return false;
                        if (!Enum.TryParse<FractalType>(fv, ignoreCase: true, out var ft))
                        {
                            error = $"Unknown --fractal '{fv}'. Valid: {string.Join(", ", Enum.GetNames<FractalType>())}";
                            return false;
                        }
                        opts.FractalType = ft;
                        break;

                    case "--x":
                        if (!NextDouble(args, ref i, a, out double xv, out error)) return false;
                        opts.CenterX = xv;
                        break;

                    case "--y":
                        if (!NextDouble(args, ref i, a, out double yv, out error)) return false;
                        opts.CenterY = yv;
                        break;

                    case "--z":
                    case "--zoom":
                        if (!NextDouble(args, ref i, a, out double zv, out error)) return false;
                        opts.Zoom = zv;
                        break;

                    case "--i":
                    case "--iter":
                    case "--iterations":
                        if (!NextInt(args, ref i, a, out int iv, out error)) return false;
                        opts.Iterations = iv;
                        break;

                    case "--theme":
                    case "-t":
                        if (!Next(args, ref i, a, out string tv, out error)) return false;
                        opts.ThemeName = tv;
                        break;

                    case "--width":
                    case "-w":
                        if (!NextInt(args, ref i, a, out int wv, out error)) return false;
                        opts.Width = wv;
                        break;

                    case "--height":
                    case "-h":
                        if (!NextInt(args, ref i, a, out int hv, out error)) return false;
                        opts.Height = hv;
                        break;

                    case "--out":
                    case "-o":
                        if (!Next(args, ref i, a, out string ov, out error)) return false;
                        opts.OutputPath = ov;
                        break;

                    case "--name":
                    case "-n":
                        if (!Next(args, ref i, a, out string nv, out error)) return false;
                        opts.OutputName = nv;
                        break;

                    case "--quality":
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

                    case "--watermark":
                        opts.Watermark = true;
                        break;

                    case "--no-keep-frames":
                        opts.KeepFrames = false;
                        opts.KeepFramesSpecified = true;
                        break;

                    case "--verbose":
                    case "-v":
                        opts.Verbose = true;
                        break;

                    case "--bulb-power":
                        if (!NextDouble(args, ref i, a, out double bpv, out error)) return false;
                        opts.BulbPower = bpv;
                        break;

                    case "--multibrot-exp":
                    case "--multibrot-power":
                        if (!NextInt(args, ref i, a, out int mev, out error)) return false;
                        opts.MultibrotExponent = mev;
                        break;

                    case "--lsystem-preset":
                    case "--lsystem":
                        if (!Next(args, ref i, a, out string lspv, out error)) return false;
                        opts.LSystemPresetName = lspv;
                        break;

                    case "--lsystem-depth":
                        if (!NextInt(args, ref i, a, out int lsdv, out error)) return false;
                        opts.LSystemDepth = lsdv;
                        break;

                    case "--plasma-roughness":
                        if (!NextDouble(args, ref i, a, out double prv, out error)) return false;
                        opts.PlasmaRoughness = prv;
                        break;

                    case "--plasma-seed":
                        if (!NextInt(args, ref i, a, out int psv, out error)) return false;
                        opts.PlasmaSeed = psv;
                        break;

                    case "--flame-preset":
                    case "--flame":
                        if (!Next(args, ref i, a, out string fpv, out error)) return false;
                        opts.FlamePresetName = fpv;
                        break;

                    case "--flame-iter":
                        if (!NextInt(args, ref i, a, out int fiv, out error)) return false;
                        opts.FlameIterations = fiv;
                        break;

                    case "--flame-gamma":
                        if (!NextDouble(args, ref i, a, out double fgv, out error)) return false;
                        opts.FlameGamma = fgv;
                        break;

                    case "--flame-vibrancy":
                        if (!NextDouble(args, ref i, a, out double fvv, out error)) return false;
                        opts.FlameVibrancy = fvv;
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
                        error = $"Unknown argument: {a}";
                        return false;
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
