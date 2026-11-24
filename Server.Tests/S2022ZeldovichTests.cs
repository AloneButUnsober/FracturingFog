// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2022 — Cosmic Web S2: the Zel'dovich approximation on a power-law Gaussian random field.
// Independent checks:
//   • the FFT inverts itself and matches a direct DFT on a small grid;
//   • (a) the generated field's binned power |delta_k|^2 follows k^n (log-log slope) for several n;
//   • the displacement satisfies div s = -delta exactly in Fourier space (i k . s_k = -delta_k);
//   • its derivative matrix is symmetric (s is a gradient) and the eigenvalue signs follow the
//     8% / 42% / 42% / 8% split Zeldovich (1970, p. 89, Eq. A.13) quotes from Doroshkevich;
//   • (b) linear regime: at small D the particle density contrast (a cloud-in-cell count written here;
//     CIC after Birdsall & Fuss 1969)
//     equals D delta_L, to first order;
//   • (c) shell crossing: the fraction of particles in cells at 4x the mean density rises with D;
//   • determinism by seed; changing D only reuses the cached field;
//   • settings survive the command line; presets render the same live, from the command line and as
//     a poster.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.CosmicWeb;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2022ZeldovichTests
{
    private static readonly string[] Presets = { "Cosmic Web - Zeldovich Web Forming", "Cosmic Web - Zeldovich Mature Web" };

    // ── FFT ──

    [Fact]
    public void Fft_Matches_A_Direct_Dft_And_Inverts()
    {
        const int n = 8;
        var rng = new SplitMix64(1);
        var re = new double[n * n * n];
        var im = new double[n * n * n];
        for (int i = 0; i < re.Length; i++) { re[i] = rng.NextDouble() - 0.5; im[i] = rng.NextDouble() - 0.5; }
        var r0 = (double[])re.Clone();
        var i0 = (double[])im.Clone();
        Fft3D.Forward(re, im, n);
        // Direct DFT of a few modes.
        foreach (var (kx, ky, kz) in new[] { (0, 0, 0), (1, 2, 3), (7, 0, 4), (4, 4, 4) })
        {
            double sr = 0, si = 0;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int i = (z * n + y) * n + x;
                        double a = -2 * Math.PI * (kx * x + ky * y + kz * z) / n;
                        sr += r0[i] * Math.Cos(a) - i0[i] * Math.Sin(a);
                        si += r0[i] * Math.Sin(a) + i0[i] * Math.Cos(a);
                    }
            int k = (kz * n + ky) * n + kx;
            Assert.Equal(sr, re[k], 9);
            Assert.Equal(si, im[k], 9);
        }
        Fft3D.Inverse(re, im, n);
        for (int i = 0; i < re.Length; i++) { Assert.Equal(r0[i], re[i], 12); Assert.Equal(i0[i], im[i], 12); }
    }

    // ── The field ──

    private static (double[] Re, double[] Im) Spectrum(float[] f, int n)
    {
        var re = f.Select(v => (double)v).ToArray();
        var im = new double[re.Length];
        Fft3D.Forward(re, im, n);
        return (re, im);
    }

    [Theory]
    [InlineData(-2.0)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void The_Field_Power_Follows_K_To_The_N(double index)
    {
        const int n = 64;
        var f = Zeldovich.Build(new ZeldovichFieldSettings(5, n, index, 0.0));
        // rms 1 by construction.
        Assert.Equal(1.0, Math.Sqrt(f.Delta.Select(v => (double)v * v).Average()), 4);
        var (re, im) = Spectrum(f.Delta, n);
        // Bin |delta_k|^2 in shells of integer |kappa| (grid units), kappa = 2..n/4 (away from k = 0 and the corners).
        var sum = new double[n];
        var cnt = new int[n];
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    double kap = Math.Sqrt(Sq(Fft3D.Freq(x, n)) + Sq(Fft3D.Freq(y, n)) + Sq(Fft3D.Freq(z, n)));
                    int b = (int)Math.Round(kap);
                    if (b < 2 || b > n / 4) continue;
                    int i = (z * n + y) * n + x;
                    sum[b] += re[i] * re[i] + im[i] * im[i];
                    cnt[b]++;
                }
        var xs = new List<double>();
        var ys = new List<double>();
        for (int b = 2; b <= n / 4; b++) { xs.Add(Math.Log(b)); ys.Add(Math.Log(sum[b] / cnt[b])); }
        double slope = Slope(xs, ys);
        TestContext.Current.TestOutputHelper?.WriteLine($"n = {index}: measured slope {slope:0.###}");
        Assert.True(Math.Abs(slope - index) < 0.1, $"n = {index}: measured slope {slope:0.###}");
    }

    private static double Sq(double v) => v * v;

    private static double Slope(List<double> xs, List<double> ys)
    {
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); }
        return sxy / sxx;
    }

    [Fact]
    public void The_Displacement_Has_Divergence_Minus_Delta_In_Fourier_Space()
    {
        const int n = 32;
        var f = Zeldovich.Build(new ZeldovichFieldSettings(9, n, -1.0, 1.0));
        var (dr, di) = Spectrum(f.Delta, n);
        var (xr, xi) = Spectrum(f.Sx, n);
        var (yr, yi) = Spectrum(f.Sy, n);
        var (zr, zi) = Spectrum(f.Sz, n);
        double err = 0, norm = 0;
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (x == n / 2 || y == n / 2 || z == n / 2) continue;   // Nyquist planes: s_j is zeroed there
                    int i = (z * n + y) * n + x;
                    double f2 = 2 * Math.PI / n;
                    double kx = Fft3D.Freq(x, n) * f2, ky = Fft3D.Freq(y, n) * f2, kz = Fft3D.Freq(z, n) * f2;
                    // i k . s = i (kx sx + ky sy + kz sz): real part -(k . Im s), imaginary part (k . Re s).
                    double divRe = -(kx * xi[i] + ky * yi[i] + kz * zi[i]);
                    double divIm = kx * xr[i] + ky * yr[i] + kz * zr[i];
                    err += Sq(divRe + dr[i]) + Sq(divIm + di[i]);
                    norm += Sq(dr[i]) + Sq(di[i]);
                }
        TestContext.Current.TestOutputHelper?.WriteLine($"div s + delta relative error {Math.Sqrt(err / norm):E2}");
        Assert.True(Math.Sqrt(err / norm) < 1e-4, $"relative error {Math.Sqrt(err / norm):E2}");
    }

    /// <summary>d s_i / d q_j by spectral differentiation (i k_j in Fourier space, 0 on the Nyquist plane).</summary>
    private static double[] Derivative(float[] si, int axis, int n)
    {
        var (re, im) = Spectrum(si, n);
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int idx = axis == 0 ? x : axis == 1 ? y : z;
                    int i = (z * n + y) * n + x;
                    double kj = idx == n / 2 ? 0 : Fft3D.Freq(idx, n) * 2 * Math.PI / n;
                    (re[i], im[i]) = (-im[i] * kj, re[i] * kj);
                }
        Fft3D.Inverse(re, im, n);
        return re;
    }

    [Fact]
    public void Deformation_Eigenvalue_Signs_Follow_Doroshkevich()
    {
        // Zeldovich 1970, p. 89 (Eq. A.13 and the text after it): for a Gaussian field the three
        // deformation eigenvalues are all of one sign 8% of the time each way, and split 2:1 42% each way.
        // Positive roots of a symmetric matrix's characteristic polynomial lambda^3 - I1 lambda^2 + I2 lambda - I3
        // are counted by the sign changes of its coefficients (exact, since every root is real).
        const int n = 64;
        var f = Zeldovich.Build(new ZeldovichFieldSettings(2022, n, -2.0, 1.0));
        var s = new[] { f.Sx, f.Sy, f.Sz };
        var d = new double[3, 3][];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                d[i, j] = Derivative(s[i], j, n);
        var counts = new int[4];
        double asym = 0, scale = 0;
        for (int p = 0; p < n * n * n; p++)
        {
            double a = d[0, 0][p], b = d[1, 1][p], c = d[2, 2][p];
            double xy = 0.5 * (d[0, 1][p] + d[1, 0][p]), xz = 0.5 * (d[0, 2][p] + d[2, 0][p]), yz = 0.5 * (d[1, 2][p] + d[2, 1][p]);
            asym += Sq(d[0, 1][p] - d[1, 0][p]) + Sq(d[0, 2][p] - d[2, 0][p]) + Sq(d[1, 2][p] - d[2, 1][p]);
            scale += Sq(xy) + Sq(xz) + Sq(yz);
            double i1 = a + b + c;
            double i2 = a * b + a * c + b * c - xy * xy - xz * xz - yz * yz;
            double i3 = a * (b * c - yz * yz) - xy * (xy * c - yz * xz) + xz * (xy * yz - b * xz);
            double[] coef = { 1, -i1, i2, -i3 };
            int changes = 0;
            double prev = coef[0];
            for (int k = 1; k < 4; k++)
            {
                if (coef[k] == 0) continue;
                if (Math.Sign(coef[k]) != Math.Sign(prev)) changes++;
                prev = coef[k];
            }
            counts[changes]++;
        }
        // s is a gradient (his p = grad Phi, Eqs. A.11-A.12), so its derivative matrix is symmetric.
        Assert.True(Math.Sqrt(asym / scale) < 1e-4, $"asymmetry {Math.Sqrt(asym / scale):E2}");
        var frac = counts.Select(c => c / (double)(n * n * n)).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine("positive eigenvalues 0/1/2/3: " + string.Join(", ", frac.Select(v => v.ToString("0.####"))));
        Assert.InRange(frac[0], 0.065, 0.095);
        Assert.InRange(frac[3], 0.065, 0.095);
        Assert.InRange(frac[1], 0.40, 0.44);
        Assert.InRange(frac[2], 0.40, 0.44);
    }

    // ── Particles ──

    /// <summary>Cloud-in-cell density contrast of the particles on an n^3 grid over the box (written here,
    /// independent of the generator).</summary>
    private static double[] CicContrast(CosmicWebPoints pts, int n)
    {
        var rho = new double[n * n * n];
        double scale = n / pts.BoxSize;
        for (int p = 0; p < pts.Count; p++)
        {
            double gx = pts.Xyz[3 * p] * scale, gy = pts.Xyz[3 * p + 1] * scale, gz = pts.Xyz[3 * p + 2] * scale;
            int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy), z0 = (int)Math.Floor(gz);
            double fx = gx - x0, fy = gy - y0, fz = gz - z0;
            for (int dz = 0; dz < 2; dz++)
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
                        int x = (x0 + dx) % n, y = (y0 + dy) % n, z = (z0 + dz) % n;
                        rho[(z * n + y) * n + x] += w;
                    }
        }
        double mean = (double)pts.Count / rho.Length;
        for (int i = 0; i < rho.Length; i++) rho[i] = rho[i] / mean - 1;
        return rho;
    }

    [Fact]
    public void In_The_Linear_Regime_The_Particle_Density_Is_D_Times_The_Field()
    {
        const int n = 32;
        const double d = 1e-3;
        var f = Zeldovich.Build(new ZeldovichFieldSettings(3, n, -1.0, 3.0));   // a smooth field
        var pts = Zeldovich.Displace(f, d, 100.0);
        var rho = CicContrast(pts, n);
        // Regression of rho / D on delta_L: slope 1 and a tight fit (first-order CIC differencing of a
        // smooth field, so a few percent).
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < rho.Length; i++)
        {
            double a = f.Delta[i], b = rho[i] / d;
            sxy += a * b; sxx += a * a; syy += b * b;
        }
        double slope = sxy / sxx, corr = sxy / Math.Sqrt(sxx * syy);
        TestContext.Current.TestOutputHelper?.WriteLine($"linear regime: slope {slope:0.####}, correlation {corr:0.####}");
        Assert.True(Math.Abs(slope - 1) < 0.1, $"slope {slope:0.###}");
        Assert.True(corr > 0.95, $"correlation {corr:0.###}");
    }

    [Fact]
    public void Past_Shell_Crossing_Particles_Pile_Into_Dense_Cells()
    {
        const int n = 32;
        var f = Zeldovich.Build(new ZeldovichFieldSettings(4, n, -1.0, 1.0));
        double Dense(double d)
        {
            var pts = Zeldovich.Displace(f, d, 100.0);
            // Nearest-grid-point counts on an (n/2)^3 grid: mean 8 per cell.
            int m = n / 2;
            var count = new int[m * m * m];
            var cellOf = new int[pts.Count];
            for (int p = 0; p < pts.Count; p++)
            {
                int x = Math.Min(m - 1, (int)(pts.Xyz[3 * p] / 100.0 * m));
                int y = Math.Min(m - 1, (int)(pts.Xyz[3 * p + 1] / 100.0 * m));
                int z = Math.Min(m - 1, (int)(pts.Xyz[3 * p + 2] / 100.0 * m));
                cellOf[p] = (z * m + y) * m + x;
                count[cellOf[p]]++;
            }
            double mean = (double)pts.Count / count.Length;
            return cellOf.Count(c => count[c] >= 4 * mean) / (double)pts.Count;
        }
        var fr = new[] { 0.1, 0.5, 1.0, 2.0 }.Select(Dense).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine("dense fraction at D = 0.1, 0.5, 1, 2: " + string.Join(", ", fr.Select(v => v.ToString("0.####"))));
        for (int i = 1; i < fr.Length; i++)
            Assert.True(fr[i] > fr[i - 1], $"dense fraction did not rise: {string.Join(", ", fr.Select(v => v.ToString("0.####")))}");
        Assert.Equal(0.0, fr[0]);   // D = 0.1: still a near-uniform grid
        Assert.True(fr[^1] > 0.05, $"D = 2: only {fr[^1]:0.####} in dense cells");
    }

    [Fact]
    public void The_Field_Is_Deterministic_By_Seed_And_Cached_Across_D()
    {
        var a = Zeldovich.Build(new ZeldovichFieldSettings(11, 16, -1, 1));
        var b = Zeldovich.Build(new ZeldovichFieldSettings(11, 16, -1, 1));
        Assert.Equal(a.Delta, b.Delta);
        Assert.Equal(a.Sx, b.Sx);
        Assert.NotEqual(a.Delta, Zeldovich.Build(new ZeldovichFieldSettings(12, 16, -1, 1)).Delta);

        var p = new FractalParameters { CosmicWebGenerator = CosmicWebGenerator.Zeldovich, CosmicWebGrid = 16, CosmicWebSeed = 11 };
        var f1 = CosmicWebCalculator.ZeldovichFieldFor(p);
        p.CosmicWebGrowth = 0.3;
        var pts = CosmicWebCalculator.PointsFor(p);
        Assert.Same(f1, CosmicWebCalculator.ZeldovichFieldFor(p));
        Assert.Equal(16 * 16 * 16, pts.Count);
        // D = 0: the bare grid.
        p.CosmicWebGrowth = 0;
        var grid = CosmicWebCalculator.PointsFor(p);
        Assert.Equal(0f, grid.Xyz[0]);
        Assert.Equal(100f / 16, grid.Xyz[3], 5);
    }

    [Fact]
    public void Grid_Sizes_Snap_To_Powers_Of_Two()
    {
        Assert.Equal(16, Zeldovich.SanitiseGrid(3));
        Assert.Equal(64, Zeldovich.SanitiseGrid(100));
        Assert.Equal(256, Zeldovich.SanitiseGrid(1000));
    }

    // ── Batch, presets, help ──

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.CosmicWeb, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.CosmicWeb, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("CosmicWeb", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.CosmicWeb, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Zeldovich_Settings_Survive_The_Command_Line()
    {
        var p = new FractalParameters
        {
            CosmicWebGenerator = CosmicWebGenerator.Zeldovich, CosmicWebGrid = 32, CosmicWebSpectralIndex = -1.75,
            CosmicWebSmoothing = 2.5, CosmicWebGrowth = 1.25, CosmicWebSeed = 99, CosmicWebBoxSize = 80,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("CosmicWeb", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            Assert.Equal(CosmicWebGenerator.Zeldovich, p.CosmicWebGenerator);
            CosmicWebCalculator Render(FractalParameters fp)
            {
                var c = new CosmicWebCalculator(48, 30) { ColorMap = new HsvPalette(), Zoom = region.Zoom, FractalParameters = fp };
                c.Calculate(TestContext.Current.CancellationToken);
                return c;
            }
            var live = Render(p);
            Assert.True(live.ColorBuffer.SequenceEqual(Render(ViaCommandLine(p)).ColorBuffer), $"{name}: --param differs");
            var poster = PosterRenderer.RenderToPixels(new PosterRequest
            {
                FractalType = FractalType.CosmicWeb, Width = 48, Height = 30, CenterX = 0, CenterY = 0, Zoom = region.Zoom,
                MaxIterations = live.MaxIterations, Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            }, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs");
            Assert.True(live.ColorBuffer.Distinct().Count() > 5, $"{name}: flat");
            Assert.StartsWith("Zel'dovich: 128^3", live.LastPrecisionLabel);
        }
    }

    [Fact]
    public void The_Animation_Drives_Growth_And_Help_Names_The_Sources()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var anim = lib.GetByName("Cosmic web: forming")!;
        Assert.Contains(anim.Tracks, t => t.ParamName == "CosmicWebGrowth");
        string help = HelpTextBundle.MathCosmicWebText.Replace("\r\n", " ").Replace("\n", " ");
        Assert.Contains("Zel'dovich approximation (1970)", help);
        Assert.Contains("Bertschinger", help);
        Assert.All(Presets, n => Assert.Contains(n, help));
    }
}
