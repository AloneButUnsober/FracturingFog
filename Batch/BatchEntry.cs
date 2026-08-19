// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Batch/BatchEntry.cs
// Entry point for headless --batch processing.
//
// Renders either a single still image or a zoom video to disk without
// showing any UI. Attaches to the parent process's console so the progress
// meter is visible from cmd / PowerShell.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Batch
{
    public static class BatchEntry
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();
        private const int ATTACH_PARENT_PROCESS = -1;

        public static int Run(string[] args)
        {
            // Phase X.3 / Slice 3.2: gate Win32 console-attach so the call is
            // unreachable on non-Win hosts once this file follows the entry
            // point into FracturingFog.App (net10.0). On Linux/macOS
            // stdout/stderr are already wired to the launching terminal.
            if (OperatingSystem.IsWindows())
                AttachOrAllocConsole();

            if (args.Length == 1 || (args.Length > 1 && (args[1] == "--help" || args[1] == "-?")))
            {
                PrintUsage();
                return 0;
            }

            if (!BatchOptions.TryParse(args, startIndex: 1, out var opts, out string? err))
            {
                if (err == "__help__") { PrintUsage(); return 0; }
                Console.Error.WriteLine($"batch: {err}");
                Console.Error.WriteLine("Try --batch --help");
                return 2;
            }

            // Load user-defined themes + regions so --region and --theme can
            // resolve names the user authored interactively in earlier runs.
            try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
            try { FractalRegionLibrary.Instance.Load(); } catch { }
            // Scene mode also needs the scene + animation libraries so --scene
            // names and shot-attached animations resolve.
            if (opts.Mode == BatchMode.Scene)
            {
                try { FracturingFog.Models.AnimationLibrary.Instance.Load(); } catch { }
                try { FracturingFog.Models.SceneLibrary.Instance.Load(); } catch { }
            }

            try
            {
                if (opts.Remote)
                    return RemoteBatchRunner.Run(opts);
                return opts.Mode switch
                {
                    BatchMode.Image => BatchRenderer.RenderImage(opts),
                    BatchMode.Video => BatchRenderer.RenderVideo(opts),
                    BatchMode.Slideshow => BatchRenderer.RenderSlideshow(opts),
                    BatchMode.Scene => BatchRenderer.RenderScene(opts),
                    _ => 2,
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"batch failed: {ex.GetType().Name}: {ex.Message}");
                if (opts.Verbose) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void AttachOrAllocConsole()
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();

            // Rebind stdout/stderr to the now-attached console — WinExe stubs
            // them out at startup.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Fracturing Fog — batch processing");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  FracturingFog --batch [options]");
            Console.WriteLine();
            Console.WriteLine("Region source (one required):");
            Console.WriteLine("  --region NAME, -r NAME      Load saved built-in or user region by name");
            Console.WriteLine("  --x VAL --y VAL --zoom VAL  Manual coordinates (plus optional --iter N)");
            Console.WriteLine();
            Console.WriteLine("Common options:");
            Console.WriteLine("  --fractal TYPE, -f TYPE     Mandelbrot|Julia|BurningShip|Tricorn|Multibrot|Phoenix|");
            Console.WriteLine("                              Newton|Nova|BuddhaBrot|Nebulabrot|AntiBuddhabrot|");
            Console.WriteLine("                              AntiNebulabrot|IFS|LSystem|StrangeAttractor|");
            Console.WriteLine("                              UserEquation|Mandelbulb|Mandelbox|Kifs|Sandbox|UserBulb|");
            Console.WriteLine("                              TearDrop|Magnet1|Magnet2|Glynn|Logistic|Halley|");
            Console.WriteLine("                              Secant|Spider|QuaternionJulia|QuaternionMandelbrot|");
            Console.WriteLine("                              Plasma|Flame|Apollonian|Kleinian|");
            Console.WriteLine("                              BicomplexMandelbrot|Dla|");
            Console.WriteLine("                              GeneratedMandelbrotZ2..Z5|");
            Console.WriteLine("                              GeneratedTricorn|GeneratedBurningShip");
            Console.WriteLine("  --theme NAME, -t NAME       Color theme name (default: HSV)");
            Console.WriteLine("  --quality NAME, -q NAME     Draft|Standard|High|Ultra|Extreme (default: Standard)");
            Console.WriteLine("  --width N, -w N             Output width (default: 1920)");
            Console.WriteLine("  --height N, -h N            Output height (default: 1080)");
            Console.WriteLine("  --iter N                    Override iteration count");
            Console.WriteLine("  --lsystem-preset NAME       L-System preset name (Hilbert|Dragon|Koch Snowflake|");
            Console.WriteLine("                              Koch Curve|Sierpinski Arrowhead|Plant|Gosper|");
            Console.WriteLine("                              Pythagoras Tree|Peano|Levy C|Pentigree). Quote names");
            Console.WriteLine("                              with spaces. Requires --fractal LSystem.");
            Console.WriteLine("  --lsystem-depth N           L-System generation depth (0..12). Default 5.");
            Console.WriteLine("  --plasma-roughness F        Plasma diamond-square roughness (0..1). 0 = smooth,");
            Console.WriteLine("                              1 = jagged. Default 0.55. Requires --fractal Plasma.");
            Console.WriteLine("  --plasma-seed N             Plasma PRNG seed. Default 12345.");
            Console.WriteLine("  --flame-preset NAME         Flame preset (Sierpinski Linear|Sierpinski Variation|");
            Console.WriteLine("                              Spherical Pair|Swirl Gasket|Heart Sierpinski|Polar Julia).");
            Console.WriteLine("                              Quote names with spaces. Requires --fractal Flame.");
            Console.WriteLine("  --flame-iter N              Flame chaos-game sample count. Default 8000000.");
            Console.WriteLine("  --flame-gamma F             Flame tone-map gamma. Default 2.2.");
            Console.WriteLine("  --flame-vibrancy F          Flame highlight saturation 0..1. Default 0.8.");
            Console.WriteLine("  --acid-pattern N            Acid Warp static pattern index 0..19. Requires");
            Console.WriteLine("                              --fractal AcidWarp.");
            Console.WriteLine("  --acid-frequency F          Acid Warp pattern frequency. Default 1.0.");
            Console.WriteLine("  --acid-warp-strength F      Acid Warp spatial warp strength. Default 0 (none).");
            Console.WriteLine("  --acid-seed N               Acid Warp PRNG seed. Default 12345.");
            Console.WriteLine("  --domain-warp               Enable domain-warp post-fx distortion (any fractal).");
            Console.WriteLine("  --domain-warp-strength F    Domain-warp strength (implies --domain-warp).");
            Console.WriteLine("  --domain-warp-frequency F   Domain-warp frequency (implies --domain-warp). Default 1.0.");
            Console.WriteLine("  --out PATH, -o PATH         Output file (image) or folder (video) — required");
            Console.WriteLine("  --name NAME, -n NAME        Base filename (default derived from region/coords)");
            Console.WriteLine("  --verbose, -v               Print extra diagnostics");
            Console.WriteLine();
            Console.WriteLine("Mode:");
            Console.WriteLine("  --mode image|video|slideshow, -m ...  Default: image");
            Console.WriteLine();
            Console.WriteLine("Slideshow options (--mode slideshow):");
            Console.WriteLine("  --slideshow NAME            Name of a saved SlideshowConfig preset to drive");
            Console.WriteLine("                              region/theme/timing (implies --mode slideshow).");
            Console.WriteLine("                              When omitted, the active preset is used.");
            Console.WriteLine("  --seconds N                 Total encoded duration in seconds (default 60).");
            Console.WriteLine("  --fps N                     Output frame rate (default 30).");
            Console.WriteLine("  --encode TYPE               Output encode preset (requires ffmpeg.exe):");
            Console.WriteLine("                                h264hq  — libx264 CRF 18 yuv420p MP4 (default)");
            Console.WriteLine("                                h264    — libx264 -qp 0 lossless yuv444p MP4");
            Console.WriteLine("                                ffv1    — FFV1 v3 lossless MKV");
            Console.WriteLine("  --more-colors               Color Focus cadence (8 themes per region, shorter");
            Console.WriteLine("                              per-theme dwell). Synonym of the \"Slideshow: More");
            Console.WriteLine("                              Colors\" context-menu item. (Image-type presets.)");
            Console.WriteLine("                              Video-type presets play one animated zoom leg per");
            Console.WriteLine("                              region (SecondsPerLeg from the preset), honouring");
            Console.WriteLine("                              --start-zoom and --reverse, cross-fading between");
            Console.WriteLine("                              regions.");
            Console.WriteLine("  --out PATH                  Output video file (extension implied by --encode).");
            Console.WriteLine();
            Console.WriteLine("Scene options (--mode scene):");
            Console.WriteLine("  --scene NAME                Saved scene name in scenes.json (implies --mode scene).");
            Console.WriteLine("  --fps N                     Output frame rate (default 30).");
            Console.WriteLine("  --motion-blur N             Accumulation motion-blur sub-frames per output frame");
            Console.WriteLine("                              (1 = off, default). Renders N sub-frames at sub-tick");
            Console.WriteLine("                              camera/param times and averages them. Cost is N× per frame.");
            Console.WriteLine("  --shutter F                 Open-shutter fraction 0<F<=1 (default 0.5 ≈ 180°).");
            Console.WriteLine("  --encode TYPE               ffmpeg preset: h264hq (default) | h264 | ffv1.");
            Console.WriteLine("  --width/--height/--out      Output size + container path (folder or file).");
            Console.WriteLine("  --keep-frames               Keep the intermediate PNG sequence after encode.");
            Console.WriteLine();
            Console.WriteLine("Video options (--mode video):");
            Console.WriteLine("  --seconds VAL               Duration (default 20.0)");
            Console.WriteLine("  --fps N                     Frames per second (default 30)");
            Console.WriteLine("  --start-zoom VAL            Starting zoom (default 0.5 = full set)");
            Console.WriteLine("  --reverse                   Zoom out from target back to full view");
            Console.WriteLine("  --lossless TYPE, -l TYPE    Lossless encode preset (requires ffmpeg.exe):");
            Console.WriteLine("                                none    — built-in WMF H.264 MP4 (default)");
            Console.WriteLine("                                h264    — libx264 -qp 0 lossless MP4");
            Console.WriteLine("                                ffv1    — FFV1 v3 lossless MKV");
            Console.WriteLine("                                h264hq  — libx264 CRF 18 visually lossless MP4");
            Console.WriteLine("  --keep-frames               Keep PNG frame folder after encode");
            Console.WriteLine("  --no-keep-frames            Delete PNG frame folder after encode");
            Console.WriteLine();
            Console.WriteLine("Common options:");
            Console.WriteLine("  --watermark, --no-watermark Turn the watermark OFF. The region/theme + program");
            Console.WriteLine("                              watermark is composited into every emitted frame by");
            Console.WriteLine("                              default across image / video / slideshow modes; pass");
            Console.WriteLine("                              this flag to suppress it.");
            Console.WriteLine();
            Console.WriteLine("Post-FX (parity with the interactive sliders; image, video + slideshow modes):");
            Console.WriteLine("  --brightness N              Brightness -100..100 (0 = none).");
            Console.WriteLine("  --contrast N                Contrast -100..100 (0 = none).");
            Console.WriteLine("  --adaptive N                Adaptive histogram-equalization strength 0..100");
            Console.WriteLine("                              (Mandelbrot only). Alias: --histogram-eq.");
            Console.WriteLine("                              In slideshow mode these override the preset's PostFx");
            Console.WriteLine("                              block; omit to use the preset.");
            Console.WriteLine("  --interior-alpha N          Interior (in-set) opacity 0..255 (#96). 255 = opaque");
            Console.WriteLine("                              (default); below 255 the interior turns translucent");
            Console.WriteLine("                              over the theme's interior background. Mandelbrot 2D only.");
            Console.WriteLine("  --view-transform NAME       Output view transform / tonemap (image mode). One of");
            Console.WriteLine("                              none|reinhard|aces|agx|filmic. Default none (identity).");
            Console.WriteLine("                              Alias: --tonemap.");
            Console.WriteLine("  --exposure EV               Exposure in stops before the view transform, -16..16.");
            Console.WriteLine("                              Default 0 (neutral).");
            Console.WriteLine();
            Console.WriteLine("2D relief (heightfield shading; any relief flag implies --relief):");
            Console.WriteLine("  --relief                    Enable the 2D heightfield relief post-pass.");
            Console.WriteLine("  --relief-raymarch           Use the oblique raymarch path (vs default emboss).");
            Console.WriteLine("  --relief-height F           Height exaggeration (>0). Default 1.0.");
            Console.WriteLine("  --relief-strength F         Blend of relief vs flat colour 0..1. Default 1.0.");
            Console.WriteLine("  --relief-light-azimuth F    Light azimuth degrees 0..360. Default 135.");
            Console.WriteLine("  --relief-light-elevation F  Light elevation degrees -90..90. Default 30.");
            Console.WriteLine("  --relief-shadow F           Shadow strength 0..1. Default 0.6.");
            Console.WriteLine("  --relief-absolute           Emboss absolute-height mode (vs relative). Emboss path.");
            Console.WriteLine("  Raymarch camera (needs --relief-raymarch):");
            Console.WriteLine("  --relief-camera-azimuth F   Camera azimuth degrees 0..360. Default 0.");
            Console.WriteLine("  --relief-camera-elevation F Camera elevation degrees -90..90. Default 45.");
            Console.WriteLine("  --relief-camera-fov F       Camera field of view 1..179. Default 50.");
            Console.WriteLine("  --relief-camera-zoom F      Camera zoom (>0). Default 1.0.");
            Console.WriteLine("  --relief-camera-ortho       Orthographic camera (vs perspective).");
            Console.WriteLine("  --dof-aperture F            Depth-of-field lens radius 0..1 (0 = pinhole, default).");
            Console.WriteLine("                              Implies --relief-raymarch; perspective camera only.");
            Console.WriteLine("                              Blur integrates over --relief supersample taps.");
            Console.WriteLine("  --dof-focus F               DOF focus distance (>=0 world units; 0 = auto-focus");
            Console.WriteLine("                              the fractal centre). Only used with --dof-aperture.");
            Console.WriteLine("  Isolate masking:");
            Console.WriteLine("  --relief-isolate            Isolate high-relief features (drop flat/low-detail).");
            Console.WriteLine("  --relief-isolate-no-detail  Turn OFF the default detail-based isolation.");
            Console.WriteLine("  --relief-isolate-threshold F  Detail threshold 0..1. Default 0.6.");
            Console.WriteLine("  --relief-isolate-by-color   Also isolate by dropping listed colours.");
            Console.WriteLine("  --relief-isolate-colors CSV Colours to drop (CSV). Implies --relief-isolate-by-color.");
            Console.WriteLine("  --relief-isolate-tolerance F  Colour match tolerance 0..1. Default 0.12.");
            Console.WriteLine();
            Console.WriteLine("Remote rendering (uses a saved FFClient connection + render preset):");
            Console.WriteLine("  --remote                    Route this batch through a remote FracturingFog server");
            Console.WriteLine("  --connection NAME           Saved client-connection name (required with --remote)");
            Console.WriteLine("  --render NAME               Saved render-preset name (required with --remote)");
            Console.WriteLine("                              Image vs video is decided by the preset's Mode field,");
            Console.WriteLine("                              NOT by --mode. Match --out extension to the preset.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  FracturingFog --batch --region \"Seahorse Valley\" --theme Fire \\");
            Console.WriteLine("                --width 3840 --height 2160 --out C:\\out\\seahorse.png");
            Console.WriteLine();
            Console.WriteLine("  FracturingFog --batch --mode video --region \"Mini Mandelbrot\" \\");
            Console.WriteLine("                --theme Plasma --seconds 30 --fps 30 --out C:\\out\\zoom.mp4");
            Console.WriteLine();
            Console.WriteLine("  FracturingFog --batch --mode video --region \"Mini Mandelbrot\" \\");
            Console.WriteLine("                --theme Plasma --lossless ffv1 --seconds 30 --out C:\\out\\");
            Console.WriteLine();
            Console.WriteLine("  FracturingFog --batch --slideshow \"Default\" --seconds 90 \\");
            Console.WriteLine("                --width 1920 --height 1080 --fps 30 --encode h264hq \\");
            Console.WriteLine("                --out C:\\out\\slideshow.mp4");
            Console.WriteLine();
            Console.WriteLine("  Remote image (preset Mode = image):");
            Console.WriteLine("  FracturingFog --batch --remote --connection render-box \\");
            Console.WriteLine("                --render seahorse_4k --out C:\\out\\poster.png");
            Console.WriteLine();
            Console.WriteLine("  Remote video (preset Mode = video):");
            Console.WriteLine("  FracturingFog --batch --remote --connection render-box \\");
            Console.WriteLine("                --render seahorse_30s --out C:\\out\\zoom.mp4");
        }
    }
}
