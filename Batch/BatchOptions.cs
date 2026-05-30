// Batch/BatchOptions.cs
// Parsed command-line options for headless --batch processing.
// See BatchEntry.PrintUsage for the supported flag grammar.

using System;
using System.Globalization;
using FracturingFog.Models;

namespace FracturingFog.Batch
{
    public enum BatchMode { Image, Video }

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

        // Keep PNG frame folder after successful video encode. Defaults to false
        // when --lossless is used (frames are intermediate), true otherwise.
        public bool KeepFrames { get; set; }
        public bool KeepFramesSpecified { get; set; }

        public bool Verbose { get; set; }

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
                        else { error = $"Unknown --mode '{mv}'. Use image|video."; return false; }
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

                    case "--no-keep-frames":
                        opts.KeepFrames = false;
                        opts.KeepFramesSpecified = true;
                        break;

                    case "--verbose":
                    case "-v":
                        opts.Verbose = true;
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
            if (string.IsNullOrWhiteSpace(opts.RegionName))
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

            if (opts.Mode == BatchMode.Video)
            {
                if (opts.VideoSeconds < 0.5 || opts.VideoSeconds > 600.0)
                    { error = "--seconds must be 0.5..600."; return false; }
                if (opts.VideoFps < 1 || opts.VideoFps > 240)
                    { error = "--fps must be 1..240."; return false; }
                if (!opts.KeepFramesSpecified)
                    opts.KeepFrames = opts.Lossless == BatchLossless.None;
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
