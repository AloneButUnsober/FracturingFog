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

        // CalculatorGen-emitted self-tests: validates that the scalar and
        // AVX2 paths of a generated calculator agree on a fixed sample grid.
        // Pass the calculator name (sans "Calculator" suffix) as arg[1].
        // Currently wired for MandelbrotZ2; add cases as more calculators
        // are generated.
        // --gentestbench: time the generated MandelbrotZ2 calculator
        // at a few zoom levels. Reports ms/frame per location. Useful
        // for evaluating perf changes to the CalcGen template.
        if (args.Length > 0 && args[0] == "--gentestbench")
        {
            var sw = new System.Diagnostics.Stopwatch();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CalcGen benchmark — MandelbrotZ2");
            var palette = new FracturingFog.Models.HsvPalette();
            (string name, double cx, double cy, double zoom, int iter)[] cases =
            {
                ("default",   -0.5,    0.0,   1.0,     256),
                ("shallow",   -0.75,   0.1,   20.0,    256),
                ("mid-1e3",   -0.745,  0.113, 1.0e3,  1024),
                ("deep-1e6",  -0.745,  0.113, 1.0e6,  2048),
            };
            foreach (var c in cases)
            {
                using var calc = new FracturingFog.Calculators.Generated.MandelbrotZ2Calculator(640, 480)
                {
                    CenterX = c.cx, CenterY = c.cy,
                    Zoom = c.zoom, MaxIterations = c.iter,
                    ColorMap = palette,
                    UsePerturbation = true, UseBla = true, UseSa = true,
                };
                // Warm-up.
                calc.Calculate();
                sw.Restart();
                const int frames = 3;
                for (int f = 0; f < frames; f++) calc.Calculate();
                sw.Stop();
                long avgMs = sw.ElapsedMilliseconds / frames;
                sb.AppendLine($"  {c.name,-12} zoom={c.zoom:G3,-8} iter={c.iter,5} → {avgMs,5} ms/frame");
            }
            string benchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "gentestbench.out");
            System.IO.File.WriteAllText(benchPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --benchmark "<equation>" [--name N] [--width W] [--height H]
        // Hot-compiles an arbitrary equation into a calculator and times
        // it across a fixed viewpoint ladder (shallow → deep zoom). Lets
        // Phase D-2 perf changes (SA orders, BLA hierarchy, cached SA
        // tables) be measured against an unchanging baseline. Output is
        // the same "name zoom iter → ms/frame" table as --gentestbench
        // but the equation is user-supplied.
        if (args.Length > 0 && args[0] == "--benchmark")
        {
            return BenchmarkEquation(args);
        }

        // --saprobe: render MandelbrotZ2 at user-bug coords across a
        // zoom ladder, dump iter histogram per zoom. Catches the "solid
        // blob" failure mode reported at deep zoom — when SA / pert /
        // ref-orbit collapse, all pixels in a region escape on the same
        // iter so the histogram has one massive bucket; correct render
        // is broadly distributed.
        if (args.Length > 0 && args[0] == "--saprobe")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SA-probe — MandelbrotZ2 deep-zoom histogram");
            var palette = new FracturingFog.Models.HsvPalette();
            (string label, double zoom, int iter)[] cases =
            {
                ("1e9",   1.0e9,  2048),
                ("1e10",  1.0e10, 2048),
                ("1e11",  1.0e11, 2048),
                ("1.08e12", 1.08e12, 4096),
                ("1e13",  1.0e13, 4096),
                ("1e14",  1.0e14, 4096),
                ("1e15",  1.0e15, 4096),
                ("1e16",  1.0e16, 4096),
            };
            foreach (var c in cases)
            {
                using var calc = new FracturingFog.Calculators.Generated.MandelbrotZ2Calculator(64, 64)
                {
                    CenterX = -1.1726999042772253,
                    CenterXLo = 8.9529605787776783E-17,
                    CenterY = -0.2968356710071185,
                    CenterYLo = -2.3536240906562374E-18,
                    Zoom = c.zoom, MaxIterations = c.iter,
                    ColorMap = palette,
                    UsePerturbation = true, UseBla = true,
                    UseSa = true,
                };
                calc.Calculate();
                var legacy = new FracturingFog.MandelbrotCalculator(64, 64)
                {
                    CenterX = -1.1726999042772253,
                    CenterXLo = 8.9529605787776783E-17,
                    CenterY = -0.2968356710071185,
                    CenterYLo = -2.3536240906562374E-18,
                    Zoom = c.zoom, MaxIterations = c.iter,
                    ColorMap = palette,
                };
                legacy.Calculate();
                // Color histogram + in-set count.
                var genDistinct = new System.Collections.Generic.HashSet<uint>();
                int genInSet = 0;
                uint inSetColor = ((FracturingFog.Interefaces.IColorMap)palette).InSetColor;
                foreach (var p in calc.ColorBuffer) { genDistinct.Add(p); if (p == inSetColor) genInSet++; }
                var legDistinct = new System.Collections.Generic.HashSet<uint>();
                int legInSet = 0;
                foreach (var p in legacy.ColorBuffer) { legDistinct.Add(p); if (p == inSetColor) legInSet++; }
                var legIters = new System.Collections.Generic.HashSet<int>();
                foreach (var i in legacy.IterationBuffer) legIters.Add(i);
                sb.AppendLine($"  zoom={c.label,-8} iter={c.iter,5}  gen={genDistinct.Count,5} ({calc.LastPrecisionLabel,-10})  legacy={legDistinct.Count,5} iter-uniq={legIters.Count,4}");
            }
            string outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "saprobe.out");
            System.IO.File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // Generated vs legacy MandelbrotCalculator comparison harness.
        // Renders both at a small grid of standard viewpoints and reports
        // per-location pixel-count disagreement. PASS when each location
        // is within MismatchTolerancePct of legacy.
        if (args.Length > 0 && args[0] == "--legacycmp")
        {
            bool okCmp = FracturingFog.Calculators.Generated
                .GeneratedVsLegacyTest.Run(out string cmpReport);
            string cmpPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "legacycmp.out");
            System.IO.File.WriteAllText(cmpPath, cmpReport + Environment.NewLine);
            Console.WriteLine(cmpReport);
            return okCmp ? 0 : 1;
        }

        // --calcgen-test: Run CalculatorGen AST pipeline unit tests
        // (parser, lexer diagnostics, differentiator, simplifier, SA
        // detector). Self-contained, no test framework dep — writes
        // calcgen-test.out next to the exe.
        if (args.Length > 0 && args[0] == "--calcgen-test")
        {
            bool okCt = FracturingFog.CalculatorGen.Parser
                .CalculatorGenUnitTests.Run(out string ctReport);
            string ctPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "calcgen-test.out");
            System.IO.File.WriteAllText(ctPath, ctReport);
            Console.WriteLine(ctReport);
            return okCt ? 0 : 1;
        }

        if (args.Length > 0 && args[0] == "--gentest")
        {
            string target = args.Length > 1 ? args[1] : "MandelbrotZ2";
            string report;
            bool ok;
            switch (target)
            {
                case "MandelbrotZ2":
                    ok = FracturingFog.Calculators.Generated
                            .MandelbrotZ2CalculatorSelfTest.Run(out report);
                    break;
                default:
                    report = $"Unknown gentest target: {target}";
                    ok = false;
                    break;
            }
            // WinExe subsystem detaches stdout; write to file so the result is
            // observable from a parent shell.
            string outPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "gentest.out");
            System.IO.File.WriteAllText(outPath, report + Environment.NewLine);
            Console.WriteLine(report);   // harmless if there's an attached console
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

    private static int BenchmarkEquation(string[] args)
    {
        string? equation = null;
        string  name     = "UserBench";
        int     width    = 640;
        int     height   = 480;
        int     frames   = 3;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--equation": case "-e": equation = args[++i]; break;
                case "--name":     case "-n": name     = args[++i]; break;
                case "--width":               width    = int.Parse(args[++i]); break;
                case "--height":              height   = int.Parse(args[++i]); break;
                case "--frames":              frames   = int.Parse(args[++i]); break;
            }
        }
        if (string.IsNullOrWhiteSpace(equation))
        {
            Console.Error.WriteLine("--benchmark requires --equation \"<expr>\".");
            return 2;
        }

        // Hot-load harvests references from AppDomain.GetAssemblies() —
        // assemblies the .NET loader hasn't touched yet won't appear.
        // The UserEquation dialog avoids this because the UI path has
        // already JIT-touched ILGPU / Parallel / SIMD. In the headless
        // --benchmark path we must force-load them here so Roslyn sees
        // the same closure of refs. Touching .Assembly.Location prevents
        // the JIT from dead-code-eliminating the typeof().
        Type[] forceLoad = {
            typeof(ILGPU.Context),
            typeof(ILGPU.Runtime.Accelerator),
            typeof(System.Threading.Tasks.Parallel),
            typeof(System.Runtime.Intrinsics.X86.Avx2),
            typeof(System.Runtime.Intrinsics.X86.Avx512F),
            typeof(FracturingFog.Models.HsvPalette),
            typeof(FracturingFog.Interefaces.IFractalCalculator),
        };
        foreach (var t in forceLoad)
            _ = t.Assembly.Location;

        var hot = FracturingFog.CalculatorGen.CalculatorGenHotLoad
            .TryCompileAndLoad(equation, name);
        if (!hot.Ok)
        {
            Console.Error.WriteLine(hot.Error);
            return 1;
        }

        var sw = new System.Diagnostics.Stopwatch();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CalcGen benchmark — {name} (equation: {equation})");
        var palette = new FracturingFog.Models.HsvPalette();
        (string label, double cx, double cy, double zoom, int iter)[] cases =
        {
            ("default",   -0.5,    0.0,   1.0,     256),
            ("shallow",   -0.75,   0.1,   20.0,    256),
            ("mid-1e3",   -0.745,  0.113, 1.0e3,  1024),
            ("deep-1e6",  -0.745,  0.113, 1.0e6,  2048),
            ("deep-1e9",  -0.745,  0.113, 1.0e9,  4096),
        };
        foreach (var c in cases)
        {
            var calc = (FracturingFog.Interefaces.IFractalCalculator)
                Activator.CreateInstance(hot.CalculatorType!, width, height)!;
            calc.CenterX = c.cx; calc.CenterY = c.cy;
            calc.Zoom = c.zoom; calc.MaxIterations = c.iter;
            calc.ColorMap = palette;

            using var ctsWarm = new System.Threading.CancellationTokenSource();
            calc.Calculate(ctsWarm.Token);                   // warm-up
            sw.Restart();
            for (int f = 0; f < frames; f++)
            {
                using var cts = new System.Threading.CancellationTokenSource();
                calc.Calculate(cts.Token);
            }
            sw.Stop();
            long avgMs = sw.ElapsedMilliseconds / Math.Max(1, frames);
            sb.AppendLine($"  {c.label,-12} zoom={c.zoom,-10:G3} iter={c.iter,5} → {avgMs,5} ms/frame");

            if (calc is IDisposable d) d.Dispose();
        }

        string benchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "benchmark.out");
        System.IO.File.WriteAllText(benchPath, sb.ToString());
        Console.WriteLine(sb.ToString());
        return 0;
    }
}
