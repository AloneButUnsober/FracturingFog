// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

        // --colorprobe: golden gate for the colour pipeline (Phase A/B/C option
        // matrix). Unlike the diagnostic probes below, this RETURNS non-zero on
        // drift so CI can gate. Prerequisite for Phase D (F11 deband / F10 alpha),
        // which edit the float->byte quantise + LUT build. See ColorProbe.cs.
        //   --colorprobe          gate vs embedded golden (exit 1 on drift)
        //   --colorprobe regen    print freshly computed digest to pin
        //   --colorprobe verbose  gate + dump per-config table to stdout
        if (args.Length > 0 && args[0] == "--colorprobe")
        {
            return FracturingFog.Diagnostics.ColorProbe.Run(args);
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

        // --rebaseprobe: SM-2 — A/B the rebasing PT fallback against the
        // per-pixel QD/OD truth. Renders each deep region twice at identical
        // settings: AllowPtRebasing OFF (current QD/OD path = truth) then ON
        // (rebased double PT). Reports iteration-count parity (match% / maxΔ),
        // wall-clock for each, the resulting speedup, and how many pixels the
        // rebased fallback resolved. Gate PASS: match ≥ 99.9% AND rebasing is
        // faster (that is the whole point — fix SM-2 slowness without changing
        // the image).
        if (args.Length > 0 && args[0] == "--rebaseprobe")
        {
            int maxIter = 20000;
            if (args.Length > 1 && int.TryParse(args[1], out int mi) && mi > 0) maxIter = mi;
            const int W = 128, H = 128;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Rebase probe — {W}×{H} single-sample, maxIter={maxIter}");
            sb.AppendLine("  OFF = per-pixel QD/OD truth; ON = rebasing double PT. match% over all pixels.");

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

            int[] Render(bool rebasing, bool saOff,
                         (string name, double[] cx, double[] cy, double zoom) r,
                         out double ms, out long rebased)
            {
                FracturingFog.MandelbrotCalculator.AllowPtRebasing = rebasing;
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
                ms = sw.Elapsed.TotalMilliseconds;
                rebased = calc.PtRebasedPixels;
                return (int[])calc.IterationBuffer.Clone();
            }

            static (double pct, int maxD) Compare(int[] a, int[] b)
            {
                int match = 0, maxD = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    int d = Math.Abs(a[i] - b[i]);
                    if (d == 0) match++; else if (d > maxD) maxD = d;
                }
                return (100.0 * match / a.Length, maxD);
            }
            static int InSet(int[] a, int cap)
            {
                int n = 0; foreach (int v in a) if (v >= cap) n++; return n;
            }

            bool allPass = true;
            foreach (var r in regions)
            {
                // truthA = QD/OD, SA off; truthB = QD/OD, SA on; test = rebasing.
                int[] truthA = Render(false, true,  r, out double msA, out _);
                int[] truthB = Render(false, false, r, out double msB, out _);
                int[] test   = Render(true,  false, r, out double msOn, out long rebased);
                FracturingFog.MandelbrotCalculator.AllowPtRebasing = false;  // restore

                var (matchA, maxDA) = Compare(truthA, test);    // rebasing vs QD-SAoff
                var (qdSelf, qdMaxD) = Compare(truthA, truthB);  // QD self-consistency
                int isTruth = InSet(truthA, maxIter), isTest = InSet(test, maxIter);
                double speedup = msOn > 0 ? msA / msOn : 0;
                // Accept when rebasing tracks the QD render at least as well as
                // the QD render tracks itself (SA-off vs SA-on) — on chaos-
                // dominated deep regions there is no tighter truth — AND when it
                // is faster. The 1-point slack covers sampling noise.
                bool pass = matchA >= qdSelf - 1.0 && msOn < msA;
                allPass &= pass;

                sb.AppendLine(
                    $"  {r.name,-24} zoom={r.zoom,9:G3} reb-vs-QD={matchA,6:F2}%(maxΔ{maxDA,4}) " +
                    $"QDself={qdSelf,6:F2}%(maxΔ{qdMaxD,4}) inSet truth={isTruth,5} test={isTest,5} " +
                    $"msQD={msA,7:F0} msReb={msOn,6:F0} sp={speedup,5:F1}× [{(pass ? "PASS" : "FAIL")}]");
            }

            sb.AppendLine(allPass ? "RESULT: PASS" : "RESULT: FAIL");
            string rbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "rebaseprobe.out");
            System.IO.File.WriteAllText(rbPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return allPass ? 0 : 1;
        }

        // --inputprobe: locate where interactive pan/zoom precision breaks near
        // the QD→OD tier boundary (user report: controls lose precision / stop
        // working approaching ~9e49). Drives FractalInputController headlessly at
        // a sweep of zooms and checks the resulting centre lands where the
        // clicked/dragged pixel should map, measured in PIXELS of error (0 =
        // perfect anchoring). Isolates whether the fault is the tier cascade,
        // the double pixel·scale delta, or the QD precision floor.
        if (args.Length > 0 && args[0] == "--inputprobe")
        {
            const int W = 1000, H = 1000;
            // Deep centre with full QD limbs (3E47 region).
            double[] cx = { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 };
            double[] cy = { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 };
            double[] zooms = { 1e40, 1e46, 1e48, 9e49, 1.1e50, 1e52, 1e55, 1e58, 1e60, 1e62, 1e63, 1e64, 1e66, 1e70 };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Input probe — {W}×{H} client. Double-click + drag-pan anchor error in PIXELS.");
            sb.AppendLine("  tier: QD (1e25–1e50) / OD (>1e50). err ≈ 0 good; err ≥ 1 px = broken anchoring.");

            foreach (double zoom in zooms)
            {
                var vs = new FracturingFog.ViewState.FractalViewState
                {
                    FractalType = FracturingFog.FractalType.Mandelbrot,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    Zoom = zoom,
                    CenterX = cx[0], CenterXLo = cx[1], CenterX2 = cx[2], CenterX3 = cx[3],
                    CenterY = cy[0], CenterYLo = cy[1], CenterY2 = cy[2], CenterY3 = cy[3],
                };
                var ctl = new FracturingFog.Input.FractalInputController(vs);
                string tier = zoom > 1e50 ? "OD" : "QD";
                double scale = 3.5 / (Math.Max(W, H) * zoom);

                // Truth centre as OD (all 8 limbs).
                FracturingFog.FFMath.OD CenterOD(bool xAxis) => xAxis
                    ? new FracturingFog.FFMath.OD(vs.CenterX, vs.CenterXLo, vs.CenterX2, vs.CenterX3,
                                                  vs.CenterX4, vs.CenterX5, vs.CenterX6, vs.CenterX7)
                    : new FracturingFog.FFMath.OD(vs.CenterY, vs.CenterYLo, vs.CenterY2, vs.CenterY3,
                                                  vs.CenterY4, vs.CenterY5, vs.CenterY6, vs.CenterY7);

                var startCXod = CenterOD(true);
                var startCYod = CenterOD(false);

                // ── Double-click focus: click 200px right, 120px down of centre.
                int clickX = W / 2 + 200, clickY = H / 2 + 120;
                ctl.OnPointerDoubleClick(new FracturingFog.Input.PointerInput(
                    clickX, clickY, W, H,
                    FracturingFog.Input.PointerButton.Left,
                    FracturingFog.Input.InputModifiers.None));
                // Expected new centre = old centre + pixelOffset·scale (OD truth).
                var expDcX = startCXod + (200.0 * scale);
                var expDcY = startCYod + (120.0 * scale);
                double dcErrX = (double)(CenterOD(true) - expDcX) / scale;
                double dcErrY = (double)(CenterOD(false) - expDcY) / scale;
                double dcErr = Math.Sqrt(dcErrX * dcErrX + dcErrY * dcErrY);

                // reset centre for the pan test
                vs.CenterX = cx[0]; vs.CenterXLo = cx[1]; vs.CenterX2 = cx[2]; vs.CenterX3 = cx[3];
                vs.CenterX4 = vs.CenterX5 = vs.CenterX6 = vs.CenterX7 = 0;
                vs.CenterY = cy[0]; vs.CenterYLo = cy[1]; vs.CenterY2 = cy[2]; vs.CenterY3 = cy[3];
                vs.CenterY4 = vs.CenterY5 = vs.CenterY6 = vs.CenterY7 = 0;

                // ── Drag-pan: press at centre, move to (+200,+120). Centre should
                // move by -(dx)·scale so the grabbed point stays under cursor.
                ctl.OnPointerDown(new FracturingFog.Input.PointerInput(
                    W / 2, H / 2, W, H, FracturingFog.Input.PointerButton.Left,
                    FracturingFog.Input.InputModifiers.None));
                ctl.OnPointerMove(new FracturingFog.Input.PointerInput(
                    W / 2 + 200, H / 2 + 120, W, H, FracturingFog.Input.PointerButton.Left,
                    FracturingFog.Input.InputModifiers.None));
                var expPanX = startCXod + (-200.0 * scale);
                var expPanY = startCYod + (-120.0 * scale);
                double panErrX = (double)(CenterOD(true) - expPanX) / scale;
                double panErrY = (double)(CenterOD(false) - expPanY) / scale;
                double panErr = Math.Sqrt(panErrX * panErrX + panErrY * panErrY);

                sb.AppendLine(
                    $"  zoom={zoom,8:G3} {tier}  scale={scale,10:E3}  " +
                    $"dblclick-err={dcErr,10:E2}px  pan-err={panErr,10:E2}px");
            }

            // ── Cumulative wheel-zoom drift: zoom IN from 1e12 with the cursor
            // held off-centre, the way a user reaches deep zoom. Anchoring
            // promises the world point under the cursor stays put; measure how
            // far it drifts (in final-frame pixels) after N steps.
            sb.AppendLine("  --- cumulative wheel zoom-in, cursor at (+200,+120), anchor drift ---");
            {
                // Realistic path: start shallow with a double-only centre (no
                // low limbs), the way a user actually reaches deep zoom.
                var vs = new FracturingFog.ViewState.FractalViewState
                {
                    FractalType = FracturingFog.FractalType.Mandelbrot,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    Zoom = 1e6,
                    CenterX = -0.743643887037158704, CenterXLo = 0, CenterX2 = 0, CenterX3 = 0,
                    CenterY =  0.131825904205311970, CenterYLo = 0, CenterY2 = 0, CenterY3 = 0,
                };
                var ctl = new FracturingFog.Input.FractalInputController(vs);
                int curX = W / 2 + 200, curY = H / 2 + 120;

                FracturingFog.FFMath.OD ODx() => new(vs.CenterX, vs.CenterXLo, vs.CenterX2, vs.CenterX3,
                                                     vs.CenterX4, vs.CenterX5, vs.CenterX6, vs.CenterX7);
                FracturingFog.FFMath.OD ODy() => new(vs.CenterY, vs.CenterYLo, vs.CenterY2, vs.CenterY3,
                                                     vs.CenterY4, vs.CenterY5, vs.CenterY6, vs.CenterY7);
                // World point under the cursor BEFORE any zoom.
                double s0 = 3.5 / (Math.Max(W, H) * vs.Zoom);
                var worldX0 = ODx() + (curX - W * 0.5) * s0;
                var worldY0 = ODy() + (curY - H * 0.5) * s0;

                double prevZoom = vs.Zoom;
                int steps = 0;
                double maxDrift = 0;
                while (vs.Zoom < 1e70 && steps < 100000)
                {
                    ctl.OnWheel(new FracturingFog.Input.WheelInput(
                        curX, curY, W, H, +120, FracturingFog.Input.InputModifiers.None));
                    steps++;
                    if (vs.Zoom == prevZoom) break;   // clamped
                    prevZoom = vs.Zoom;

                    double s = 3.5 / (Math.Max(W, H) * vs.Zoom);
                    // Where the ORIGINAL world point now sits on screen.
                    double onScreenX = W * 0.5 + (double)(worldX0 - ODx()) / s;
                    double onScreenY = H * 0.5 + (double)(worldY0 - ODy()) / s;
                    double drift = Math.Sqrt(
                        (onScreenX - curX) * (onScreenX - curX) +
                        (onScreenY - curY) * (onScreenY - curY));
                    if (drift > maxDrift) maxDrift = drift;

                    if (steps == 1 || vs.Zoom > 1e70 * 0.999 || (steps % 80 == 0))
                        sb.AppendLine(
                            $"    step={steps,5} zoom={vs.Zoom,9:G3} anchor-drift={drift,10:E2}px");
                }
                sb.AppendLine($"  max anchor-drift over the whole zoom-in = {maxDrift:E2}px");
                sb.AppendLine(maxDrift < 0.5
                    ? "RESULT: PASS (anchor stable to <0.5px through 1e70)"
                    : "RESULT: FAIL (anchor drift exceeds 0.5px)");
            }

            string ipPath = System.IO.Path.Combine(AppContext.BaseDirectory, "inputprobe.out");
            System.IO.File.WriteAllText(ipPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --focusprobe: END-TO-END double-click-focus accuracy through the REAL
        // render. The --inputprobe checks only that the controller moves the
        // ViewState centre self-consistently (OD math vs OD truth) — it can't see
        // whether the RENDERED image agrees. This renders a deep frame, performs a
        // double-click focus via FractalInputController, re-renders at the new
        // centre, then patch-matches to find where the clicked feature actually
        // landed. err = |offset of the clicked patch from screen centre| in px;
        // 0 = focus is pixel-perfect. Reproduces the user report "double-click
        // misses / pan overshoots past ~1e63".
        if (args.Length > 0 && args[0] == "--focusprobe")
        {
            // Optional dim override: `--focusprobe 64` shrinks the viewport. If the
            // flat-collapse zoom rises when dim shrinks (bigger pixel scale), the
            // limit is the point's δ-amplification floor (scale-dependent), not a
            // fixed-zoom engine bug.
            int dim = 220;
            if (args.Length > 1 && int.TryParse(args[1], out int dimArg) && dimArg >= 16) dim = dimArg;
            int W = dim, H = dim;
            // User's real deep centre (5 printed limbs; X5..X7 = 0).
            double[] cx = { -1.9918151296901943, -7.8219844803880472E-17,
                             1.6601399303928428E-34, 5.9806621035236938E-51,
                             4.0825430733972371E-67, 0, 0, 0 };
            double[] cy = { -5.5240415753972429E-06, -2.8659813126937928E-22,
                             6.6910924089534E-39, -3.7336948285574332E-55,
                             1.3067965264006595E-71, 0, 0, 0 };
            double[] zooms = { 1e56, 1e58, 1e60, 1e62, 1e63, 1e64, 1e66, 1e70 };
            const int r = 3;                        // patch radius (7×7)
            const int search = 12;                  // ± match search window (px)
            // Click offset from centre — kept inside the viewport for small dims
            // (patch + search must not run off the edge).
            int clickDX = Math.Min(44, W / 2 - r - search - 1);
            int clickDY = Math.Min(26, H / 2 - r - search - 1);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Focus probe — {W}×{H}. Double-click at (+{clickDX},+{clickDY}) from centre; the");
            sb.AppendLine("clicked world point must land at screen centre after re-render.");
            sb.AppendLine("  err = best-match offset of clicked patch from centre (px). 0 = perfect focus.");

            // Reference-orbit escape depth for this centre (zoom-independent). The
            // perturbation δ is amplified by ~∏|2 Z_n| over the orbit; if the orbit
            // ESCAPES at iteration N the deepest resolvable zoom is ~that product.
            // A short orbit ⇒ a hard depth limit that is a property of the POINT,
            // not a precision bug.
            {
                var zc = FracturingFog.FFMath.OD.Zero;
                var zci = FracturingFog.FFMath.OD.Zero;
                var ccr = new FracturingFog.FFMath.OD(cx[0], cx[1], cx[2], cx[3], cx[4], cx[5], cx[6], cx[7]);
                var cci = new FracturingFog.FFMath.OD(cy[0], cy[1], cy[2], cy[3], cy[4], cy[5], cy[6], cy[7]);
                int escN = -1; double logDeriv = 0; // Σ log2|2 Zn| ⇒ zoom decades resolvable
                for (int n = 0; n < 200000; n++)
                {
                    double zr = zc.X0, zi = zci.X0;
                    double mag2 = zr * zr + zi * zi;
                    if (mag2 > 4.0) { escN = n; break; }
                    if (n > 0) { double d = 2.0 * Math.Sqrt(mag2); if (d > 0) logDeriv += Math.Log10(d); }
                    var nzr = zc.Square() - zci.Square() + ccr;
                    var nzi = (zc * zci) + (zc * zci) + cci;
                    zc = nzr; zci = nzi;
                }
                sb.AppendLine($"  ref-orbit: escapeIter={(escN < 0 ? ">=200000 (bounded)" : escN.ToString())}  " +
                              $"Σlog10|2Zn|≈{logDeriv:F1} decades of δ-amplification");
            }

            FracturingFog.MandelbrotCalculator MakeCalc(double zoom, int maxIter,
                double x0, double x1, double x2, double x3, double x4, double x5, double x6, double x7,
                double y0, double y1, double y2, double y3, double y4, double y5, double y6, double y7)
                => new FracturingFog.MandelbrotCalculator(W, H)
                {
                    CenterX = x0, CenterXLo = x1, CenterX2 = x2, CenterX3 = x3,
                    CenterX4 = x4, CenterX5 = x5, CenterX6 = x6, CenterX7 = x7,
                    CenterY = y0, CenterYLo = y1, CenterY2 = y2, CenterY3 = y3,
                    CenterY4 = y4, CenterY5 = y5, CenterY6 = y6, CenterY7 = y7,
                    Zoom = zoom, MaxIterations = maxIter,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };

            foreach (double zoom in zooms)
            {
                int maxIter = FracturingFog.Models.QualityPreset.Extreme.ComputeIterations(zoom);
                string tier = zoom > 1e50 ? "OD" : "QD";

                // Localize the collapse: normal (SA+BLA acceleration) vs
                // DisableAcceleration (raw perturbation δ-loop, no SA prelude / BLA
                // skip). If raw survives where accelerated collapses → SA/BLA is the
                // precision sink; if both collapse → the δ core / reference orbit.
                foreach (bool disAcc in new[] { false, true })
                {
                    var calcR = MakeCalc(zoom, maxIter,
                        cx[0], cx[1], cx[2], cx[3], cx[4], cx[5], cx[6], cx[7],
                        cy[0], cy[1], cy[2], cy[3], cy[4], cy[5], cy[6], cy[7]);
                    calcR.DisableAcceleration = disAcc;
                    calcR.Calculate();
                    var fd = new System.Collections.Generic.HashSet<int>();
                    foreach (var it in calcR.IterationBuffer) fd.Add(it);
                    sb.AppendLine($"      [accel={(disAcc ? "OFF" : "ON ")}] frameDistinct={fd.Count,5}");
                }

                // Frame A — centred on the deep coord.
                var calcA = MakeCalc(zoom, maxIter,
                    cx[0], cx[1], cx[2], cx[3], cx[4], cx[5], cx[6], cx[7],
                    cy[0], cy[1], cy[2], cy[3], cy[4], cy[5], cy[6], cy[7]);
                calcA.Calculate();
                int[] A = (int[])calcA.IterationBuffer.Clone();
                double calcMaxUseful = calcA.MaxUsefulZoomLog10;

                // Frame-A richness — detect a collapsed (near-solid) deep render.
                var frameDistinct = new System.Collections.Generic.HashSet<int>();
                int frameInSet = 0;
                for (int i = 0; i < A.Length; i++)
                { frameDistinct.Add(A[i]); if (A[i] >= maxIter) frameInSet++; }
                double frameInSetPct = 100.0 * frameInSet / A.Length;

                // Double-click focus at the clicked pixel through the real controller.
                var vs = new FracturingFog.ViewState.FractalViewState
                {
                    FractalType = FracturingFog.FractalType.Mandelbrot,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    Zoom = zoom,
                    CenterX = cx[0], CenterXLo = cx[1], CenterX2 = cx[2], CenterX3 = cx[3],
                    CenterX4 = cx[4], CenterX5 = cx[5], CenterX6 = cx[6], CenterX7 = cx[7],
                    CenterY = cy[0], CenterYLo = cy[1], CenterY2 = cy[2], CenterY3 = cy[3],
                    CenterY4 = cy[4], CenterY5 = cy[5], CenterY6 = cy[6], CenterY7 = cy[7],
                };
                var ctl = new FracturingFog.Input.FractalInputController(vs);
                ctl.OnPointerDoubleClick(new FracturingFog.Input.PointerInput(
                    W / 2 + clickDX, H / 2 + clickDY, W, H,
                    FracturingFog.Input.PointerButton.Left,
                    FracturingFog.Input.InputModifiers.None));

                // Frame B — centred on the focused (new) centre from the ViewState.
                var calcB = MakeCalc(zoom, maxIter,
                    vs.CenterX, vs.CenterXLo, vs.CenterX2, vs.CenterX3,
                    vs.CenterX4, vs.CenterX5, vs.CenterX6, vs.CenterX7,
                    vs.CenterY, vs.CenterYLo, vs.CenterY2, vs.CenterY3,
                    vs.CenterY4, vs.CenterY5, vs.CenterY6, vs.CenterY7);
                calcB.Calculate();
                int[] B = (int[])calcB.IterationBuffer.Clone();

                // Patch-match: the 7×7 patch around the clicked pixel in A should now
                // sit at the centre of B. Search a window for the min-SAD offset.
                int acx = W / 2 + clickDX, acy = H / 2 + clickDY;
                long bestSad = long.MaxValue; int bex = 0, bey = 0;
                for (int ey = -search; ey <= search; ey++)
                for (int ex = -search; ex <= search; ex++)
                {
                    long sad = 0;
                    for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int ax = acx + dx, ay = acy + dy;
                        int bx = W / 2 + ex + dx, by = H / 2 + ey + dy;
                        if (ax < 0 || ay < 0 || ax >= W || ay >= H ||
                            bx < 0 || by < 0 || bx >= W || by >= H) { sad = long.MaxValue; goto skip; }
                        sad += Math.Abs((long)A[ay * W + ax] - B[by * W + bx]);
                    }
                    if (sad < bestSad) { bestSad = sad; bex = ex; bey = ey; }
                    skip: ;
                }
                double err = Math.Sqrt((double)bex * bex + bey * bey);
                // Distinct iters in the A patch — guards against a flat (SOLID) region
                // giving a false 0-err match.
                var patchDistinct = new System.Collections.Generic.HashSet<int>();
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    patchDistinct.Add(A[(acy + dy) * W + (acx + dx)]);

                string errStr = patchDistinct.Count <= 1 ? "  (flat patch — err N/A)"
                                                         : $" |err|={err,6:F2}px";
                sb.AppendLine(
                    $"  zoom={zoom,8:G3} {tier}  focus-err=({bex,3},{bey,3}){errStr}  " +
                    $"frameDistinct={frameDistinct.Count,5} frameInSet={frameInSetPct,5:F1}%  " +
                    $"patchDistinct={patchDistinct.Count,3} maxUseful=1e{calcMaxUseful,4:F0}");
            }

            string fpPath = System.IO.Path.Combine(AppContext.BaseDirectory, "focusprobe.out");
            System.IO.File.WriteAllText(fpPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --navrepro: reproduce a USER-reported deep-zoom navigation issue from a
        // coordinate file (`navrepro.txt` next to the exe, or a path in args[1]).
        // The user copies CX / CY straight from the floating menu (pipe-separated
        // limbs) plus zoom + client px + the click offset; this renders the frame,
        // performs the exact double-click focus through FractalInputController,
        // re-renders, and patch-matches the clicked feature to report the real
        // focus error in pixels. File format (one key=value per line):
        //   cx=-1.99...|-7.8E-17|1.6E-34|...      (up to 8 pipe-separated limbs)
        //   cy=-5.5E-06|...
        //   zoom=5e63
        //   dim=1600            (client width in px; square assumed unless h= given)
        //   h=900               (optional client height)
        //   click=44,26         (optional; pixels off-centre the user clicked)
        if (args.Length > 0 && args[0] == "--navrepro")
        {
            string path = args.Length > 1
                ? args[1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "navrepro.txt");
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"--navrepro: no coordinate file at {path}. See the header comment for the format.");
                return 1;
            }

            double[] ParseLimbs(string s)
            {
                var parts = s.Split('|');
                var v = new double[8];
                for (int i = 0; i < 8; i++)
                    v[i] = i < parts.Length && double.TryParse(parts[i].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0.0;
                return v;
            }

            double[] cx = new double[8], cy = new double[8];
            double zoom = 1e60; int W = 1000, H = 1000, clickX = 44, clickY = 26;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "cx": cx = ParseLimbs(val); break;
                    case "cy": cy = ParseLimbs(val); break;
                    case "zoom": double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out zoom); break;
                    case "dim": case "w": int.TryParse(val, out W); break;
                    case "h": int.TryParse(val, out H); break;
                    case "click":
                        var cp = val.Split(',');
                        if (cp.Length == 2) { int.TryParse(cp[0].Trim(), out clickX); int.TryParse(cp[1].Trim(), out clickY); }
                        break;
                }
            }
            if (H == 1000 && W != 1000) H = W;   // square unless h given
            const int r = 3, search = 16;
            clickX = Math.Min(clickX, W / 2 - r - search - 1);
            clickY = Math.Min(clickY, H / 2 - r - search - 1);

            var sb = new System.Text.StringBuilder();
            int Nz(double[] a) { int n = 0; foreach (var v in a) if (v != 0) n++; return n; }
            int nzX = Nz(cx), nzY = Nz(cy);
            sb.AppendLine($"NAV REPRO — {W}x{H}  zoom={zoom:G6}  click=(+{clickX},+{clickY})");
            sb.AppendLine($"  centre limbs: X {nzX}/8  Y {nzY}/8");

            int maxIter = FracturingFog.Models.QualityPreset.Extreme.ComputeIterations(zoom);
            FracturingFog.MandelbrotCalculator Make(double[] x, double[] y)
                => new FracturingFog.MandelbrotCalculator(W, H)
                {
                    CenterX = x[0], CenterXLo = x[1], CenterX2 = x[2], CenterX3 = x[3],
                    CenterX4 = x[4], CenterX5 = x[5], CenterX6 = x[6], CenterX7 = x[7],
                    CenterY = y[0], CenterYLo = y[1], CenterY2 = y[2], CenterY3 = y[3],
                    CenterY4 = y[4], CenterY5 = y[5], CenterY6 = y[6], CenterY7 = y[7],
                    Zoom = zoom, MaxIterations = maxIter,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };

            // Optional path toggles to test whether the render divergence is
            // fixable precision: `--navrepro <file> norebase|acceloff|saoff`.
            foreach (var a in args)
            {
                if (a == "norebase") FracturingFog.MandelbrotCalculator.AllowPtRebasing = false;
                if (a == "ddref") FracturingFog.MandelbrotCalculator.UseDdRebaseReference = true;
                if (a == "scalar") FracturingFog.MandelbrotCalculator.ForceScalarPtPath = true;
            }
            bool accelOff = Array.IndexOf(args, "acceloff") >= 0;
            bool saOff = Array.IndexOf(args, "saoff") >= 0;
            sb.AppendLine($"  path: rebase={FracturingFog.MandelbrotCalculator.AllowPtRebasing} " +
                          $"ddRef={FracturingFog.MandelbrotCalculator.UseDdRebaseReference} " +
                          $"accelOff={accelOff} saOff={saOff}");

            var calcA = Make(cx, cy);
            calcA.DisableAcceleration = accelOff; calcA.DisableSeriesApproximation = saOff;
            calcA.Calculate();
            int[] A = (int[])calcA.IterationBuffer.Clone();
            var fd = new System.Collections.Generic.HashSet<int>();
            foreach (var it in A) fd.Add(it);
            sb.AppendLine($"  frame: distinctIters={fd.Count}  maxUseful=1e{calcA.MaxUsefulZoomLog10:F0}  " +
                          $"ref-orbit {(calcA.ReferenceOrbitEscaped ? "escaped@" + calcA.ReferenceOrbitLength : "bounded")}  " +
                          $"rebasedPx={calcA.PtRebasedPixels}/{W * H}");

            var vs = new FracturingFog.ViewState.FractalViewState
            {
                FractalType = FracturingFog.FractalType.Mandelbrot,
                Quality = FracturingFog.Models.QualityPreset.Extreme, Zoom = zoom,
                CenterX = cx[0], CenterXLo = cx[1], CenterX2 = cx[2], CenterX3 = cx[3],
                CenterX4 = cx[4], CenterX5 = cx[5], CenterX6 = cx[6], CenterX7 = cx[7],
                CenterY = cy[0], CenterYLo = cy[1], CenterY2 = cy[2], CenterY3 = cy[3],
                CenterY4 = cy[4], CenterY5 = cy[5], CenterY6 = cy[6], CenterY7 = cy[7],
            };
            var ctl = new FracturingFog.Input.FractalInputController(vs);
            ctl.OnPointerDoubleClick(new FracturingFog.Input.PointerInput(
                W / 2 + clickX, H / 2 + clickY, W, H,
                FracturingFog.Input.PointerButton.Left, FracturingFog.Input.InputModifiers.None));

            double[] bx = { vs.CenterX, vs.CenterXLo, vs.CenterX2, vs.CenterX3, vs.CenterX4, vs.CenterX5, vs.CenterX6, vs.CenterX7 };
            double[] by = { vs.CenterY, vs.CenterYLo, vs.CenterY2, vs.CenterY3, vs.CenterY4, vs.CenterY5, vs.CenterY6, vs.CenterY7 };

            // PURE-MATH input check (no render / patch-match): the controller's new
            // centre vs the ideal centre computed directly in OD. Isolates an input
            // fault from a render/patch-match artefact.
            {
                double scaleD = (3.5 / Math.Max(W, H)) / zoom;
                var cAx = new FracturingFog.FFMath.OD(cx[0], cx[1], cx[2], cx[3], cx[4], cx[5], cx[6], cx[7]);
                var cAy = new FracturingFog.FFMath.OD(cy[0], cy[1], cy[2], cy[3], cy[4], cy[5], cy[6], cy[7]);
                var idealX = FracturingFog.FFMath.OD.FromCenterOffset(cAx, clickX, scaleD);
                var idealY = FracturingFog.FFMath.OD.FromCenterOffset(cAy, clickY, scaleD);
                var ctlX = new FracturingFog.FFMath.OD(bx[0], bx[1], bx[2], bx[3], bx[4], bx[5], bx[6], bx[7]);
                var ctlY = new FracturingFog.FFMath.OD(by[0], by[1], by[2], by[3], by[4], by[5], by[6], by[7]);
                double ex = (double)(ctlX - idealX) / scaleD;
                double ey = (double)(ctlY - idealY) / scaleD;
                sb.AppendLine($"  input-math err (controller centre vs ideal OD): " +
                              $"({ex:E2},{ey:E2}) px  [0 ⇒ input exact ⇒ any focus-err below is RENDER/patch]");
            }

            var calcB = Make(bx, by);
            calcB.DisableAcceleration = accelOff; calcB.DisableSeriesApproximation = saOff;
            calcB.Calculate();
            int[] B = (int[])calcB.IterationBuffer.Clone();

            int acx = W / 2 + clickX, acy = H / 2 + clickY;
            long bestSad = long.MaxValue; int bex = 0, bey = 0;
            for (int ey = -search; ey <= search; ey++)
            for (int ex = -search; ex <= search; ex++)
            {
                long sad = 0;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int ax = acx + dx, ay = acy + dy, bxp = W / 2 + ex + dx, byp = H / 2 + ey + dy;
                    if (ax < 0 || ay < 0 || ax >= W || ay >= H || bxp < 0 || byp < 0 || bxp >= W || byp >= H) { sad = long.MaxValue; goto skip; }
                    sad += Math.Abs((long)A[ay * W + ax] - B[byp * W + bxp]);
                }
                if (sad < bestSad) { bestSad = sad; bex = ex; bey = ey; }
                skip: ;
            }
            // SAD at offset (0,0) — if it is ~as good as the found minimum, the
            // structure is self-similar and the "min" offset is a patch-match
            // artefact, not a real render displacement.
            long sad00 = 0;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int ax = acx + dx, ay = acy + dy, bxp = W / 2 + dx, byp = H / 2 + dy;
                if (ax >= 0 && ay >= 0 && ax < W && ay < H && bxp >= 0 && byp >= 0 && bxp < W && byp < H)
                    sad00 += Math.Abs((long)A[ay * W + ax] - B[byp * W + bxp]);
            }

            var patch = new System.Collections.Generic.HashSet<int>();
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) patch.Add(A[(acy + dy) * W + (acx + dx)]);
            double err = Math.Sqrt((double)bex * bex + bey * bey);
            sb.AppendLine($"  match: SAD(min)={bestSad} at ({bex},{bey})  vs SAD(0,0)={sad00}  " +
                          $"[SAD(0,0)≈SAD(min) ⇒ self-similar ⇒ min is a patch artefact]");
            sb.AppendLine(patch.Count <= 1
                ? "  focus-err: N/A (clicked patch is flat — pick a textured spot / lower zoom)"
                : $"  focus-err = ({bex},{bey})  |{err:F2}| px   (0 = perfect; >1 = the reported bug, reproduced)");

            string np = System.IO.Path.Combine(AppContext.BaseDirectory, "navrepro.out");
            System.IO.File.WriteAllText(np, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        // --panjitter: SM-11b validation. Simulate a horizontal drag (centre
        // steps by `step` px each frame) at a deep centre and measure how much
        // each preview frame CHANGES beyond the pure pan translation — that
        // change is the "image jumps around while dragging" the user reports.
        // Compares two render modes: FRESH (recompute the reference orbit every
        // frame, current preview behaviour) vs RECYCLE (reuse one reference across
        // the drag, SM-11b). Metric: for consecutive frames, shift frame[i] by the
        // known `step` and SAD it against frame[i+1] over the overlap — 0 = the
        // image only translated (stable); large = reference-recompute shimmer.
        if (args.Length > 0 && args[0] == "--panjitter")
        {
            const int W = 512, H = 512, frames = 6;
            int[] steps = { 2, 8, 20, 40 };
            if (args.Length > 1 && int.TryParse(args[1], out int stepArg) && stepArg > 0)
                steps = new[] { stepArg };
            double[] cx = { -1.9918151296901943, -7.8219844803880472E-17, 1.6601399303928428E-34,
                             5.9806621034830635E-51, -2.60673981819717E-67, 0, 0, 0 };
            double[] cy = { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924089534E-39,
                             -3.7336955151644623E-55, -2.8541322190114832E-71, 0, 0, 0 };
            double zoom = 4.65087e64;
            int maxIter = FracturingFog.Models.QualityPreset.Extreme.ComputeIterations(zoom);
            double scaleD = (3.5 / Math.Max(W, H)) / zoom;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pan-jitter probe — {W}x{H} zoom={zoom:G4}, {frames} frames per drag.");
            sb.AppendLine("  interFrameSAD = change beyond the pure pan (0 = stable, high = shimmer).");

            // Build the OD centre for a given horizontal pixel-step offset k.
            FracturingFog.MandelbrotCalculator MakeAt(int k, int step, bool recycle, FracturingFog.MandelbrotCalculator? reuse)
            {
                var cxOD = FracturingFog.FFMath.OD.FromCenterOffset(
                    new FracturingFog.FFMath.OD(cx[0], cx[1], cx[2], cx[3], cx[4], cx[5], cx[6], cx[7]),
                    k * step, scaleD);
                var c = reuse ?? new FracturingFog.MandelbrotCalculator(W, H)
                {
                    Zoom = zoom, MaxIterations = maxIter,
                    Quality = FracturingFog.Models.QualityPreset.Extreme,
                    ColorMap = new FracturingFog.Models.HsvPalette(),
                };
                c.CenterX = cxOD.X0; c.CenterXLo = cxOD.X1; c.CenterX2 = cxOD.X2; c.CenterX3 = cxOD.X3;
                c.CenterX4 = cxOD.X4; c.CenterX5 = cxOD.X5; c.CenterX6 = cxOD.X6; c.CenterX7 = cxOD.X7;
                c.CenterY = cy[0]; c.CenterYLo = cy[1]; c.CenterY2 = cy[2]; c.CenterY3 = cy[3];
                c.CenterY4 = cy[4]; c.CenterY5 = cy[5]; c.CenterY6 = cy[6]; c.CenterY7 = cy[7];
                c.AllowRecycleThisRender = recycle;
                return c;
            }

            long InterFrameSad(int[] a, int[] b, int step)
            {
                // b is the same view panned by +step px in X; shift a by step and
                // compare on the overlap [step, W) so a pure translation scores 0.
                long sad = 0; int n = 0;
                for (int y = 0; y < H; y += 2)
                for (int x = step; x < W; x += 2)
                { sad += Math.Abs((long)a[y * W + (x - step)] - b[y * W + x]); n++; }
                return n > 0 ? sad / n : 0;   // avg |Δiter| per sampled pixel
            }

            foreach (int step in steps)
            {
                foreach (bool recycle in new[] { false, true })
                {
                    var reuseCalc = recycle ? new FracturingFog.MandelbrotCalculator(W, H)
                    {
                        Zoom = zoom, MaxIterations = maxIter,
                        Quality = FracturingFog.Models.QualityPreset.Extreme,
                        ColorMap = new FracturingFog.Models.HsvPalette(),
                    } : null;

                    int[]? prev = null; long sum = 0, worst = 0; int cnt = 0;
                    for (int k = 0; k < frames; k++)
                    {
                        var c = MakeAt(k, step, recycle, reuseCalc);
                        c.Calculate();
                        int[] cur = (int[])c.IterationBuffer.Clone();
                        if (prev != null)
                        {
                            long s = InterFrameSad(prev, cur, step);
                            sum += s; if (s > worst) worst = s; cnt++;
                        }
                        prev = cur;
                    }
                    sb.AppendLine($"  step={step,2}px [{(recycle ? "RECYCLE" : "FRESH  ")}] " +
                                  $"avg interFrameSAD={(cnt > 0 ? sum / cnt : 0),6}  worst={worst,6}");
                }
            }

            string pjPath = System.IO.Path.Combine(AppContext.BaseDirectory, "panjitter.out");
            System.IO.File.WriteAllText(pjPath, sb.ToString());
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
