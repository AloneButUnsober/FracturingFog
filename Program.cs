// Fracturing Fog - MandelbrotExplorer — .NET 10 / C# 14 / DirectX 11 via Vortice.DirectX 3.8.3

using System;
using System.Windows.Forms;

using FracturingFog.Benchmarks;
using FracturingFog.Batch;
using FracturingFog.ServerHost;

namespace FracturingFog;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench")
            return BenchEntry.Run(args);

        if (args.Length > 0 && args[0] == "--ubtest")
            return UserBulbSelfTest.Run();

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

        // Headless batch processing: render single image or zoom video to disk
        // without showing any UI. Attaches to the parent console so the
        // progress meter is visible from cmd/PowerShell.
        if (args.Length > 0 && (args[0] == "--batch" || args[0] == "-b"))
            return BatchEntry.Run(args);

        // Headless render server: JSON-RPC over mTLS TCP, reuses the same
        // PosterRenderer + video pipeline the --batch path drives. Mutex
        // gated so only one server instance runs per machine.
        if (args.Length > 0 && args[0] == "--server")
            return ServerEntry.Run(args);

        // --winforms forces the legacy WinForms shell. Default path is the
        // Avalonia shell.
        bool forceWinForms = false;
        foreach (var a in args)
            if (string.Equals(a, "--winforms", StringComparison.OrdinalIgnoreCase))
                { forceWinForms = true; break; }

        if (!forceWinForms)
            return FracturingFog.UI.Avalonia.AvaloniaShell.Run(
                args,
                FracturingFog.Hosting.AvaloniaShellBootstrap.OnSurfaceReady);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
