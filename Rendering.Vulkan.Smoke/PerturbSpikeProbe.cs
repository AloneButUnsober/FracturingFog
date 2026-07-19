// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V6 spike (#82): --vulkanpturbprobe. Feasibility gate for a GPU perturbation
// kernel (deep-zoom on GPU). See Docs/Deep-Zoom-Perturbation.md §2 and
// Docs/Technical/Vulkan-Compute-DevelopmentPlan.md §13/§14.
//
// This is a SPIKE, not the production kernel. It answers the open questions in
// issue #82 before committing to the full build:
//
//   (1) LOOP PARITY. Does a `double` δ-rebased perturbation loop in HLSL —
//       δ_{n+1} = (2·Z[m] + δ)·δ + dc, with Zhuoran rebasing (SM-2) — running
//       on the GPU match the CPU `ComputePixelPTRebased` (the default double
//       path, MandelbrotCalculator.cs) for a genuinely deep view? The probe
//       runs a self-contained C# mirror of that loop and the same loop as an
//       HLSL kernel, and compares iteration counts pixel-for-pixel.
//
//   (2) dc PRECISION AT DEPTH. Is a single-rounded `double` dc = pixelOffset·
//       scale enough, or is a double-double dc needed? δ stays double either way
//       (per the doc). The probe compares the double path against a DD oracle
//       (DD dc + DD δ, local TwoProduct/TwoSum limb math — no FMA, per the
//       ILGPU-ICE caution in #82) and reports the divergence.
//
//   (3) DXC COMPILES THE DOUBLE MATH. Reaching a green run proves DXC `-spirv`
//       emits the Float64 capability and the driver runs it. The FXC `cs_5_0`
//       leg is a separate compile-only check (fxc is Windows/D3D-only; not wired
//       into this cross-platform smoke — run it against the dumped HLSL).
//
//   (4) CONSUMER-GPU FP64 VIABILITY. If the picked device does not advertise
//       shaderFloat64 (VulkanContext.SupportsFloat64 == false) the probe prints
//       SKIP and exits 0 — that absence is itself a spike finding.
//
// Deep view without many-digit centre arithmetic: centre = (-0.75, 0), the
// parabolic root of the period-2 bulb. It is EXACTLY double-representable and
// its orbit stays bounded to maxIter, so a double reference orbit is faithful
// and the frame is non-degenerate at any zoom (the long bounded orbit amplifies
// the ~1e-21 dc back to O(1) detail — the maxUseful mechanism, doc §3). This
// isolates the GPU-vs-CPU loop question from the OD-centre machinery, which the
// full build would layer on top.

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Probe = FracturingFog.Rendering.Vulkan.Smoke.RealKernelProbe;

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static unsafe class PerturbSpikeProbe
{
    // Same escape radius the CPU perturbation path uses (512² for smooth
    // shading; the δ loop tests |z|² ≥ this).
    private const double EscapeR2 = 512.0 * 512.0;

    // Deep view firmly past MaxGpuZoom=1e4 (perturbation-only regime). Centre is a
    // seahorse-valley boundary point whose orbit ranges up to |Z|≈2 — so the
    // amplification ∏|2·Zₙ| is large (finite maxUseful) and the tiny dc is lifted
    // back to O(1) detail, giving a NON-degenerate frame that actually exercises
    // the rebase + escape branches. Contrast the parabolic root (-0.75,0), whose
    // orbit stays small: amplification ≈1, deep neighbourhood uniformly interior.
    // The centre is ~15 significant digits, so a double carries it to ~1e-16 world
    // error — negligible against a ~1e-6-wide frame (a genuine deep boundary point
    // beyond double's reach is the full build's OD-centre job, not the spike's).
    private const int Dim = 96;
    private const int MaxIter = 6000;
    private const double Zoom = 1e6;
    private const double CenterX = -0.743643887037151, CenterY = 0.13182590420533;

    // DXC binding shifts (shared convention with RealKernelProbe): b→0, t→100,
    // u→200. Refs are t0/t1 → SSBO 100/101; output iter is u0 → SSBO 200.
    private const int BShift = 0, TShift = 100, UShift = 200;

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamsBlob
    {
        public int W, H, MaxIter, RefLen;   // 16 bytes → doubles land 8-aligned
        public double Scale;
        public double EscapeR2;
    }

    public static int Run(VulkanContext ctx)
    {
        if (!ctx.SupportsFloat64)
        {
            Console.WriteLine(
                $"vulkanpturbprobe SKIP: {ctx.PickedType} {ctx.PickedName} has no " +
                "shaderFloat64. A double GPU perturbation kernel cannot run here " +
                "(finding for #82 checkbox 4 — needs an FP64-capable device).");
            return 0; // absence of FP64 is informative, not a smoke failure.
        }

        double scale = 3.5 / (Math.Max(Dim, Dim) * Zoom);

        // Reference orbit at the (exact) centre, in double. Bounded to maxIter
        // for this centre, so refLen == MaxIter.
        BuildReferenceOrbit(CenterX, CenterY, MaxIter, out double[] refZr, out double[] refZi, out int refLen);

        // CPU mirrors: double path (GPU-matched) + DD oracle (checkbox 2).
        int[] cpuDouble = CpuMirrorDouble(scale, refZr, refZi, refLen);
        int[] cpuDd = CpuMirrorDd(scale, refZr, refZi, refLen);

        // GPU: the same double loop as an HLSL kernel via DXC → SPIR-V.
        int[] gpu = RunVulkan(ctx, scale, refZr, refZi, refLen);

        int n = Dim * Dim;

        // Non-degeneracy: a deep frame on a bounded-orbit centre must show
        // structure, not one flat escape value.
        var distinct = new System.Collections.Generic.HashSet<int>(cpuDouble);
        int inSet = 0; for (int i = 0; i < n; i++) if (cpuDouble[i] >= MaxIter) inSet++;

        // (1) GPU vs CPU double loop.
        int gpuDisagree = 0, maxIterDelta = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(gpu[i] - cpuDouble[i]);
            if (d != 0) { gpuDisagree++; if (d > maxIterDelta) maxIterDelta = d; }
        }
        double gpuFrac = (double)gpuDisagree / n;

        // (2) double dc vs DD dc/δ oracle.
        int ddDisagree = 0, ddMaxDelta = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(cpuDouble[i] - cpuDd[i]);
            if (d != 0) { ddDisagree++; if (d > ddMaxDelta) ddMaxDelta = d; }
        }
        double ddFrac = (double)ddDisagree / n;

        Console.WriteLine(
            $"vulkanpturbprobe {Dim}x{Dim} zoom={Zoom:0e+0} maxIter={MaxIter} refLen={refLen} " +
            $"scale={scale:0.000e+00} dc~{Math.Abs(0.5 * Dim * scale):0.0e+00}");
        Console.WriteLine(
            $"  non-degeneracy: distinct={distinct.Count} inSet={inSet}/{n}");
        // maxΔiter is large on the handful of filament pixels that straddle the
        // escape-time knife-edge — a sub-ULP rounding difference flips them by
        // many iterations (doc §2: the QD path disagrees with ITSELF by the same
        // order). So the meaningful metric is the disagreement FRACTION vs the
        // CPU precision NOISE FLOOR (the double-vs-DD divergence), not maxΔ or a
        // strict digest — same philosophy as --vulkanprobe's ULP band.
        Console.WriteLine(
            $"  (1) GPU vs CPU-double:      disagree={gpuDisagree}/{n} ({gpuFrac:P3}) maxΔiter={maxIterDelta} (boundary chaos)");
        Console.WriteLine(
            $"  (2) CPU-double vs DD oracle: disagree={ddDisagree}/{n} ({ddFrac:P3}) maxΔiter={ddMaxDelta} " +
            $"→ single-double dc {(ddFrac <= 0.02 ? "SUFFICES" : "INSUFFICIENT")} at this depth (δ noise floor)");
        Console.WriteLine(
            $"  GPU disagreement {(gpuFrac <= Math.Max(0.02, 4.0 * ddFrac) ? "AT" : "ABOVE")} the CPU precision noise floor " +
            $"→ {(gpuFrac <= Math.Max(0.02, 4.0 * ddFrac) ? "no GPU dialect gap" : "GPU-SPECIFIC divergence")}");

        // The frame must be non-flat (otherwise parity is vacuous), and the GPU
        // divergence must sit within the ULP band AND within a small multiple of
        // the CPU's own double-vs-DD noise floor — i.e. the GPU adds no error
        // beyond the inherent boundary chaos.
        bool nonDegenerate = distinct.Count >= 8 && inSet < n;
        bool parity = gpuFrac <= Math.Max(0.02, 4.0 * ddFrac);

        if (!nonDegenerate)
        {
            Console.Error.WriteLine(
                "vulkanpturbprobe FAIL: degenerate frame (dc underflowed or centre " +
                "orbit amplification too low) — the parity check would be vacuous.");
            return 1;
        }
        if (!parity)
        {
            Console.Error.WriteLine(
                "vulkanpturbprobe FAIL: GPU double perturbation loop diverged from the " +
                "CPU mirror ABOVE the precision noise floor — a real dialect/precision gap.");
            return 1;
        }

        Console.WriteLine($"vulkanpturbprobe OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }

    // ── Reference orbit (double, exact centre) ────────────────────────────────
    private static void BuildReferenceOrbit(
        double cr, double ci, int maxIter, out double[] zr, out double[] zi, out int refLen)
    {
        zr = new double[maxIter];
        zi = new double[maxIter];
        double x = 0.0, y = 0.0;
        double ampLog10 = 0.0;   // Σ log₁₀|2·Zₙ| — the detail-depth amplification (doc §3)
        int n = 0;
        for (; n < maxIter; n++)
        {
            zr[n] = x; zi[n] = y;
            double x2 = x * x, y2 = y * y;
            double mag2 = x2 + y2;
            if (mag2 >= EscapeR2) { n++; break; }
            if (mag2 > 0) ampLog10 += 0.5 * Math.Log10(4.0 * mag2); // log10|2Z| = ½log10(4|Z|²)
            double xy = x * y;
            x = x2 - y2 + cr;
            y = xy + xy + ci;
        }
        refLen = n;
        Console.WriteLine($"  ref orbit: len={n} escaped={(n < maxIter)} amplification≈1e{ampLog10:0.0} (maxUseful proxy)");
    }

    // ── CPU mirror: exact double twin of ComputePixelPTRebased (no derivative,
    // iteration count only). Keep in sync with MandelbrotCalculator.cs. ───────
    private static int[] CpuMirrorDouble(double scale, double[] refZr, double[] refZi, int refLen)
    {
        int[] outIter = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            double dcR = (px - 0.5 * Dim) * scale;
            double dcI = (py - 0.5 * Dim) * scale;

            double dr = 0.0, di = 0.0;
            int m = 0;
            double zr = 0.0, zi = 0.0;
            int iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                double Zr = refZr[m], Zi = refZi[m];
                zr = Zr + dr; zi = Zi + di;
                double zmag2 = zr * zr + zi * zi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr * dr + di * di;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = 0.0; Zi = 0.0; m = 0; }
                double a = 2.0 * Zr + dr, b = 2.0 * Zi + di;
                double newDr = a * dr - b * di + dcR;
                double newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            outIter[py * Dim + px] = iter;
        }
        return outIter;
    }

    // ── DD oracle: same loop with double-double dc + δ (ref stays Hi-only, per
    // the default path). Answers "is single-double dc enough at this depth?" ──
    private static int[] CpuMirrorDd(double scale, double[] refZr, double[] refZi, int refLen)
    {
        int[] outIter = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            Dd dcR = Dd.FromProduct(px - 0.5 * Dim, scale);
            Dd dcI = Dd.FromProduct(py - 0.5 * Dim, scale);

            Dd dr = default, di = default;
            int m = 0;
            double zrHi = 0.0, ziHi = 0.0;
            int iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                Dd Zr = new Dd(refZr[m]), Zi = new Dd(refZi[m]);
                Dd zr = Zr + dr, zi = Zi + di;
                zrHi = zr.Hi; ziHi = zi.Hi;
                double zmag2 = zrHi * zrHi + ziHi * ziHi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr.Hi * dr.Hi + di.Hi * di.Hi;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = default; Zi = default; m = 0; }
                Dd a = Zr * 2.0 + dr, b = Zi * 2.0 + di;
                Dd newDr = a * dr - b * di + dcR;
                Dd newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            outIter[py * Dim + px] = iter;
        }
        return outIter;
    }

    // ── Vulkan: double δ-rebased kernel via DXC → SPIR-V ─────────────────────
    private static int[] RunVulkan(VulkanContext ctx, double scale, double[] refZr, double[] refZi, int refLen)
    {
        byte[] spirv = DxcCompiler.CompileToSpirv(
            Hlsl(), entry: "main", profile: "cs_6_0",
            "-fvk-b-shift", BShift.ToString(), "0",
            "-fvk-t-shift", TShift.ToString(), "0",
            "-fvk-u-shift", UShift.ToString(), "0");

        var vk = ctx.Vk;
        var device = ctx.Device;
        int n = Dim * Dim;

        var blob = new ParamsBlob { W = Dim, H = Dim, MaxIter = MaxIter, RefLen = refLen, Scale = scale, EscapeR2 = EscapeR2 };
        ulong paramsSize = (ulong)sizeof(ParamsBlob);
        ulong refSize = (ulong)(refLen * sizeof(double));
        ulong iterSize = (ulong)(n * sizeof(int));

        var buffers = new Probe.Allocated[4]; // 0=params,1=refZr,2=refZi,3=iter
        ShaderModule module = default;
        DescriptorSetLayout dsl = default;
        PipelineLayout layout = default;
        Pipeline pipeline = default;
        DescriptorPool pool = default;
        CommandPool cmdPool = default;
        nint entryPtr = 0;

        try
        {
            buffers[0] = Probe.AllocBuffer(ctx, paramsSize, BufferUsageFlags.UniformBufferBit);
            buffers[1] = Probe.AllocBuffer(ctx, refSize, BufferUsageFlags.StorageBufferBit);
            buffers[2] = Probe.AllocBuffer(ctx, refSize, BufferUsageFlags.StorageBufferBit);
            buffers[3] = Probe.AllocBuffer(ctx, iterSize, BufferUsageFlags.StorageBufferBit);

            Probe.WriteBuffer(ctx, buffers[0], &blob, (int)paramsSize);
            fixed (double* pr = refZr) Probe.WriteBuffer(ctx, buffers[1], pr, (int)refSize);
            fixed (double* pi = refZi) Probe.WriteBuffer(ctx, buffers[2], pi, (int)refSize);

            fixed (byte* code = spirv)
            {
                var smci = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Probe.Check(vk.CreateShaderModule(device, in smci, null, out module), "vkCreateShaderModule");
            }

            var bindings = stackalloc DescriptorSetLayoutBinding[4]
            {
                Probe.LayoutBinding(0,              DescriptorType.UniformBuffer),
                Probe.LayoutBinding((uint)TShift,   DescriptorType.StorageBuffer),   // gRefZr t0
                Probe.LayoutBinding((uint)TShift+1, DescriptorType.StorageBuffer),   // gRefZi t1
                Probe.LayoutBinding((uint)UShift,   DescriptorType.StorageBuffer),   // gIter  u0
            };
            var dslci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 4,
                PBindings = bindings,
            };
            Probe.Check(vk.CreateDescriptorSetLayout(device, in dslci, null, out dsl), "vkCreateDescriptorSetLayout");

            var dslLocal = dsl;
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &dslLocal,
            };
            Probe.Check(vk.CreatePipelineLayout(device, in plci, null, out layout), "vkCreatePipelineLayout");

            entryPtr = SilkMarshal.StringToPtr("main");
            var cpci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = (byte*)entryPtr,
                },
                Layout = layout,
            };
            Pipeline created;
            Probe.Check(vk.CreateComputePipelines(device, default, 1, &cpci, null, &created), "vkCreateComputePipelines");
            pipeline = created;

            var poolSizes = stackalloc DescriptorPoolSize[2]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
                new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 3 },
            };
            var dpci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
            };
            Probe.Check(vk.CreateDescriptorPool(device, in dpci, null, out pool), "vkCreateDescriptorPool");

            var dsai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &dslLocal,
            };
            Probe.Check(vk.AllocateDescriptorSets(device, in dsai, out DescriptorSet set), "vkAllocateDescriptorSets");

            var infos = stackalloc DescriptorBufferInfo[4];
            var writes = stackalloc WriteDescriptorSet[4];
            uint[] bindNums = { 0, (uint)TShift, (uint)TShift + 1, (uint)UShift };
            DescriptorType[] types =
            {
                DescriptorType.UniformBuffer, DescriptorType.StorageBuffer,
                DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            };
            for (int i = 0; i < 4; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = buffers[i].Buffer, Offset = 0, Range = Vk.WholeSize };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = bindNums[i],
                    DescriptorCount = 1,
                    DescriptorType = types[i],
                    PBufferInfo = &infos[i],
                };
            }
            vk.UpdateDescriptorSets(device, 4, writes, 0, null);

            var cmdPoolCi = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = ctx.ComputeQueueFamily,
            };
            Probe.Check(vk.CreateCommandPool(device, in cmdPoolCi, null, out cmdPool), "vkCreateCommandPool");

            var cbai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = cmdPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Probe.Check(vk.AllocateCommandBuffers(device, in cbai, out CommandBuffer cmd), "vkAllocateCommandBuffers");

            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Probe.Check(vk.BeginCommandBuffer(cmd, in begin), "vkBeginCommandBuffer");
            vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &set, 0, null);
            vk.CmdDispatch(cmd, (uint)((Dim + 7) / 8), (uint)((Dim + 7) / 8), 1);
            Probe.Check(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var cmdLocal = cmd;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdLocal,
            };
            Probe.Check(vk.QueueSubmit(ctx.ComputeQueue, 1, &submit, default), "vkQueueSubmit");
            Probe.Check(vk.QueueWaitIdle(ctx.ComputeQueue), "vkQueueWaitIdle");

            return Probe.ReadBuffer<int>(ctx, buffers[3], n);
        }
        finally
        {
            if (entryPtr != 0) SilkMarshal.Free(entryPtr);
            if (cmdPool.Handle != 0) vk.DestroyCommandPool(device, cmdPool, null);
            if (pool.Handle != 0) vk.DestroyDescriptorPool(device, pool, null);
            if (pipeline.Handle != 0) vk.DestroyPipeline(device, pipeline, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(device, layout, null);
            if (dsl.Handle != 0) vk.DestroyDescriptorSetLayout(device, dsl, null);
            if (module.Handle != 0) vk.DestroyShaderModule(device, module, null);
            for (int i = 0; i < buffers.Length; i++) Probe.FreeBuffer(ctx, buffers[i]);
        }
    }

    // Self-contained double δ-rebased perturbation kernel. No vk::binding
    // attributes (FXC-compatible); DXC maps registers via -fvk-*-shift. The
    // body is the line-for-line twin of CpuMirrorDouble / ComputePixelPTRebased.
    private static string Hlsl() => """
cbuffer Params : register(b0)
{
    int gW;
    int gH;
    int gMaxIter;
    int gRefLen;
    double gScale;
    double gEscapeR2;
};

StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);
RWStructuredBuffer<int>  gIter  : register(u0);

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)gW || tid.y >= (uint)gH) return;
    int idx = int(tid.y) * gW + int(tid.x);

    double fx = (double)int(tid.x) - 0.5 * (double)gW;
    double fy = (double)int(tid.y) - 0.5 * (double)gH;
    double dcR = fx * gScale;
    double dcI = fy * gScale;

    double dr = 0.0;
    double di = 0.0;
    int m = 0;
    double zr = 0.0;
    double zi = 0.0;

    int iter;
    for (iter = 0; iter < gMaxIter; iter++)
    {
        double Zr = gRefZr[m];
        double Zi = gRefZi[m];
        zr = Zr + dr;
        zi = Zi + di;

        double zmag2 = zr * zr + zi * zi;
        if (zmag2 >= gEscapeR2) break;

        double dmag2 = dr * dr + di * di;
        if (zmag2 < dmag2 || m + 1 >= gRefLen)
        {
            dr = zr; di = zi;
            Zr = 0.0; Zi = 0.0;
            m = 0;
        }

        double a = 2.0 * Zr + dr;
        double b = 2.0 * Zi + di;
        double newDr = a * dr - b * di + dcR;
        double newDi = a * di + b * dr + dcI;
        dr = newDr; di = newDi;
        m++;
    }

    gIter[idx] = iter;
}
""";

    // ── minimal double-double (no FMA — Dekker TwoProduct/TwoSum) ────────────
    private readonly struct Dd
    {
        public readonly double Hi, Lo;
        public Dd(double hi) { Hi = hi; Lo = 0.0; }
        private Dd(double hi, double lo) { Hi = hi; Lo = lo; }

        private static void TwoSum(double a, double b, out double s, out double e)
        {
            s = a + b;
            double bb = s - a;
            e = (a - (s - bb)) + (b - bb);
        }

        private static void Split(double a, out double hi, out double lo)
        {
            double t = 134217729.0 * a;   // 2^27 + 1
            hi = t - (t - a);
            lo = a - hi;
        }

        private static void TwoProd(double a, double b, out double p, out double e)
        {
            p = a * b;
            Split(a, out double ah, out double al);
            Split(b, out double bh, out double bl);
            e = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        }

        public static Dd FromProduct(double a, double b)
        {
            TwoProd(a, b, out double p, out double e);
            return new Dd(p, e);
        }

        public static Dd operator +(Dd a, Dd b)
        {
            TwoSum(a.Hi, b.Hi, out double s, out double e);
            e += a.Lo + b.Lo;
            TwoSum(s, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator -(Dd a, Dd b) => a + new Dd(-b.Hi, -b.Lo);

        public static Dd operator *(Dd a, double b)
        {
            TwoProd(a.Hi, b, out double p, out double e);
            e += a.Lo * b;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator *(Dd a, Dd b)
        {
            TwoProd(a.Hi, b.Hi, out double p, out double e);
            e += a.Hi * b.Lo + a.Lo * b.Hi;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }
    }
}
