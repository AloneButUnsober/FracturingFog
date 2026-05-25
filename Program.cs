// Fracturing Fog - MandelbrotExplorer — .NET 10 / C# 14 / DirectX 11 via Vortice.DirectX 3.8.3

using System;
using System.Windows.Forms;

using FracturingFog.Benchmarks;

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

        // Phase 2 bootstrap: --avalonia launches the new responsive shell.
        // Default path stays on WinForms so existing workflow is unaffected
        // until every dialog has been ported and the Avalonia shell reaches
        // feature parity. See PHASE2_AVALONIA_MIGRATION.md for status.
        if (args.Length > 0 && args[0] == "--avalonia")
            return FracturingFog.UI.Avalonia.AvaloniaShell.Run(args);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
