// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbrotGpuKernelBench.cs — #1162 slice B: BenchmarkDotNet harness for
// MandelbrotCalculator's GPU kernel paths.
//
// Invoke via:   FracturingFog.exe --bench --filter *MandelbrotGpuKernelBench*
//
// Coverage matrix:
//   • Backend : D3D11 (Rendering.D3D MandelbrotGpuKernel, default hardware
//               adapter) · Vulkan (VulkanComputeKernel, own context)
//   • Path    :
//       - SpShallow  : UseGpuCompute, zoom 1, 512 iter   (SP iteration loop)
//       - SpZoom1e4  : UseGpuCompute, seahorse @ 1e4 (= MaxGpuZoom), 2048 iter
//       - PerturbInPT: UseGpuPerturbation, seahorse @ 1e15, 2048 iter (every
//                      pixel inside the perturbation loop; cf. DeepHPInPT)
//       - PerturbDeep: UseGpuPerturbation, seahorse @ 1e15, 4096 iter (cf. DeepHP)
//   • Width   : 640x360 · 1920x1080
//
// Same coordinates as MandelbrotBench so CPU and GPU rows are comparable.
//
// Like GpuCalculatorBench, this refuses to time a CPU fallback: Setup checks
// MandelbrotCalculator.LastFrameUsedGpuCompute / LastFrameUsedGpuPerturbation
// after a warm frame and throws if the GPU path didn't run (no device, no
// fp64 for the perturbation kernel, …), so BenchmarkDotNet reports the case as
// failed instead of producing a CPU number.
//
// These kernels keep persistent device buffers (no per-frame device
// allocation), so there is no DeviceAlloc/op column here; the Device column
// records the adapter that ran the case.

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using FracturingFog.Models;

namespace FracturingFog.Benchmarks;

public enum GpuKernelBackend
{
    D3D11,
    Vulkan,
}

public enum GpuKernelPath
{
    SpShallow,
    SpZoom1e4,
    PerturbInPT,
    PerturbDeep,
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(Config))]
public class MandelbrotGpuKernelBench
{
    private MandelbrotCalculator _calc = null!;
    private FracturingFog.Rendering.IGpuKernel _kernel = null!;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private bool _savedUseGpuPerturbation;

    [Params(GpuKernelBackend.D3D11, GpuKernelBackend.Vulkan)]
    public GpuKernelBackend Backend { get; set; }

    [Params(GpuKernelPath.SpShallow, GpuKernelPath.SpZoom1e4,
            GpuKernelPath.PerturbInPT, GpuKernelPath.PerturbDeep)]
    public GpuKernelPath Path { get; set; }

    [Params(640, 1920)]
    public int Width { get; set; }

    private bool IsPerturbation => Path is GpuKernelPath.PerturbInPT or GpuKernelPath.PerturbDeep;

    [GlobalSetup]
    public void Setup()
    {
        string device;
        (_kernel, device) = Backend switch
        {
            GpuKernelBackend.D3D11 => CreateD3D11Kernel(),
            GpuKernelBackend.Vulkan => CreateVulkanKernel(),
            _ => throw new ArgumentOutOfRangeException(nameof(Backend)),
        };

        if (IsPerturbation && !_kernel.SupportsPerturbation)
            throw new InvalidOperationException(
                $"{Backend} '{device}' has no fp64 shader support, so the GPU perturbation kernel can't run.");

        int height = Width == 640 ? 360 : 1080;
        _calc = new MandelbrotCalculator(Width, height)
        {
            ColorMap = new HsvPalette(),
            GpuKernel = _kernel,
            UseGpuCompute = !IsPerturbation,
        };
        // Process-wide static (V6 #82); restored in Cleanup.
        _savedUseGpuPerturbation = MandelbrotCalculator.UseGpuPerturbation;
        MandelbrotCalculator.UseGpuPerturbation = IsPerturbation;

        switch (Path)
        {
            case GpuKernelPath.SpShallow:
                _calc.CenterX = -0.5; _calc.CenterY = 0.0;
                _calc.Zoom = 1.0; _calc.MaxIterations = 512;
                break;
            case GpuKernelPath.SpZoom1e4:
                _calc.CenterX = -0.743643887037151; _calc.CenterY = 0.131825904205330;
                _calc.Zoom = MandelbrotCalculator.MaxGpuZoom; _calc.MaxIterations = 2048;
                break;
            case GpuKernelPath.PerturbInPT:
                _calc.CenterX = -0.743643887037151; _calc.CenterY = 0.131825904205330;
                _calc.Zoom = 1e15; _calc.MaxIterations = 2048;
                break;
            case GpuKernelPath.PerturbDeep:
                _calc.CenterX = -0.743643887037151; _calc.CenterY = 0.131825904205330;
                _calc.Zoom = 1e15; _calc.MaxIterations = 4096;
                break;
        }

        // Warm frame: compiles / loads shaders and proves the GPU path ran.
        _calc.Calculate();
        bool ranOnGpu = IsPerturbation ? _calc.LastFrameUsedGpuPerturbation : _calc.LastFrameUsedGpuCompute;
        if (!ranOnGpu)
        {
            // The calculator's GPU-PERTURB-TOO-SLOW guard turns the static off
            // for the session when the estimated frame exceeds its time budget;
            // the app would render this view on the CPU too.
            string why = IsPerturbation && !MandelbrotCalculator.UseGpuPerturbation
                ? " The calculator's too-slow guard disabled GPU perturbation (estimated frame over its time budget on this device)."
                : "";
            throw new InvalidOperationException(
                $"{Backend}/{Path}: the frame fell back to the CPU on '{device}'.{why} " +
                "Refusing to time a CPU fallback as a GPU number.");
        }

        CaseMetrics.Record(CaseMetrics.Device,
            CaseMetrics.Key(new (string, object?)[]
            {
                (nameof(Backend), Backend),
                (nameof(Path), Path),
                (nameof(Width), Width),
            }),
            device);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        MandelbrotCalculator.UseGpuPerturbation = _savedUseGpuPerturbation;
        _calc = null!;
        _kernel?.Dispose();
        _kernel = null!;
        _d3dContext?.Dispose();
        _d3dContext = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }

    [Benchmark]
    public void Calculate() => _calc.Calculate();

    private (FracturingFog.Rendering.IGpuKernel, string) CreateD3D11Kernel()
    {
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
        if (hr.Failure || device == null || context == null)
            throw new InvalidOperationException($"No D3D11 hardware device (0x{hr.Code:X8}).");
        _d3dDevice = device;
        _d3dContext = context;

        string name = "D3D11 hardware adapter";
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            name = adapter.Description.Description;
        }
        catch { /* name is cosmetic */ }

        return (new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object()), $"D3D11 {name}");
    }

    private static (FracturingFog.Rendering.IGpuKernel, string) CreateVulkanKernel()
    {
        var kernel = FracturingFog.Rendering.Vulkan.VulkanComputeKernel.TryCreateWithOwnContext()
            ?? throw new InvalidOperationException("No Vulkan compute device available.");
        return (kernel, kernel.Description);
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // Same in-process job as MandelbrotBench (see the toolchain note
            // there). The Device column also depends on running in-process.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
            AddDiagnoser(MemoryDiagnoser.Default);
            AddColumn(new CaseMetricColumn(CaseMetrics.Device,
                "GPU adapter that ran the case (backend + device name)",
                isNumeric: false, UnitType.Dimensionless));
        }
    }
}
