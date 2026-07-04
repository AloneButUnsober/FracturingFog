// Fracturing Fog - MandelbrotExplorer — .NET 10 / C# 14 / DirectX 11 via Vortice.DirectX 3.8.3

using System;
using System.Runtime.Versioning;
using System.Windows.Forms;

using FracturingFog.Benchmarks;
using FracturingFog.Batch;
using FracturingFog.Hosting;
using FracturingFog.ServerHost;

namespace FracturingFog;

/// <summary>
/// S-X1 carve (2026-06-23) — Windows-only IColorSampleBridge wrapper over
/// the WinForms-bound DesktopEyedropper. Installed onto
/// AvaloniaShellBootstrap from Program.Main before AvaloniaShell.Run so the
/// Color Theme Editor's eyedropper continues to work on Windows. Cross-plat
/// hosts (FracturingFog.App on Linux/macOS) leave the bridge null and the
/// SampleColorRequested handler completes without picking.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinExeColorSampleBridge : IColorSampleBridge
{
    public bool IsActive => FracturingFog.Views.Editors.DesktopEyedropper.IsActive;

    public void Begin(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        FracturingFog.Views.Editors.DesktopEyedropper.Begin(
            c => onPicked((c.R, c.G, c.B)),
            onCancelled);
    }
}

/// <summary>
/// S-X1b carve (2026-06-23) — Windows-only IHostSyncDialogs implementation
/// over the System.Windows.Forms common dialogs. Lives in the WinExe because
/// the WinExe is the only assembly with UseWindowsForms=true on its csproj.
/// The source-editor VMs (UserEquation, UserBulb, ColorGen) raise sync
/// Func/EventArgs.Result events the bootstrap satisfies by calling this
/// bridge; Avalonia's async dialog stack can't satisfy synchronous event
/// returns without re-entering the dispatcher (which crashed on Cancel/X).
/// Cross-plat hosts leave BootstrapHooks.SyncDialogs null and the bootstrap
/// helpers fall through to no-op until the VM events themselves move to
/// async patterns.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinFormsSyncDialogs : IHostSyncDialogs
{
    public string? PromptName(string title, string prompt, string defaultValue)
    {
        using var dlg = new System.Windows.Forms.Form
        {
            Text = string.IsNullOrEmpty(title) ? "Enter Name" : title,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new System.Drawing.Size(420, 124),
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi,
        };

        var lbl = new System.Windows.Forms.Label
        {
            Text = prompt ?? "Enter a name:",
            Location = new System.Drawing.Point(12, 12),
            Size = new System.Drawing.Size(396, 24),
            AutoEllipsis = true,
        };
        var box = new System.Windows.Forms.TextBox
        {
            Text = defaultValue ?? string.Empty,
            Location = new System.Drawing.Point(12, 40),
            Size = new System.Drawing.Size(396, 24),
            Anchor = System.Windows.Forms.AnchorStyles.Left
                   | System.Windows.Forms.AnchorStyles.Right
                   | System.Windows.Forms.AnchorStyles.Top,
        };
        var ok = new System.Windows.Forms.Button
        {
            Text = "OK",
            DialogResult = System.Windows.Forms.DialogResult.OK,
            Location = new System.Drawing.Point(252, 80),
            Size = new System.Drawing.Size(76, 28),
        };
        var cancel = new System.Windows.Forms.Button
        {
            Text = "Cancel",
            DialogResult = System.Windows.Forms.DialogResult.Cancel,
            Location = new System.Drawing.Point(332, 80),
            Size = new System.Drawing.Size(76, 28),
        };

        dlg.Controls.Add(lbl);
        dlg.Controls.Add(box);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dlg.Shown += (_, _) => { box.SelectAll(); box.Focus(); };

        var result = dlg.ShowDialog();
        if (result != System.Windows.Forms.DialogResult.OK) return null;
        string r = box.Text;
        return string.IsNullOrWhiteSpace(r) ? null : r;
    }

    public bool ConfirmYesNo(string message, string title)
        => System.Windows.Forms.MessageBox.Show(
               message, title,
               System.Windows.Forms.MessageBoxButtons.YesNo,
               System.Windows.Forms.MessageBoxIcon.Question)
           == System.Windows.Forms.DialogResult.Yes;

    public void ShowInfo(string title, string body, bool isError)
        => System.Windows.Forms.MessageBox.Show(
               body, title,
               System.Windows.Forms.MessageBoxButtons.OK,
               isError ? System.Windows.Forms.MessageBoxIcon.Error
                       : System.Windows.Forms.MessageBoxIcon.Information);

    public string? PickOpenSync(string title, string filter)
    {
        using var d = new System.Windows.Forms.OpenFileDialog
        {
            Title = string.IsNullOrEmpty(title) ? "Open" : title,
            Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
            CheckFileExists = true,
        };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
    }

    public string? PickSaveSync(string title, string filter, string defaultName)
    {
        using var d = new System.Windows.Forms.SaveFileDialog
        {
            Title = string.IsNullOrEmpty(title) ? "Save" : title,
            Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
            FileName = defaultName ?? string.Empty,
            OverwritePrompt = true,
        };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
    }
}

static class Program
{
    // Avalonia XAML previewer (Accelerate / OSS designer) scans the WinExe's
    // entry assembly for a static `BuildAvaloniaApp` factory and bails silently
    // when it is absent — "Previewer process exited unexpectedly" with no
    // output. Forward to the shell so the previewer can boot. Not called at
    // runtime; the avalonia CLI path goes through AvaloniaShell.Run directly.
    public static global::Avalonia.AppBuilder BuildAvaloniaApp()
        => FracturingFog.UI.Avalonia.AvaloniaShell.BuildAvaloniaApp();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench")
            return BenchEntry.Run(args);

        if (args.Length > 0 && args[0] == "--ubtest")
            return UserBulbSelfTest.Run();

        if (args.Length > 0 && args[0] == "--ubspike")
            return FracturingFog.Calculators.UserBulbSandboxGpuSpike.Run();

        // Phase X.5 / Slice 5.1 — per-RID ILGPU device-kind smoke. Asserts
        // CPU fallback is reachable; emits ilgpu-probe.out next to the exe.
        // Also wired in FracturingFog.App/Program.cs so the same flag works
        // on Linux + macOS legs of the release workflow.
        if (args.Length > 0 && args[0] == "--ilgpu-probe")
        {
            bool okIlg = FracturingFog.Calculators.AcceleratorProbe.RunSmoke(out string ilgReport);
            string ilgPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ilgpu-probe.out");
            try { System.IO.File.WriteAllText(ilgPath, ilgReport); } catch { }
            Console.Write(ilgReport);
            return okIlg ? 0 : 1;
        }

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
                ("1e30",  1.0e30, 4096),
                ("1e60",  1.0e60, 4096),
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

        // --gpurefprobe: Wave 2.12 — compare CPU vs GPU reference orbit.
        // Runs three implementations at QD-tier coord + zoom:
        //   * CPU-QD     — full QD reference (truth for the perturbation path).
        //   * CPU-Hi     — plain-double Mandelbrot iteration on the QD's Hi
        //                  limb only. Matches what the first-cut GPU kernel
        //                  computes; used as the bit-exact parity target.
        //   * GPU-Hi     — first-cut MandelbrotRefOrbitGpu kernel (Hi-only).
        // Parity Δ vs CPU-QD diverges by chaos amplification at deep iter
        // counts (expected for Hi-only); parity Δ vs CPU-Hi should be at
        // FP64 round-off (validates the GPU kernel is functionally
        // equivalent to the CPU Hi-only path). QD-upgrade of the kernel is
        // the next Wave 2.12 slice.
        if (args.Length > 0 && args[0] == "--gpurefprobe")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("GPU ref-orbit probe — Wave 2.12 (D-6.27)");
            sb.AppendLine("  CPU-QD = QD-precision truth; CPU-Hi & GPU-Hi = plain-double comparison");
            (string label, double cxX0, double cxX1, double cyX0, double cyX1, int iter)[] cases =
            {
                ("1e15 saprobe", -1.1726999042772253, 8.9529605787776783E-17,
                                 -0.2968356710071185, -2.3536240906562374E-18, 4096),
                ("1e30 saprobe", -1.1726999042772253, 8.9529605787776783E-17,
                                 -0.2968356710071185, -2.3536240906562374E-18, 8192),
            };
            using var gpu = new FracturingFog.Calculators.Gpu.MandelbrotRefOrbitGpu();
            foreach (var c in cases)
            {
                var cx = new FracturingFog.FFMath.QD(c.cxX0, c.cxX1, 0, 0);
                var cy = new FracturingFog.FFMath.QD(c.cyX0, c.cyX1, 0, 0);
                int maxIter = c.iter;
                int slots = maxIter + 1;
                // CPU baseline (mirror of MandelbrotCalculator.ComputeReferenceOrbitQD).
                double[] cZrX0 = new double[slots], cZrX1 = new double[slots];
                double[] cZrX2 = new double[slots], cZrX3 = new double[slots];
                double[] cZiX0 = new double[slots], cZiX1 = new double[slots];
                double[] cZiX2 = new double[slots], cZiX3 = new double[slots];
                var swCpu = System.Diagnostics.Stopwatch.StartNew();
                int cpuN = 0;
                {
                    var zr = FracturingFog.FFMath.QD.Zero;
                    var zi = FracturingFog.FFMath.QD.Zero;
                    int n;
                    for (n = 0; n < maxIter; n++)
                    {
                        cZrX0[n] = zr.X0; cZrX1[n] = zr.X1; cZrX2[n] = zr.X2; cZrX3[n] = zr.X3;
                        cZiX0[n] = zi.X0; cZiX1[n] = zi.X1; cZiX2[n] = zi.X2; cZiX3[n] = zi.X3;
                        if (zr.X0 * zr.X0 + zi.X0 * zi.X0 >= 512.0 * 512.0) break;
                        var newZi = (zr * zi) * 2.0 + cy;
                        zr = zr.Square() - zi.Square() + cx;
                        zi = newZi;
                    }
                    cZrX0[n] = zr.X0; cZrX1[n] = zr.X1; cZrX2[n] = zr.X2; cZrX3[n] = zr.X3;
                    cZiX0[n] = zi.X0; cZiX1[n] = zi.X1; cZiX2[n] = zi.X2; cZiX3[n] = zi.X3;
                    cpuN = n;
                }
                swCpu.Stop();

                // CPU Hi-only baseline — matches GPU kernel's current math.
                double[] hZrX0 = new double[slots], hZiX0 = new double[slots];
                var swCpuHi = System.Diagnostics.Stopwatch.StartNew();
                int cpuHiN = 0;
                {
                    double zr = 0, zi = 0;
                    double cxh = cx.X0, cyh = cy.X0;
                    int n;
                    for (n = 0; n < maxIter; n++)
                    {
                        hZrX0[n] = zr; hZiX0[n] = zi;
                        if (zr * zr + zi * zi >= 512.0 * 512.0) break;
                        double nzi = 2.0 * zr * zi + cyh;
                        double nzr = zr * zr - zi * zi + cxh;
                        zr = nzr; zi = nzi;
                    }
                    hZrX0[n] = zr; hZiX0[n] = zi;
                    cpuHiN = n;
                }
                swCpuHi.Stop();

                // GPU path.
                double[] gZrX0 = new double[slots], gZrX1 = new double[slots];
                double[] gZrX2 = new double[slots], gZrX3 = new double[slots];
                double[] gZiX0 = new double[slots], gZiX1 = new double[slots];
                double[] gZiX2 = new double[slots], gZiX3 = new double[slots];
                var swGpu = System.Diagnostics.Stopwatch.StartNew();
                bool ok = gpu.Compute(cx.X0, cx.X1, cx.X2, cx.X3,
                                       cy.X0, cy.X1, cy.X2, cy.X3,
                                       maxIter, 512.0 * 512.0,
                                       gZrX0, gZrX1, gZrX2, gZrX3,
                                       gZiX0, gZiX1, gZiX2, gZiX3,
                                       out int gpuN, out _);
                swGpu.Stop();
                if (!ok)
                {
                    sb.AppendLine($"  {c.label,-14} CPU n={cpuN} ms={swCpu.Elapsed.TotalMilliseconds:F2}  GPU FAILED: {gpu.LastError}");
                    continue;
                }
                // Two parity comparisons:
                //   * vs CPU-Hi — should be bit-exact (FP64 round-off only).
                //     If non-zero, the GPU kernel has drifted from the CPU
                //     Hi-only math; investigate.
                //   * vs CPU-QD — chaos-amplified divergence at deep iter,
                //     expected to be O(magnitude of Z). Reported for context;
                //     not a failure indicator until the QD kernel lands.
                int parityN = Math.Min(cpuHiN, gpuN);
                int[] checkIters = { 0, 100, 1000, Math.Min(5000, parityN), parityN };
                double maxHiDelta = 0;
                double maxQdDelta = 0;
                foreach (int k in checkIters)
                {
                    if (k > parityN) continue;
                    double hi = Math.Max(Math.Abs(hZrX0[k] - gZrX0[k]),
                                         Math.Abs(hZiX0[k] - gZiX0[k]));
                    if (hi > maxHiDelta) maxHiDelta = hi;
                    double qd = Math.Max(Math.Abs(cZrX0[k] - gZrX0[k]),
                                         Math.Abs(cZiX0[k] - gZiX0[k]));
                    if (qd > maxQdDelta) maxQdDelta = qd;
                }
                sb.AppendLine($"  {c.label,-14} CPU-QD n={cpuN,5} ms={swCpu.Elapsed.TotalMilliseconds,7:F2}  CPU-Hi n={cpuHiN,5} ms={swCpuHi.Elapsed.TotalMilliseconds,7:F2}  GPU-Hi n={gpuN,5} ms={swGpu.Elapsed.TotalMilliseconds,7:F2}  Δ(GPU-Hi vs CPU-Hi)={maxHiDelta:E2}  Δ(GPU-Hi vs CPU-QD)={maxQdDelta:E2}  dev=[{gpu.SelectedDeviceLabel}]");
            }
            string gprPath = System.IO.Path.Combine(AppContext.BaseDirectory, "gpurefprobe.out");
            System.IO.File.WriteAllText(gprPath, sb.ToString());
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

        // D-2b — cluster master / worker entry points.
        if (args.Length > 0 && args[0] == "--master")
            return FracturingFog.ServerHost.ClusterEntry.RunMaster(args);
        if (args.Length > 0 && args[0] == "--worker")
            return FracturingFog.ServerHost.ClusterEntry.RunWorker(args);
        if (args.Length > 0 && args[0] == "--cluster-parity")
            return FracturingFog.ServerHost.ClusterParitySelfTest.Run(args);
        // D-3b / D-4d — scale harness: in-process N-worker render to
        // measure walltime + speedup vs the single-worker baseline.
        // --mode image|video selects the workload.
        if (args.Length > 0 && args[0] == "--cluster-scale")
            return FracturingFog.ServerHost.ClusterScaleSelfTest.Run(args);
        // D-4d — video parity self-test: render a short zoom two ways and
        // assert per-frame PNG SHA-256 + ffprobe stream parity + framemd5
        // identity on the encoded artifact.
        if (args.Length > 0 && args[0] == "--cluster-video-parity")
            return FracturingFog.ServerHost.ClusterVideoParitySelfTest.Run(args);

        // Phase X.4 / Slice 4.1 — --renderer override. Default is RendererBackend.Auto
        // (DX on Win, Silk on Linux/macOS, picked by RendererFactory.Create
        // from the surface kind). Explicit values let the user parity-test
        // Silk or Skia on a Windows host or downgrade from DX12 to Silk when
        // the discrete GPU is busy:
        //   --renderer dx     → force DX (Win only).
        //   --renderer silk   → force Silk.NET OpenGL.
        //   --renderer skia   → force SkiaSharp CPU.
        //   --renderer auto   → default; same as omitting the flag.
        // The selection is set on the RendererFactory before either shell
        // boots so OnSurfaceReady picks it up the first time a surface
        // arrives.
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

        // --winforms forces the legacy WinForms shell. Default path is the
        // Avalonia shell. WinForms is DEPRECATED — see CLAUDE.md. New UI
        // work must land in UI.Avalonia/, not MainForm.cs.
        bool forceWinForms = false;
        foreach (var a in args)
            if (string.Equals(a, "--winforms", StringComparison.OrdinalIgnoreCase))
                { forceWinForms = true; break; }

        if (!forceWinForms)
        {
            // S-X1 carve (2026-06-23) — wire Windows-only services onto the
            // cross-platform AvaloniaShellBootstrap before the shell boots.
            // FracturingFog.App skips this install on Linux/macOS so the hooks
            // stay null and the bootstrap takes its cross-plat code paths.
            FracturingFog.Win.WindowsBootstrap.Install();
            FracturingFog.Hosting.BootstrapHooks.ColorSampleBridge =
                new WinExeColorSampleBridge();
            FracturingFog.Hosting.BootstrapHooks.SyncDialogs =
                new WinFormsSyncDialogs();
            return FracturingFog.UI.Avalonia.AvaloniaShell.Run(
                args,
                FracturingFog.Hosting.AvaloniaShellBootstrap.OnSurfaceReady);
        }

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
