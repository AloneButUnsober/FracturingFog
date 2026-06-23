// FracturingFog.App entry point — cross-platform Avalonia shell launcher.
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

        // UserBulbSandboxGpuSpike — Engine-side GPU spike harness. Cross-plat
        // because the kernel sits in FracturingFog.Engine and routes through
        // ILGPU's CPU/CUDA/OpenCL accelerator picker.
        if (args.Length > 0 && args[0] == "--ubspike")
            return FracturingFog.Calculators.UserBulbSandboxGpuSpike.Run();

        // S-X7.1 (2026-06-23) — headless JSON-RPC server flag. Without this
        // dispatch the child process re-entered AvaloniaShell.Run and opened
        // a second GUI window. Full server (ServerEntry + HostFractalRenderEngine)
        // still drags System.Drawing.Imaging via PosterRenderer PNG export, so
        // wire-up is gated to Windows for now; the legacy WinExe carries the
        // running server until HostFractalRenderEngine ports to SkiaSharp.
        // Linux/macOS: exit early with a clear error rather than silently
        // opening a duplicate GUI shell.
        if (args.Length > 0 && args[0] == "--server")
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine(
                    "FracturingFog: --server is not yet supported on this platform. " +
                    "Server-side PosterRenderer still depends on System.Drawing.Imaging; " +
                    "cross-platform port is tracked as S-X7.1 follow-up.");
                return 2;
            }
            Console.Error.WriteLine(
                "FracturingFog: --server requires the legacy WinExe (FracturingFog.exe). " +
                "Launch via 'FracturingFog.exe --server' instead of FracturingFog.App.");
            return 2;
        }

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
                "auto" => RendererBackend.Auto,
                "dx"   => RendererBackend.Dx,
                "silk" => RendererBackend.Silk,
                "skia" => RendererBackend.Skia,
                _      => null,
            };
            if (backend == null)
            {
                Console.Error.WriteLine(
                    $"--renderer expects one of: auto | dx | silk | skia (got '{val}').");
                return 2;
            }
            RendererFactory.PreferredBackend = backend.Value;
            break;
        }

        // S-X3 (2026-06-23) — Windows-only services install via direct call
        // on the net10.0-windows TFM. The MSBuild WINDOWS constant is defined
        // only when TargetFramework ends with -windows; the net10.0 TFM
        // compiles this block out entirely, so the cross-plat publish has
        // no reference to FracturingFog.Win at all. ColorSampleBridge +
        // SyncDialogs remain wired by the legacy WinExe (their backends still
        // live in the WinForms-bound WinExe) — moving those into
        // FracturingFog.Win is a later slice.
#if WINDOWS
        FracturingFog.Win.WindowsBootstrap.Install();
#endif

        return FracturingFog.UI.Avalonia.AvaloniaShell.Run(
            args,
            FracturingFog.Hosting.AvaloniaShellBootstrap.OnSurfaceReady);
    }
}
