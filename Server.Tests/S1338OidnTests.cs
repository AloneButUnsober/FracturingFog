// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1338 — Intel Open Image Denoise as an optional learned denoiser. Invariants:
//   • off (the default) and on-without-the-runtime render exactly the option-off frame;
//     an AOV view and a low-res drag preview are never denoised;
//   • on, a 1 spp 3D frame and a ss-1 relief frame get closer (PSNR) to a 4x4 / ss-4
//     supersampled reference — measured independently of OIDN's own output;
//   • on the relief raymarch OIDN replaces the A-Trous pass; without the runtime the
//     A-Trous pass runs exactly as configured;
//   • batch parity: --oidn parses, the Command builder emits it from the live view and
//     it parses back, the preset carries the field, the fidelity check covers it;
//   • the installer takes only the CPU runtime + licences, and rejects a wrong hash.
// Tests that need the native runtime skip when it is not installed (set FF_OIDN_DIR to
// an unpacked OIDN 2.x release, or install it from the app). They live in one class, so
// they never run concurrently with each other (OidnDenoiser.Disabled is static).

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1338OidnTests(ITestOutputHelper output)
{
    private static bool Oidn => OidnDenoiser.IsAvailable;

    [Fact]
    public void Denoising_Noise_Brings_The_Image_Closer_To_The_Clean_One()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed: " + OidnDenoiser.LoadError); return; }
        const int W = 128, H = 96;
        var clean = new float[W * H * 3];
        var noisy = new float[W * H * 3];
        var rng = new Random(1338);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                for (int c = 0; c < 3; c++)
                {
                    int i = (y * W + x) * 3 + c;
                    clean[i] = 0.2f + 0.6f * x / W * (c + 1) / 3f;
                    noisy[i] = Math.Clamp(clean[i] + (float)(rng.NextDouble() - 0.5) * 0.3f, 0f, 1f);
                }
        var outp = new float[W * H * 3];
        Assert.True(OidnDenoiser.TryDenoise(noisy, null, null, W, H, hdr: false, srgb: true, outp, out var err), err);
        double Mse(float[] a) => a.Zip(clean, (p, q) => (double)(p - q) * (p - q)).Average();
        output.WriteLine($"from {OidnDenoiser.LoadedFrom}: mse noisy {Mse(noisy):E3}, denoised {Mse(outp):E3}");
        Assert.True(Mse(outp) < Mse(noisy) / 4);
    }

    // ── 3D ─────────────────────────────────────────────────────────────────────

    private const int W = 160, H = 90;

    private delegate void FxEdit(ref LightingFxData l);   // LightingFxData is a struct

    private static FractalParameters Kifs(bool oidn, FxEdit? fx = null)
    {
        var fp = new FractalParameters();
        var l = fp.Lighting;
        l.UseGpuRender = false;
        l.OidnDenoise = oidn;
        fx?.Invoke(ref l);
        fp.Lighting = l;
        return fp;
    }

    private static uint[] RenderKifs(FractalParameters fp, int ss = 1, bool lowRes = false)
    {
        var c = new KifsCalculator(W * ss, H * ss) { FractalParameters = fp, ColorMap = ColorPalette.BuiltIns[0], LowResPreview = lowRes };
        c.Calculate(CancellationToken.None);
        return ss == 1 ? c.ColorBuffer.Take(W * H).ToArray() : Box(c.ColorBuffer, ss);
    }

    private static uint[] Box(uint[] big, int ss)
    {
        var o = new uint[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int r = 0, g = 0, b = 0, n = ss * ss;
                for (int j = 0; j < ss; j++)
                    for (int i = 0; i < ss; i++)
                    {
                        uint p = big[(y * ss + j) * W * ss + x * ss + i];
                        r += (int)((p >> 16) & 255); g += (int)((p >> 8) & 255); b += (int)(p & 255);
                    }
                o[y * W + x] = 0xFF000000u | (uint)((r + n / 2) / n) << 16 | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n);
            }
        return o;
    }

    private static double Psnr(uint[] a, uint[] b)
    {
        double se = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
            {
                double d = (int)((a[i] >> s) & 255) - (int)((b[i] >> s) & 255);
                se += d * d;
            }
        return 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-12, se / (a.Length * 3.0)));
    }

    [Fact]
    public void Off_And_Without_The_Runtime_The_3D_Frame_Is_Unchanged()
    {
        var off = RenderKifs(Kifs(false));
        try
        {
            OidnDenoiser.Disabled = true;
            Assert.Equal(off, RenderKifs(Kifs(true)));
            Assert.NotNull(ScreenSpacePost.LastOidnSkip);
        }
        finally { OidnDenoiser.Disabled = false; }
    }

    [Fact]
    public void Denoised_1spp_3D_Frames_Get_Closer_To_The_Supersampled_Reference()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed"); return; }
        var reference = RenderKifs(Kifs(false), ss: 4);
        var low = RenderKifs(Kifs(false));
        var den = RenderKifs(Kifs(true));
        double before = Psnr(reference, low), after = Psnr(reference, den);
        output.WriteLine($"KIFS 1 spp vs 16 spp: {before:F2} dB, with OIDN {after:F2} dB");
        Assert.NotEqual(low, den);
        Assert.True(after > before + 2, $"{before:F2} -> {after:F2} dB");
        // An AOV view and a low-res drag preview are left alone.
        var nOff = RenderKifs(Kifs(false, (ref LightingFxData l) => l.DebugAov = AovView.Normals));
        Assert.NotEqual(low, nOff);   // precondition: the view is really the AOV
        Assert.Equal(nOff, RenderKifs(Kifs(true, (ref LightingFxData l) => l.DebugAov = AovView.Normals)));
        Assert.Equal(RenderKifs(Kifs(false), lowRes: true), RenderKifs(Kifs(true), lowRes: true));
    }

    // ── Relief ─────────────────────────────────────────────────────────────────

    private static (uint[] Albedo, float[] Height) ReliefSource()
    {
        var m = new MandelbrotCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0, CenterX = -0.5 };
        m.Calculate(CancellationToken.None);
        var smooth = m.SmoothBuffer;
        float max = 1e-6f;
        for (int i = 0; i < W * H; i++) max = Math.Max(max, smooth[i]);
        return (m.ColorBuffer.Take(W * H).ToArray(), smooth.Take(W * H).Select(v => v / max).ToArray());
    }

    private static uint[] Relief(uint[] albedo, float[] height, int ss, bool oidn, int atrous)
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DSupersample = ss, Relief2DDenoiseIterations = atrous };
        var l = p.Lighting; l.UseGpuRender = false; l.OidnDenoise = oidn; p.Lighting = l;
        // As the host's final relief stage does it.
        var aov = ReliefDenoisePass.MakeCapture(p, W, H);
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, height, W, H, W, H, p, dst, out _, null, aov);
        ReliefDenoisePass.Apply(dst, aov, W, H, p);
        return dst;
    }

    [Fact]
    public void Relief_Oidn_Replaces_ATrous_And_Falls_Back_Without_The_Runtime()
    {
        var (albedo, height) = ReliefSource();
        var raw = Relief(albedo, height, 1, oidn: false, atrous: 0);
        var atrous = Relief(albedo, height, 1, oidn: false, atrous: 3);
        Assert.NotEqual(raw, atrous);   // precondition: the A-Trous pass does something
        try
        {
            OidnDenoiser.Disabled = true;
            Assert.Equal(raw, Relief(albedo, height, 1, oidn: true, atrous: 0));
            Assert.Equal(atrous, Relief(albedo, height, 1, oidn: true, atrous: 3));
        }
        finally { OidnDenoiser.Disabled = false; }
        if (!Oidn) { output.WriteLine("OIDN not installed"); return; }
        var oidnOnly = Relief(albedo, height, 1, oidn: true, atrous: 0);
        Assert.Equal(oidnOnly, Relief(albedo, height, 1, oidn: true, atrous: 3));   // OIDN replaces A-Trous
        var reference = Relief(albedo, height, 4, oidn: false, atrous: 0);
        double before = Psnr(reference, raw), after = Psnr(reference, oidnOnly);
        output.WriteLine($"relief ss1 vs ss4: {before:F2} dB, with OIDN {after:F2} dB, with A-Trous {Psnr(reference, atrous):F2} dB");
        Assert.True(after > before, $"{before:F2} -> {after:F2} dB");
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_Flag_Parses_And_The_Command_Builder_Emits_It()
    {
        var baseArgs = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        Assert.True(BatchOptions.TryParse(baseArgs.Append("--oidn").ToArray(), 0, out var on, out var e1), e1);
        Assert.True(on.Oidn);
        Assert.True(BatchOptions.TryParse(baseArgs, 0, out var off, out var e2), e2);
        Assert.False(off.Oidn);

        string cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { Oidn = true });
        Assert.Contains(" --oidn", cmd);
        Assert.DoesNotContain("--oidn", BatchCommandBuilder.Build(new BatchCommandSnapshot()));
        // The emitted command parses back with the switch on.
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.IndexOf(tokens, "--batch") + 1;
        Assert.True(at > 0, cmd);
        Assert.True(BatchOptions.TryParse(tokens.Skip(at).Concat(new[] { "--out", "o.png" }).ToArray(), 0, out var back, out var e3), e3 + " | " + cmd);
        Assert.True(back.Oidn);
    }

    [Fact]
    public void The_Field_Defaults_Off_And_Travels_In_Presets_And_The_Fidelity_Check()
    {
        var fx = LightingFxData.CreateDefault();
        Assert.False(fx.OidnDenoise);
        var on = fx; on.OidnDenoise = true;
        Assert.NotEqual(fx.SceneSignature(), on.SceneSignature());
        Assert.True(LightingFxPresetData.FromFx(on).ToFx().OidnDenoise);
        Assert.DoesNotContain("OidnDenoise", LightingFidelity.UnexpressedFields(on));
    }

    [Fact]
    public void The_Menu_Opens_The_Setup()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FloatingMenuViewModel();
        int opened = 0;
        vm.OidnSetupClick += (_, _) => opened++;
        vm.OidnSetupCommand.Execute().Subscribe();
        Assert.Equal(1, opened);
    }

    // ── The installer ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("oidn-2.5.1.x64.windows/bin/OpenImageDenoise.dll", true)]
    [InlineData("oidn-2.5.1.x64.windows/bin/OpenImageDenoise_core.dll", true)]
    [InlineData("oidn-2.5.1.x64.windows/bin/OpenImageDenoise_device_cpu.dll", true)]
    [InlineData("oidn-2.5.1.x64.windows/bin/tbb12.dll", true)]
    [InlineData("oidn-2.5.1.x64.windows/doc/LICENSE.txt", true)]
    [InlineData("oidn-2.5.1.x64.windows/doc/third-party-programs-oneTBB.txt", true)]
    [InlineData("oidn-2.5.1.x64.windows/bin/OpenImageDenoise_device_cuda.dll", false)]
    [InlineData("oidn-2.5.1.x64.windows/bin/OpenImageDenoise_device_sycl.dll", false)]
    [InlineData("oidn-2.5.1.x64.windows/bin/sycl9.dll", false)]
    [InlineData("oidn-2.5.1.x64.windows/bin/tbbbind_2_5.dll", false)]
    [InlineData("oidn-2.5.1.x64.windows/bin/oidnDenoise.exe", false)]
    [InlineData("oidn-2.5.1.x64.windows/lib/OpenImageDenoise.lib", false)]
    [InlineData("oidn-2.5.1.x64.windows/include/OpenImageDenoise/oidn.h", false)]
    [InlineData("oidn-2.5.1.x64.windows/doc/readme.pdf", false)]
    [InlineData("oidn-2.5.1.x86_64.linux/lib/libOpenImageDenoise.so.2.5.1", true)]
    [InlineData("oidn-2.5.1.x86_64.linux/lib/libOpenImageDenoise.so.2", true)]
    [InlineData("oidn-2.5.1.x86_64.linux/lib/libOpenImageDenoise_device_cpu.so.2.5.1", true)]
    [InlineData("oidn-2.5.1.x86_64.linux/lib/libtbb.so.12", true)]
    [InlineData("oidn-2.5.1.x86_64.linux/lib/libOpenImageDenoise_device_hip.so.2.5.1", false)]
    [InlineData("oidn-2.5.1.arm64.macos/lib/libOpenImageDenoise.2.dylib", true)]
    [InlineData("oidn-2.5.1.arm64.macos/lib/libOpenImageDenoise_device_metal.2.5.1.dylib", false)]
    [InlineData("oidn-2.5.1.arm64.macos/lib/libtbb.12.dylib", true)]
    public void The_Installer_Takes_Only_The_Cpu_Runtime_And_Its_Licences(string entry, bool take)
        => Assert.Equal(take, OidnInstaller.ShouldExtract(entry));

    [Fact]
    public void The_Pinned_Archive_Installs_And_A_Wrong_Hash_Is_Rejected()
    {
        // FF_OIDN_ZIP: the official oidn-2.5.1.x64.windows.zip (skipped without it).
        var zip = Environment.GetEnvironmentVariable("FF_OIDN_ZIP");
        if (string.IsNullOrEmpty(zip) || !System.IO.File.Exists(zip)) { output.WriteLine("FF_OIDN_ZIP not set"); return; }
        var asset = new OidnInstaller.Asset(System.IO.Path.GetFileName(zip), "f11f91bc072a5e3a564515724cb72ab8fcfbc445c84a84c197ff9b16cc01396f");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-oidn-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A wrong hash: rejected, and nothing is written.
            var bad = OidnInstaller.InstallFromArchive(zip, asset with { Sha256 = new string('0', 64) }, dir);
            Assert.Equal(InstallOutcome.HashMismatch, bad.Outcome);
            Assert.False(System.IO.Directory.Exists(dir));

            var ok = OidnInstaller.InstallFromArchive(zip, asset, dir);
            Assert.True(ok.Success, ok.ErrorDetail);
            var files = System.IO.Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            output.WriteLine(string.Join(", ", files));
            Assert.Equal(new[]
            {
                "LICENSE.txt", "OpenImageDenoise.dll", "OpenImageDenoise_core.dll", "OpenImageDenoise_device_cpu.dll",
                "tbb12.dll", "third-party-programs-DPCPP.txt", "third-party-programs-oneTBB.txt", "third-party-programs.txt",
            }, files);
            // Installing again replaces the folder whole.
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "stale.txt"), "x");
            Assert.True(OidnInstaller.InstallFromArchive(zip, asset, dir).Success);
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(dir, "stale.txt")));
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
