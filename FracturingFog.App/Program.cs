// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// -----------------------------------------------------------------------------
// Fracturing Fog
// Program.cs
//
// FracturingFog.App entry point — cross-platform Avalonia shell launcher.
//
// Copyright (c) 2026 Bradley Brown(DanarDalin) - AloneButUnsober
// -----------------------------------------------------------------------------
//
// S-X2 (2026-06-23) — replaces the Phase X.0 scaffold stub with the real
// cross-platform entry. Targets the Avalonia shell directly (no WinForms
// fallback flag — that lives on the legacy WinExe). Linux/macOS launchers
// (AppRun in the AppImage, .app/Contents/MacOS/ launcher) exec this exe.
//
// S-X3 (2026-06-23) — multi-targeted as net10.0;net10.0-windows. The Win leg
// ProjectReferences FracturingFog.Win directly so WindowsBootstrap.Install
// is a compile-time call (no reflection). The cross-plat leg leaves the hook
// surface unwired and AvaloniaShellBootstrap takes its no-op fallbacks.

using System;
using System.IO;

using FracturingFog;
using FracturingFog.Hosting;
using FracturingFog.Rendering;

namespace FracturingFog.App;

internal static class Program
{
    /// <summary>
    /// Avalonia XAML previewer expects a static factory named
    /// <c>BuildAvaloniaApp</c> on the entry assembly. Forward to the shell
    /// so the previewer (Accelerate / OSS designer) can boot against the
    /// cross-plat App exe the same way it does against the legacy WinExe.
    /// </summary>
    public static global::Avalonia.AppBuilder BuildAvaloniaApp()
        => FracturingFog.UI.Avalonia.AvaloniaShell.BuildAvaloniaApp();

    public static int Main(string[] args)
    {
        // S-X7.10 (2026-06-23) — startup heartbeat. Linux smoke runs reported
        // empty output even with `./FracturingFog.App > ff.log 2>&1`; this
        // prints + flushes immediately so the user can confirm Main reached
        // and stdio is hooked. Cheap, prints once at process start.
        try
        {
            Console.Error.WriteLine($"[FF] Main entered args=[{string.Join(' ', args)}] pid={Environment.ProcessId}");
            Console.Error.Flush();
        }
        catch { /* stdio absent — silently proceed */ }

        // Phase X.5 / Slice 5.1 — per-RID ILGPU device-kind smoke. Asserts
        // CPU fallback is reachable on the current host. Used by the release
        // workflow smoke step on the Linux + macOS legs where CUDA/OpenCL
        // drivers are absent. Mirrors the same flag on the legacy WinExe.
        if (args.Length > 0 && args[0] == "--ilgpu-probe")
        {
            bool ok = FracturingFog.Calculators.AcceleratorProbe.RunSmoke(out string report);
            string outPath = Path.Combine(AppContext.BaseDirectory, "ilgpu-probe.out");
            try { File.WriteAllText(outPath, report); } catch { }
            Console.Write(report);
            return ok ? 0 : 1;
        }

        // Phase 2.4 cross-platform GL smoke. Opens a 256x256 Silk.NET window
        // via GLFW, uploads one solid frame, prints the renderer description,
        // exits 0. CI hooks this on the linux-x64 leg under xvfb-run; failure
        // means the Silk.NET native chain or libGL.so.1 is broken on the runner.
        if (args.Length > 0 && args[0] == "--silk-smoke")
        {
            try
            {
                string desc = FracturingFog.Rendering.Silk.SilkStandaloneRunner.SmokeOneFrame();
                Console.WriteLine($"silk-smoke OK: {desc}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"silk-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        // #271 (parent #58) — OpenAL live-audio probe. Cross-platform: reports
        // whether the OpenAL runtime loads, the AudioCapabilityProbe capability
        // set, and (when present) the live backend's negotiated caps incl. Linux
        // monitor-loopback detection. This is the flag the Linux/macOS smoke uses
        // (the WinExe carries the same block). Exit 0 = present, 3 = absent
        // (informational), 2 = unexpected error.
        if (args.Length > 0 && args[0] == "--openalprobe")
        {
            try
            {
                bool available = FracturingFog.Audio.OpenAlRuntime.IsAvailable();
                var caps = FracturingFog.Audio.AudioCapabilityProbe.Detect();
                Console.WriteLine($"openalprobe: runtime={(available ? "present" : "absent")}");
                Console.WriteLine($"openalprobe: probe-caps={caps}");
                if (available)
                {
                    using var be = new FracturingFog.Audio.OpenAlAudioBackend();
                    Console.WriteLine($"openalprobe: backend-caps={be.Capabilities}");
                }
                Console.Out.Flush();
                return available ? 0 : 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"openalprobe FAIL: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        // UserBulbSandboxGpuSpike — Engine-side GPU spike harness. Cross-plat
        // because the kernel sits in FracturingFog.Engine and routes through
        // ILGPU's CPU/CUDA/OpenCL accelerator picker.
        if (args.Length > 0 && args[0] == "--ubspike")
            return FracturingFog.Calculators.UserBulbSandboxGpuSpike.Run();

        // S-X3 (2026-06-23) — Windows-only services install via direct call
        // on the net10.0-windows TFM. The MSBuild WINDOWS constant is defined
        // only when TargetFramework ends with -windows; the net10.0 TFM
        // compiles this block out entirely, so the cross-plat publish has
        // no reference to FracturingFog.Win at all.
        //
        // S-X7.1b (2026-06-23) — moved above the --server check so the
        // headless server gets the Win hooks too (Mp4Writer factory, etc).
        // Both the GUI shell and the server path consume the bootstrap hook
        // surface, so the install needs to run before either entry point.
#if WINDOWS
        FracturingFog.Win.WindowsBootstrap.Install();
#endif

        // S-X7.1b (2026-06-23) — headless JSON-RPC server, now cross-plat.
        // ServerEntry + HostFractalRenderEngine ported off System.Drawing /
        // Media Foundation onto ImageExport (Skia) + BootstrapHooks.NativeVideoWriterFactoryHook
        // (Mp4Writer on Win, ffmpeg fallback on Linux/macOS).
        if (args.Length > 0 && args[0] == "--server")
            return FracturingFog.ServerHost.ServerEntry.Run(args);

        // D-2b — cluster master / worker entry points.
        if (args.Length > 0 && args[0] == "--master")
            return FracturingFog.ServerHost.ClusterEntry.RunMaster(args);
        if (args.Length > 0 && args[0] == "--worker")
            return FracturingFog.ServerHost.ClusterEntry.RunWorker(args);
        if (args.Length > 0 && args[0] == "--cluster-parity")
            return FracturingFog.ServerHost.ClusterParitySelfTest.Run(args);
        if (args.Length > 0 && args[0] == "--cluster-scale")
            return FracturingFog.ServerHost.ClusterScaleSelfTest.Run(args);
        if (args.Length > 0 && args[0] == "--cluster-video-parity")
            return FracturingFog.ServerHost.ClusterVideoParitySelfTest.Run(args);

        // Phase X.4 / Slice 4.1 — --renderer override. Default is
        // RendererBackend.Auto (DX on Win, Silk on Linux/macOS, picked by
        // RendererFactory.Create from the surface kind). Explicit values let
        // the user parity-test Silk or Skia on any host or downgrade from
        // DX12 to Silk on a busy Windows GPU.
        //   --renderer auto   → default; same as omitting the flag.
        //   --renderer dx     → force DX (Win only).
        //   --renderer silk   → force Silk.NET OpenGL.
        //   --renderer skia   → force SkiaSharp CPU.
        // Selection lands on RendererFactory before AvaloniaShell.Run so
        // OnSurfaceReady picks it up the first time a surface arrives.
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--renderer", StringComparison.OrdinalIgnoreCase))
                continue;
            string val = args[i + 1];
            RendererBackend? backend = val.ToLowerInvariant() switch
            {
                "auto"   => RendererBackend.Auto,
                "dx"     => RendererBackend.Dx,
                "silk"   => RendererBackend.Silk,
                "skia"   => RendererBackend.Skia,
                "vulkan" => RendererBackend.Vulkan,
                _        => null,
            };
            if (backend == null)
            {
                Console.Error.WriteLine(
                    $"--renderer expects one of: auto | dx | silk | skia | vulkan (got '{val}').");
                return 2;
            }
            RendererFactory.PreferredBackend = backend.Value;
            break;
        }

        // S-X7.1b — WindowsBootstrap.Install moved above the --server check
        // (see comment further up); no second install needed here.
        return FracturingFog.UI.Avalonia.AvaloniaShell.Run(
            args,
            FracturingFog.Hosting.AvaloniaShellBootstrap.OnSurfaceReady);
    }
}
