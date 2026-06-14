// Batch/BatchEntry.cs
// Entry point for headless --batch processing.
//
// Renders either a single still image or a zoom video to disk without
// showing any UI. Attaches to the parent process's console so the progress
// meter is visible from cmd / PowerShell.

using System;
using System.Diagnostics;
using System.Drawing.Imaging;
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

            try
            {
                if (opts.Remote)
                    return RemoteBatchRunner.Run(opts);
                return opts.Mode switch
                {
                    BatchMode.Image => BatchRenderer.RenderImage(opts),
                    BatchMode.Video => BatchRenderer.RenderVideo(opts),
                    BatchMode.Slideshow => BatchRenderer.RenderSlideshow(opts),
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
            Console.WriteLine("                              Secant|Spider|QuaternionJulia|GeneratedMandelbrotZ2..Z5|");
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
            Console.WriteLine("                              Colors\" context-menu item.");
            Console.WriteLine("  --out PATH                  Output video file (extension implied by --encode).");
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
            Console.WriteLine("  --watermark                 Composite region/theme + program watermark into every");
            Console.WriteLine("                              emitted frame across image / video / slideshow modes");
            Console.WriteLine("                              (image mode already watermarks unconditionally).");
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
