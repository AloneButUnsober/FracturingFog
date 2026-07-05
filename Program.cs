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
        //                  limb only. Kept as the pre-QD-kernel reference so
        //                  the report shows how far Hi-only drifts.
        //   * GPU-QD     — MandelbrotRefOrbitGpu kernel, now full quad-double.
        // Since the kernel iterates in QD, parity Δ(GPU-QD vs CPU-QD) should
        // now sit at QD round-off (validates the GPU QD chain matches the CPU
        // QD truth), while Δ(GPU-QD vs CPU-Hi) diverges by chaos amplification
        // at deep iter counts (expected — Hi-only loses precision).
        if (args.Length > 0 && args[0] == "--gpurefprobe")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("GPU ref-orbit probe — Wave 2.12 (D-6.27)");
            sb.AppendLine("  CPU-QD = QD-precision truth; GPU-QD = GPU quad-double kernel; CPU-Hi = plain-double reference");
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
                //   * vs CPU-QD — the primary check now the kernel iterates in
                //     QD. Both sides run the same QD algorithm, so the Hi limb
                //     should agree to QD round-off (~1e-11 abs at these
                //     magnitudes). A large delta means the GPU QD chain drifted
                //     from the CPU QD truth; investigate.
                //   * vs CPU-Hi — reported for context. Diverges by chaos
                //     amplification at deep iter (Hi-only loses precision);
                //     expected, not a failure.
                int parityQd = Math.Min(cpuN, gpuN);
                int parityHi = Math.Min(cpuHiN, gpuN);
                int[] checkQd = { 0, 100, 1000, Math.Min(5000, parityQd), parityQd };
                int[] checkHi = { 0, 100, 1000, Math.Min(5000, parityHi), parityHi };
                double maxQdDelta = 0;
                foreach (int k in checkQd)
                {
                    if (k < 0 || k > parityQd) continue;
                    double qd = Math.Max(Math.Abs(cZrX0[k] - gZrX0[k]),
                                         Math.Abs(cZiX0[k] - gZiX0[k]));
                    if (qd > maxQdDelta) maxQdDelta = qd;
                }
                double maxHiDelta = 0;
                foreach (int k in checkHi)
                {
                    if (k < 0 || k > parityHi) continue;
                    double hi = Math.Max(Math.Abs(hZrX0[k] - gZrX0[k]),
                                         Math.Abs(hZiX0[k] - gZiX0[k]));
                    if (hi > maxHiDelta) maxHiDelta = hi;
                }
                string verdict = maxQdDelta < 1e-6 ? "PASS" : "CHECK";
                sb.AppendLine($"  {c.label,-14} CPU-QD n={cpuN,5} ms={swCpu.Elapsed.TotalMilliseconds,7:F2}  CPU-Hi n={cpuHiN,5} ms={swCpuHi.Elapsed.TotalMilliseconds,7:F2}  GPU-QD n={gpuN,5} ms={swGpu.Elapsed.TotalMilliseconds,7:F2}  Δ(GPU-QD vs CPU-QD)={maxQdDelta:E2} [{verdict}]  Δ(GPU-QD vs CPU-Hi)={maxHiDelta:E2}  dev=[{gpu.SelectedDeviceLabel}]");
            }
            string gprPath = System.IO.Path.Combine(AppContext.BaseDirectory, "gpurefprobe.out");
            System.IO.File.WriteAllText(gprPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --kifsprobe: Wave 5.9.f1 — headless geometric check on the KIFS folds.
        // Sphere-traces the DE inward from radius 6 along a Fibonacci set of
        // directions and records the surface radius R(dir) per direction, plus
        // R along the +X axis, a face-diagonal (1,1,0) and the body-diagonal
        // (1,1,1). Detects the two documented failure modes without a GUI:
        //   * all-black  → hitFrac ≈ 0 (DE never gets small; nothing to render)
        //   * cube       → R is near-constant / larger along diagonals than axes
        // Menger + Sierpinski are printed as known-good baselines to compare the
        // three fixed folds (Octahedron / Dodecahedron / MandelboxRot) against.
        if (args.Length > 0 && args[0] == "--kifsprobe")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("KIFS geometric probe — Wave 5.9.f1");
            sb.AppendLine("  hitFrac = fraction of directions that reach a surface;");
            sb.AppendLine("  Rmin/Rmean/Rmax over the sphere; Raxis/Rface/Rbody = radius along +X / (1,1,0) / (1,1,1).");

            const double eps = 1e-4;
            const int iter = 12;
            const double ox = 1.0, oy = 1.0, oz = 1.0;

            double SurfaceRadius(FracturingFog.Models.KifsFoldKind fold, double scale,
                double dx, double dy, double dz)
            {
                // Normalize the direction, sphere-trace from 6·dir toward origin.
                double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dl < 1e-12) return -1;
                dx /= dl; dy /= dl; dz /= dl;
                double px = 6.0 * dx, py = 6.0 * dy, pz = 6.0 * dz;
                double t = 6.0;
                for (int step = 0; step < 512; step++)
                {
                    double de = FracturingFog.KifsCalculator.ProbeDE(fold, px, py, pz, scale, ox, oy, oz, iter);
                    if (de < eps) return Math.Sqrt(px * px + py * py + pz * pz);
                    t -= de;
                    if (t <= 0) return -1;               // marched past the origin, miss
                    px -= dx * de; py -= dy * de; pz -= dz * de;
                }
                return -1;
            }

            (string name, FracturingFog.Models.KifsFoldKind fold, double scale)[] cases =
            {
                ("Menger",       FracturingFog.Models.KifsFoldKind.Menger,       3.0),
                ("Sierpinski",   FracturingFog.Models.KifsFoldKind.Sierpinski,   2.0),
                ("Octahedron",   FracturingFog.Models.KifsFoldKind.Octahedron,   2.0),
                ("Dodecahedron", FracturingFog.Models.KifsFoldKind.Dodecahedron, 2.0),
                ("MandelboxRot", FracturingFog.Models.KifsFoldKind.MandelboxRot, 2.0),
            };

            const int N = 512;
            foreach (var c in cases)
            {
                int hits = 0;
                double rMin = double.MaxValue, rMax = 0, rSum = 0;
                for (int i = 0; i < N; i++)
                {
                    // Fibonacci sphere direction.
                    double phi = Math.Acos(1.0 - 2.0 * (i + 0.5) / N);
                    double theta = Math.PI * (1.0 + Math.Sqrt(5.0)) * i;
                    double dx = Math.Sin(phi) * Math.Cos(theta);
                    double dy = Math.Sin(phi) * Math.Sin(theta);
                    double dz = Math.Cos(phi);
                    double r = SurfaceRadius(c.fold, c.scale, dx, dy, dz);
                    if (r > 0) { hits++; rSum += r; if (r < rMin) rMin = r; if (r > rMax) rMax = r; }
                }
                double hitFrac = (double)hits / N;
                double rMean = hits > 0 ? rSum / hits : 0;
                if (hits == 0) rMin = 0;
                double rAxis = SurfaceRadius(c.fold, c.scale, 1, 0, 0);
                double rFace = SurfaceRadius(c.fold, c.scale, 1, 1, 0);
                double rBody = SurfaceRadius(c.fold, c.scale, 1, 1, 1);
                sb.AppendLine($"  {c.name,-13} hitFrac={hitFrac,5:F2}  Rmin={rMin,6:F3} Rmean={rMean,6:F3} Rmax={rMax,6:F3}  Raxis={rAxis,6:F3} Rface={rFace,6:F3} Rbody={rBody,6:F3}");
            }

            string kpPath = System.IO.Path.Combine(AppContext.BaseDirectory, "kifsprobe.out");
            System.IO.File.WriteAllText(kpPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --reforbitrecycle: Wave 3.5 — verify reference-orbit recycling across
        // frames reproduces a fresh render. For each DD-tier viewpoint the
        // target centre C1 is rendered twice:
        //   * truth   — a fresh orbit built AT C1 (recycling off).
        //   * recycle — build the orbit at a nearby C0, then move to C1 with
        //               recycling ON so the C0 orbit is reused with a Δc shift.
        // The recycled frame must reproduce the fresh one everywhere except a
        // negligible fraction of escape-boundary pixels, which flip iteration
        // count under ANY change to the reference (the reference rounds dc
        // differently), so bit-reproduction is not attainable and not the
        // metric. What a Δc-injection or validity-gate BUG looks like is a
        // large-area shift (whole image dc-offset), caught by the significant-
        // divergence fraction below. RefRecycleHits must be ≥ 1 (the recycle
        // path actually engaged) or the test proved nothing.
        if (args.Length > 0 && args[0] == "--reforbitrecycle")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Reference-orbit recycle probe — Wave 3.5");
            sb.AppendLine("  truth = fresh orbit at C1; recycle = C0 orbit reused for C1 with Δc shift.");
            sb.AppendLine("  miss = any Δiter≠0; big = Δiter>3 (boundary flips excluded).");
            sb.AppendLine("  PASS = recycle engaged AND significant-divergence (big) fraction < 0.1%.");

            const int W = 128, H = 128, panPx = 6;
            const double baseCx = -1.1726999042772253, baseCxLo = 8.9529605787776783E-17;
            const double baseCy = -0.2968356710071185, baseCyLo = -2.3536240906562374E-18;
            (string label, double zoom, int iter)[] cases =
            {
                ("1e13", 1.0e13, 3000),
                ("1e15", 1.0e15, 4000),
                ("1e18", 1.0e18, 5000),
                ("1e22", 1.0e22, 6000),
            };

            bool allPass = true;
            foreach (var c in cases)
            {
                double scale = (3.5 / Math.Max(W, H)) / c.zoom;
                double panWorld = panPx * scale;
                var c1x = new FracturingFog.FFMath.DD(baseCx, baseCxLo)
                        + new FracturingFog.FFMath.DD(panWorld, 0);

                // Truth: fresh orbit at C1 (recycle off).
                FracturingFog.MandelbrotCalculator.AllowRefOrbitRecycle = false;
                var truth = new FracturingFog.MandelbrotCalculator(W, H)
                {
                    CenterX = c1x.Hi, CenterXLo = c1x.Lo,
                    CenterY = baseCy, CenterYLo = baseCyLo,
                    Zoom = c.zoom, MaxIterations = c.iter,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };
                truth.Calculate();
                int[] truthIter = (int[])truth.IterationBuffer.Clone();

                // Recycle: build the orbit at C0, then move to C1 with recycling
                // on so the C0 orbit is reused.
                var recyc = new FracturingFog.MandelbrotCalculator(W, H)
                {
                    CenterX = baseCx, CenterXLo = baseCxLo,
                    CenterY = baseCy, CenterYLo = baseCyLo,
                    Zoom = c.zoom, MaxIterations = c.iter,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };
                FracturingFog.MandelbrotCalculator.AllowRefOrbitRecycle = false;
                recyc.Calculate();                          // seed orbit + BLA at C0
                FracturingFog.MandelbrotCalculator.AllowRefOrbitRecycle = true;
                recyc.CenterX = c1x.Hi; recyc.CenterXLo = c1x.Lo;
                recyc.Calculate();                          // should recycle to C1
                FracturingFog.MandelbrotCalculator.AllowRefOrbitRecycle = false;
                int[] recIter = recyc.IterationBuffer;

                long hits = recyc.RefRecycleHits;
                int mismatch = 0, bigMiss = 0, maxDelta = 0;
                for (int i = 0; i < truthIter.Length; i++)
                {
                    int d = Math.Abs(truthIter[i] - recIter[i]);
                    if (d != 0) { mismatch++; if (d > maxDelta) maxDelta = d; if (d > 3) bigMiss++; }
                }
                double mmFrac = (double)mismatch / truthIter.Length;
                double bigFrac = (double)bigMiss / truthIter.Length;
                bool pass = hits >= 1 && bigFrac < 0.001;
                allPass &= pass;
                sb.AppendLine($"  zoom={c.label,-6} iter={c.iter,5} hits={hits} miss={mismatch,6} ({mmFrac,7:P2}) big={bigMiss,4} ({bigFrac,7:P2}) maxΔ={maxDelta,4} [{(pass ? "PASS" : "FAIL")}]");
            }

            sb.AppendLine(allPass ? "RESULT: PASS" : "RESULT: FAIL");
            string rrPath = System.IO.Path.Combine(AppContext.BaseDirectory, "reforbitrecycle.out");
            System.IO.File.WriteAllText(rrPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return allPass ? 0 : 1;
        }

        // --regionprobe: headless render of the deep smoke-test regions to
        // diagnose the "renders solid colour / takes forever" reports without a
        // GUI. Renders each at 128² single-sample and reports the precision tier,
        // wall-clock, in-set fraction, and — the solid-colour tell — how many
        // DISTINCT iteration counts / colours the image actually contains. A
        // healthy fractal has hundreds; SOLID = ≤ 2 distinct colours. Optional
        // arg [maxIter] overrides the default 8192 cap.
        if (args.Length > 0 && args[0] == "--regionprobe")
        {
            int maxIter = 8192;
            if (args.Length > 1 && int.TryParse(args[1], out int mi) && mi > 0) maxIter = mi;
            const int W = 128, H = 128;
            const double QDt = 1e25, ODt = 1e50;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Region probe — {W}×{H} single-sample, maxIter={maxIter}");
            sb.AppendLine("  tier by zoom (DD ≤1e25 < QD ≤1e50 < OD). distIter/distColor = unique values;");
            sb.AppendLine("  SOLID verdict when distColor ≤ 2. inSet% = pixels that never escaped.");

            // name, CX limbs (hi,lo,x2,x3), CY limbs, zoom. From user smoke report.
            (string name, double[] cx, double[] cy, double zoom)[] regions =
            {
                ("3E47 Test",
                    new[] { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 },
                    new[] { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 },
                    3E+47),
                ("E45Test04",
                    new[] { 0.40679612541749072, 1.0460588279145483E-17, -4.3674629952735269E-35, 5.0770999219861446E-50 },
                    new[] { -0.56778808906247447, -4.0266051197805093E-17, 1.5194922328871422E-33, 5.0770999219861446E-50 },
                    1.07808E+47),
                ("Deeper and Deeper",
                    new[] { -1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49 },
                    new[] { -5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55 },
                    4.49845E+46),
                ("Deep Lightning in Space",
                    new[] { -1.4181949444785762, -7.4415882477902279E-17, 0.0, 0.0 },
                    new[] { -0.12700786443815276, -2.3429499355532375E-18, 0.0, 0.0 },
                    7.58348E+26),
            };

            foreach (var r in regions)
            {
                string tier = r.zoom > ODt ? "OD" : (r.zoom > QDt ? "QD" : "DD");
                var calc = new FracturingFog.MandelbrotCalculator(W, H)
                {
                    CenterX = r.cx[0], CenterXLo = r.cx[1], CenterX2 = r.cx[2], CenterX3 = r.cx[3],
                    CenterY = r.cy[0], CenterYLo = r.cy[1], CenterY2 = r.cy[2], CenterY3 = r.cy[3],
                    Zoom = r.zoom, MaxIterations = maxIter,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                calc.Calculate();
                sw.Stop();

                var distIter = new System.Collections.Generic.HashSet<int>();
                var distColor = new System.Collections.Generic.HashSet<uint>();
                int inSet = 0, minIt = int.MaxValue, maxIt = 0;
                for (int i = 0; i < calc.IterationBuffer.Length; i++)
                {
                    int it = calc.IterationBuffer[i];
                    distIter.Add(it);
                    if (it >= maxIter) inSet++;
                    else { if (it < minIt) minIt = it; if (it > maxIt) maxIt = it; }
                }
                foreach (var p in calc.ColorBuffer) distColor.Add(p);
                if (minIt == int.MaxValue) minIt = 0;
                double inSetPct = 100.0 * inSet / calc.IterationBuffer.Length;
                string verdict = distColor.Count <= 2 ? "SOLID" : "ok";

                sb.AppendLine(
                    $"  {r.name,-24} {tier} zoom={r.zoom,10:G4} HP={(calc.IsHighPrecisionActive ? "Y" : "N")} " +
                    $"ms={sw.Elapsed.TotalMilliseconds,9:F1} distIter={distIter.Count,5} distColor={distColor.Count,5} " +
                    $"inSet={inSetPct,5:F1}% escIt=[{minIt},{maxIt}] [{verdict}]");
            }

            string rpPath = System.IO.Path.Combine(AppContext.BaseDirectory, "regionprobe.out");
            System.IO.File.WriteAllText(rpPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --qdfloorprobe: Wave 2.14 diagnostic — localise the source of deep-QD
        // pixelation (zoom 1e40–1e58) BEFORE committing to the documented
        // "DD-precision PT δ chain" rewrite. Finding that predates the plan: the
        // SIMD PT δ-loop bails on iteration ~1 past ~1e30 (δ absorbed by Z in
        // double, glitch check returns false), so deep frames are actually
        // rendered by the per-pixel direct-QD ComputePixelQD, whose SA prelude
        // seeds δ in *double* (dcR = dc.X0, EvalDelta in double). This probe
        // renders each QD-band region twice — SA on vs SA off (direct QD from
        // iter 0) — and reports a neighbour-collapse metric (fraction of
        // horizontally/vertically adjacent escaped pixels with IDENTICAL iter,
        // the pixelation tell). If SA-off collapses far less → the double SA
        // seed is the culprit (cheap fix: QD/DD SA eval). If both collapse the
        // same → the direct-QD arithmetic floor itself, needing the OD/DD-δ work.
        if (args.Length > 0 && args[0] == "--qdfloorprobe")
        {
            int maxIter = 20000;
            if (args.Length > 1 && int.TryParse(args[1], out int mi) && mi > 0) maxIter = mi;
            const int W = 128, H = 128;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"QD-floor probe — {W}×{H} single-sample, maxIter={maxIter}");
            sb.AppendLine("  Each region rendered SA-on then SA-off (direct QD from iter 0).");
            sb.AppendLine("  collapse% = adjacent escaped pixels (H+V) with identical iter — pixelation tell.");
            sb.AppendLine("  Big drop on SA-off ⇒ double SA seed is the floor. Same ⇒ direct-QD arithmetic floor.");

            (string name, double[] cx, double[] cy, double zoom)[] regions =
            {
                ("3E47 Test",
                    new[] { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 },
                    new[] { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 },
                    3E+47),
                ("E45Test04",
                    new[] { 0.40679612541749072, 1.0460588279145483E-17, -4.3674629952735269E-35, 5.0770999219861446E-50 },
                    new[] { -0.56778808906247447, -4.0266051197805093E-17, 1.5194922328871422E-33, 5.0770999219861446E-50 },
                    1.07808E+47),
                ("Deeper and Deeper",
                    new[] { -1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49 },
                    new[] { -5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55 },
                    4.49845E+46),
            };

            // collapse% over escaped pixels: fraction of H+V neighbour pairs
            // (both escaped) whose iteration counts are bit-equal.
            static double CollapsePct(int[] it, int w, int h, int cap)
            {
                long pairs = 0, equal = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (it[i] >= cap) continue;         // in-set — skip
                        if (x + 1 < w && it[i + 1] < cap)
                        { pairs++; if (it[i] == it[i + 1]) equal++; }
                        if (y + 1 < h && it[i + w] < cap)
                        { pairs++; if (it[i] == it[i + w]) equal++; }
                    }
                return pairs == 0 ? 0.0 : 100.0 * equal / pairs;
            }

            foreach (var r in regions)
            {
                foreach (bool saOff in new[] { false, true })
                {
                    var calc = new FracturingFog.MandelbrotCalculator(W, H)
                    {
                        CenterX = r.cx[0], CenterXLo = r.cx[1], CenterX2 = r.cx[2], CenterX3 = r.cx[3],
                        CenterY = r.cy[0], CenterYLo = r.cy[1], CenterY2 = r.cy[2], CenterY3 = r.cy[3],
                        Zoom = r.zoom, MaxIterations = maxIter,
                        Quality = FracturingFog.Models.QualityPreset.Extreme,
                        ColorMap = new FracturingFog.Models.HsvPalette(),
                        DisableSeriesApproximation = saOff,
                    };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    calc.Calculate();
                    sw.Stop();

                    var distIter = new System.Collections.Generic.HashSet<int>();
                    int inSet = 0;
                    for (int i = 0; i < calc.IterationBuffer.Length; i++)
                    {
                        distIter.Add(calc.IterationBuffer[i]);
                        if (calc.IterationBuffer[i] >= maxIter) inSet++;
                    }
                    double inSetPct = 100.0 * inSet / calc.IterationBuffer.Length;
                    double collapse = CollapsePct(calc.IterationBuffer, W, H, maxIter);

                    sb.AppendLine(
                        $"  {r.name,-20} SA={(saOff ? "off" : "on ")} zoom={r.zoom,10:G4} " +
                        $"ms={sw.Elapsed.TotalMilliseconds,8:F1} distIter={distIter.Count,5} " +
                        $"inSet={inSetPct,5:F1}% collapse={collapse,5:F1}%");
                }
            }

            string qfPath = System.IO.Path.Combine(AppContext.BaseDirectory, "qdfloorprobe.out");
            System.IO.File.WriteAllText(qfPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --qdfloorsweep: Wave 2.14 — locate the QD (and OD) coordinate-
        // separation floor DIRECTLY, no rendering. For a fixed deep centre and a
        // sweep of zoom levels, build the 128 per-pixel X coordinates the render
        // would use (QD.FromCenterOffset, same call the QD path makes) and count
        // how many are bit-distinct. distinctQD < 128 ⇒ QD arithmetic can no
        // longer separate adjacent pixels at that zoom = the true pixelation
        // floor. distinctOD shows how far OD extends it. This settles whether the
        // plan's "pixelation at 1e40–1e58" is a QD-arithmetic floor in that band
        // (⇒ real 2.14 work) or a mis-attribution (⇒ close it).
        if (args.Length > 0 && args[0] == "--qdfloorsweep")
        {
            const int W = 128;
            // 3E47 Test centre — |c| ≈ 2, 4 QD limbs down to ~1e-51.
            var cxQ = new FracturingFog.FFMath.QD(
                -1.9918151296901943, -7.8219844803880472E-17,
                 1.660139930392911E-34, 8.217274172159319E-51);
            var cxO = new FracturingFog.FFMath.OD(
                -1.9918151296901943, -7.8219844803880472E-17,
                 1.660139930392911E-34, 8.217274172159319E-51, 0, 0, 0, 0);

            double[] zooms =
            {
                1e40, 1e45, 1e48, 1e50, 1e52, 1e54, 1e56, 1e58,
                1e60, 1e62, 1e64, 1e66, 1e70, 1e80, 1e100, 1e120,
            };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"QD/OD coordinate-separation sweep — {W} pixels wide, 3E47 centre (|c|≈2).");
            sb.AppendLine("  distinctQD/OD = bit-distinct per-pixel X coords out of 128. <128 ⇒ pixels collapse.");

            foreach (double zoom in zooms)
            {
                double scale = (3.5 / W) / zoom;

                var qset = new System.Collections.Generic.HashSet<(double, double, double, double)>();
                var oset = new System.Collections.Generic.HashSet<(double, double, double, double, double, double, double, double)>();
                for (int i = 0; i < W; i++)
                {
                    double off = i - W * 0.5;
                    var q = FracturingFog.FFMath.QD.FromCenterOffset(cxQ, off, scale);
                    var o = FracturingFog.FFMath.OD.FromCenterOffset(cxO, off, scale);
                    qset.Add((q.X0, q.X1, q.X2, q.X3));
                    oset.Add((o.X0, o.X1, o.X2, o.X3, o.X4, o.X5, o.X6, o.X7));
                }

                string flag = qset.Count < W ? "  <-- QD FLOOR" : (oset.Count < W ? "  (OD floor)" : "");
                sb.AppendLine(
                    $"  zoom={zoom,8:G3} scale={scale,10:E3} distinctQD={qset.Count,4}/{W} " +
                    $"distinctOD={oset.Count,4}/{W}{flag}");
            }

            string qsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "qdfloorsweep.out");
            System.IO.File.WriteAllText(qsPath, sb.ToString());
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
