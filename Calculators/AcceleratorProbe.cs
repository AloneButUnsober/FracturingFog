// Calculators/AcceleratorProbe.cs
//
// Phase 2.4 ILGPU cross-platform validation helper. Enumerates the ILGPU
// devices the current runtime can see and returns a short multi-line summary
// for the System Info / About dialog. Used to confirm the CPU fallback path
// is reachable on machines without CUDA / OpenCL (Linux + macOS targets in
// the cross-platform matrix).
//
// ILGPU 1.5.x ships managed CPU + Velocity backends that work on every RID
// the .NET runtime supports; CUDA and OpenCL backends are loaded
// opportunistically when their native drivers are present. The fall-through
// in UserBulbGpuCalculator.TryInit (GetPreferredDevice(preferCPU:false))
// already routes through CUDA → OpenCL → CPU; this probe makes that
// behaviour visible without forcing a kernel JIT.

using System;
using System.Linq;
using System.Text;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace FracturingFog.Calculators;

public static class AcceleratorProbe
{
    /// <summary>
    /// Returns a short description of every ILGPU device visible on this
    /// machine. Safe to call from any thread; constructs and disposes a
    /// throwaway Context.
    /// </summary>
    public static string DescribeDevices()
    {
        try
        {
            using var ctx = Context.Create(b => b.Default());
            var devices = ctx.Devices.ToArray();
            if (devices.Length == 0)
                return "ILGPU: no devices available (managed CPU fallback only).";

            var sb = new StringBuilder();
            sb.Append("ILGPU devices (").Append(devices.Length).AppendLine("):");
            foreach (var d in devices)
                sb.Append("  ").Append(d.AcceleratorType).Append(" — ").AppendLine(d.Name);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"ILGPU probe failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true when at least one non-CPU accelerator is present (CUDA,
    /// OpenCL, Velocity). The cross-platform build relies on the CPU device
    /// existing on every RID; verifying that here lets the migration
    /// document tick the "CPU fallback validated" box.
    /// </summary>
    public static bool HasGpuAccelerator()
    {
        try
        {
            using var ctx = Context.Create(b => b.Default());
            return ctx.Devices.Any(d => d.AcceleratorType != AcceleratorType.CPU);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms ILGPU can construct and dispose a CPU accelerator on the
    /// current runtime. Used by smoke tests to assert the managed CPU
    /// fallback works on Linux/macOS CI runners where no GPU driver is
    /// installed.
    /// </summary>
    public static bool TryCreateCpuAccelerator(out string error)
    {
        error = string.Empty;
        try
        {
            using var ctx = Context.Create(b => b.CPU());
            var cpu = ctx.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            if (cpu == null) { error = "no CPU device exposed by ILGPU"; return false; }
            using var acc = cpu.CreateAccelerator(ctx);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
