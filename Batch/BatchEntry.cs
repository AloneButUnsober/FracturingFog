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
            // #999 — when both streams are already redirected (the Command panel's
            // Run, a CI log, `> out.txt 2>&1`) write to them; attaching / allocating
            // a console would pop up a window and bypass the pipes.
            if (OperatingSystem.IsWindows() && !(Console.IsOutputRedirected && Console.IsErrorRedirected))
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

            // #997 — reject an unknown --param key / bad value before any render
            // starts (the keys are region-snapshot names, resolved Engine-side).
            if (opts.Params.Count > 0
                && RegionFractalParams.FromKeyValues(opts.Params, opts.FractalType, out string? paramError) == null)
            {
                Console.Error.WriteLine($"batch: {paramError}");
                return 2;
            }

            // Load user-defined themes + regions so --region and --theme can
            // resolve names the user authored interactively in earlier runs.
            try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
            try { FractalRegionLibrary.Instance.Load(); } catch { }
            // #947 — user-code regions (UserEquation / Sandbox / UserBulb) name their
            // equation in these stores; without loading them every lookup misses
            // and the frame renders blank. Read-only here: the interactive
            // startup's DSL migrations (which write) are deliberately not run.
            try { UserEquationStore.Instance.Load(); } catch { }
            try { SandboxEquationStore.Instance.Load(); } catch { }
            try { UserBulbStore.Instance.Load(); } catch { }
            // Scene mode also needs the scene + animation libraries so --scene
            // names and shot-attached animations resolve.
            if (opts.Mode == BatchMode.Scene)
            {
                try { FracturingFog.Models.AnimationLibrary.Instance.Load(); } catch { }
                try { FracturingFog.Models.SceneLibrary.Instance.Load(); } catch { }
            }

            // #998 — named Lighting & FX preset / animation: resolve before any
            // render starts so a typo fails fast (exit 2) instead of mid-run.
            if (!string.IsNullOrWhiteSpace(opts.LightingPresetName)
                && LightingFxPresetLibrary.Get(LightingFxPresetLibrary.Load(), opts.LightingPresetName!) == null)
            {
                Console.Error.WriteLine($"batch: Lighting & FX preset '{opts.LightingPresetName}' not found in lighting-fx-presets.json.");
                return 2;
            }
            if (!string.IsNullOrWhiteSpace(opts.AnimationName))
            {
                try { FracturingFog.Models.AnimationLibrary.Instance.Load(); } catch { }
                if (FracturingFog.Models.AnimationLibrary.Instance.GetByName(opts.AnimationName) == null)
                {
                    Console.Error.WriteLine($"batch: animation '{opts.AnimationName}' not found in animations.json.");
                    return 2;
                }
                if (opts.Mode != BatchMode.Video)
                    Console.WriteLine("  note     : --animation plays only in --mode video; ignored.");
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
                    BatchMode.Regrade => BatchRenderer.RenderRegrade(opts),
                    BatchMode.Relight => BatchRenderer.RenderRelight(opts),
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
            Console.WriteLine("A render needs --out plus a view source: --region NAME, or all of");
            Console.WriteLine("--x --y --zoom. --slideshow / --scene / --regrade-exr / --relight-from");
            Console.WriteLine("bring their own source; --remote uses a saved render preset.");
            Console.WriteLine();
            // #993 — the flag reference is generated from BatchFlagCatalog so it
            // can never drift from the parser (guard-tested).
            Console.Write(BatchFlagCatalog.FormatUsage());
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
