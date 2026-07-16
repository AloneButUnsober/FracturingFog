// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

    // Phase X.5 / Slice 5.1 — per-RID device-kind smoke.
    //
    // Asserts:
    //   * ILGPU constructs without throwing.
    //   * At least one CPU device is exposed (the cross-platform fallback).
    //   * CPU accelerator JIT-creates and disposes cleanly.
    //   * Per-RID expectation: on osx-arm64 / linux-arm64, no CUDA device
    //     is enumerated (would indicate a packaging bug — CUDA on Linux
    //     ARM only ships on Jetson, never on Apple Silicon).
    //
    // Returns ok + a one-screen report suitable for CI logs and --self-test
    // output files. Used by both the WinExe (`--ilgpu-probe`) and the new
    // FracturingFog.App entry (`--ilgpu-probe`) so the same assertion lands
    // on every RID the release workflow ships.
    public static bool RunSmoke(out string report)
    {
        var sb = new StringBuilder();
        bool ok = true;

        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        sb.Append("RID: ").AppendLine(rid);
        sb.Append("OS:  ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.Append("Arch: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
        sb.AppendLine();
        sb.AppendLine(DescribeDevices());
        sb.AppendLine();

        bool sawCpu = false;
        bool sawCuda = false;
        try
        {
            using var ctx = Context.Create(b => b.Default());
            foreach (var d in ctx.Devices)
            {
                if (d.AcceleratorType == AcceleratorType.CPU) sawCpu = true;
                if (d.AcceleratorType == AcceleratorType.Cuda) sawCuda = true;
            }
        }
        catch (Exception ex)
        {
            sb.Append("ILGPU context failed: ").AppendLine(ex.Message);
            report = sb.ToString();
            return false;
        }

        if (!sawCpu)
        {
            sb.AppendLine("FAIL: no CPU device exposed.");
            ok = false;
        }
        else
        {
            sb.AppendLine("PASS: CPU device exposed.");
        }

        if (!TryCreateCpuAccelerator(out string err))
        {
            sb.Append("FAIL: CPU accelerator create: ").AppendLine(err);
            ok = false;
        }
        else
        {
            sb.AppendLine("PASS: CPU accelerator constructed + disposed.");
        }

        // Per-RID chosen-device assert (Open-Work-Plan 1.C3). The compute
        // paths (UserBulbGpuCalculator, GpuAcceleratorHost, the CalcGen
        // template, …) all pick their device via
        // GetPreferredDevice(preferCPU: false) — CUDA → OpenCL → CPU. Probe
        // the same selection here so the smoke asserts what the app actually
        // runs on, not merely that a CPU device is enumerable somewhere.
        AcceleratorType chosenKind = AcceleratorType.CPU;
        try
        {
            using var ctx = Context.Create(b => b.Default());
            var chosen = ctx.GetPreferredDevice(preferCPU: false);
            if (chosen == null)
            {
                sb.AppendLine("FAIL: GetPreferredDevice returned no device.");
                ok = false;
            }
            else
            {
                chosenKind = chosen.AcceleratorType;
                using var acc = chosen.CreateAccelerator(ctx);
                sb.Append("PASS: chosen device ").Append(chosenKind)
                  .Append(" — ").Append(chosen.Name)
                  .AppendLine(" (constructed + disposed).");
            }
        }
        catch (Exception ex)
        {
            sb.Append("FAIL: chosen-device create: ").AppendLine(ex.Message);
            ok = false;
        }

        bool isArmMac = OperatingSystem.IsMacOS() &&
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64;
        bool isArmLinux = OperatingSystem.IsLinux() &&
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64;
        if ((isArmMac || isArmLinux) && (sawCuda || chosenKind == AcceleratorType.Cuda))
        {
            sb.AppendLine("FAIL: CUDA device on ARM host — packaging or driver bug.");
            ok = false;
        }
        else if (isArmMac || isArmLinux)
        {
            sb.AppendLine("PASS: no CUDA device on ARM host (expected).");
        }

        sb.Append("Result: ").AppendLine(ok ? "OK" : "FAIL");
        report = sb.ToString();
        return ok;
    }
}
