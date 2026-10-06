// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #749 — the 3D-fractal GPU kernels must actually JIT. They silently did not:
// every kernel called System.Math.Clamp(double,double,double), which lowers to
// a call to Math.ThrowMinMaxException, and ILGPU cannot compile the Throw IL
// that leaves in the kernel graph ("Not supported IL instruction of type
// 'Throw'"). So LoadAutoGroupedStreamKernel threw for all eight families, the
// calculators fell back to the CPU ShadingPipeline, and the whole 3D GPU test
// family was passing trivially via that fallback -- never exercising the kernel.
//
// Fixed by GpuKernelUtils.Clamp (a branch-only, throw-free clamp; byte-identical
// for ordered bounds) replacing every device-path Math.Clamp. This test locks it
// in: each family kernel is JIT-loaded on the ILGPU CPU accelerator (forced via
// GpuAcceleratorHost.SetTestOverride so it is device-independent and available on
// every CI runner) and TryInit must succeed. A reintroduced Math.Clamp -- or any
// other throw -- in a kernel path fails the load and trips this test.
//
// JIT only, no render: the compile is what #749 was about, and it keeps the test
// cheap (no per-pixel CPU-accelerator raymarch). UserBulbGpuCalculator is covered
// by its own suite; it owns its accelerator (ignores the override) so it is not
// forced onto the CPU device here.

using System;
using System.Linq;
using System.Reflection;
using FracturingFog.Calculators.Gpu;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S749Gpu3DKernelJitTests
{
    public static readonly TheoryData<Type> FamilyCalculators = new()
    {
        typeof(MandelbulbGpuCalculator),
        typeof(MandelboxGpuCalculator),
        typeof(MengerGpuCalculator),
        typeof(SierpinskiGpuCalculator),
        typeof(QJuliaGpuCalculator),
        typeof(QMandelGpuCalculator),
        typeof(KleinianGpuCalculator),
        typeof(BicomplexGpuCalculator),
    };

    [Theory]
    [MemberData(nameof(FamilyCalculators))]
    public void Family_Kernel_Jits_On_Cpu_Accelerator(Type calcType)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        var cpuDev = ctx.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
        Assert.NotNull(cpuDev); // ILGPU always exposes a CPU device.
        using var acc = cpuDev!.CreateAccelerator(ctx);

        AssertKernelJits(calcType, acc,
            "#749 regression — a Math.Clamp or other throw is back?");
    }

    // #1164 — the CPU accelerator has .NET math for every intrinsic, so the test
    // above can't see backend-specific failures. On CUDA every family failed to
    // load ("SinF does not have an intrinsic implementation for this backend")
    // because the context lacked ILGPU.Algorithms, and the app silently rendered
    // on the CPU. JIT each family on every real fp64 GPU device present, through
    // the same GpuAcceleratorHost.CreateContext the app uses. A GPU-less runner
    // has no such device and the test passes vacuously; on a dev box with a GPU
    // it catches the next backend gap.
    [Theory]
    [MemberData(nameof(FamilyCalculators))]
    public void Family_Kernel_Jits_On_Every_Gpu_Device(Type calcType)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        foreach (var dev in ctx.Devices.Where(d =>
                     d.AcceleratorType != AcceleratorType.CPU && GpuAcceleratorHost.SupportsFloat64(d)))
        {
            // CUDA launch failures are sticky for the whole PROCESS. Another test
            // in this run may have launched a kernel that faulted on this device
            // (#1169 / #1170 on weak hardware) — then the device is unusable here
            // and that is not this test's subject. JIT never launches, so it can't
            // be the cause; skip rather than misreport.
            Accelerator acc;
            try { acc = dev.CreateAccelerator(ctx); }
            catch (ILGPU.Runtime.Cuda.CudaException ex)
            {
                Assert.Skip($"{dev.Name} unusable in this process (sticky CUDA fault from an earlier test: {ex.Message})");
                return;
            }
            using (acc)
                AssertKernelJits(calcType, acc,
                    $"on {dev.AcceleratorType} '{dev.Name}' (#1164 — missing EnableAlgorithms or another backend gap?)",
                    skipOnStickyFault: true);
        }
    }

    private static void AssertKernelJits(Type calcType, Accelerator acc, string hint, bool skipOnStickyFault = false)
    {
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var inst = Activator.CreateInstance(calcType)!;
            try
            {
                var tryInit = calcType.GetMethod("TryInit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                bool ok = (bool)tryInit.Invoke(inst, null)!;
                var lastError = (string)calcType.GetProperty("LastError")!.GetValue(inst)!;
                if (!ok && skipOnStickyFault && lastError.Contains("unspecified launch failure", StringComparison.Ordinal))
                    Assert.Skip($"device poisoned by an earlier test's CUDA fault: {lastError}");
                Assert.True(ok, $"{calcType.Name} kernel failed to JIT {hint}: {lastError}");
            }
            finally
            {
                (inst as IDisposable)?.Dispose();
            }
        }
        finally
        {
            GpuAcceleratorHost.SetTestOverride(null);
        }
    }
}
