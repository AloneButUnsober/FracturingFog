// #1668 (epic #1667, S1) — the catalog of Fracturing Fog's FF_* environment switches.
//
// One entry per switch the source reads. EnvVarCatalogTests scans every .cs file for
// "FF_..." string literals and fails when one has no entry here, or an entry is no
// longer read anywhere — the same posture as BatchFlagCatalog (#64). The Control
// Center Environment panel (#1669), the Restart handoff (#1670), safe mode (#1671)
// and the Command builder's environment prefix (#1672) are built on it.
//
// Most switches are read once (static readonly fields, type initialisers), so a
// change takes effect on the next launch; RequiresRestart says so per entry.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Cli
{
    /// <summary>How a switch's value is written.</summary>
    public enum EnvVarKind
    {
        /// <summary>On / off: FF writes "1" or "0".</summary>
        Toggle,
        /// <summary>A number in [Min, Max].</summary>
        Number,
        /// <summary>One of <see cref="EnvVarEntry.Choices"/>.</summary>
        Choice,
        /// <summary>Free text (a path, a list).</summary>
        Text,
    }

    /// <summary>Panel grouping.</summary>
    public enum EnvVarGroup
    {
        Renderer,
        GpuCompute,
        GpuDeepZoom,
        Vulkan,
        DeepZoomCpu,
        LinearApproximation,
        Denoise,
        Security,
        Diagnostics,
        Probe,
        Test,
    }

    /// <summary>What can go wrong when the switch is changed from its default.</summary>
    public enum EnvVarRisk
    {
        None,
        /// <summary>Slower, or a different render route; nothing breaks.</summary>
        Caution,
        /// <summary>Can hang or reset the GPU driver, or weakens a safety policy.</summary>
        Danger,
    }

    /// <summary>One FF_* switch.</summary>
    /// <param name="Default">The behaviour when unset, in the switch's own terms
    /// ("on", "off", "auto", a number).</param>
    /// <param name="DefaultOn">Toggles only: the switch's behaviour when unset.</param>
    /// <param name="Developer">Probe / test / diagnostic switch: hidden behind the
    /// panel's Advanced toggle.</param>
    /// <param name="AffectsRender">Changes the render route or output, so a batch
    /// command must carry it (#1672).</param>
    public sealed record EnvVarEntry(
        string Name,
        EnvVarKind Kind,
        EnvVarGroup Group,
        string Description,
        string Default,
        string? Issue = null,
        bool DefaultOn = false,
        IReadOnlyList<string>? Choices = null,
        double? Min = null,
        double? Max = null,
        EnvVarRisk Risk = EnvVarRisk.None,
        string? Warning = null,
        bool Developer = false,
        bool AffectsRender = false,
        bool RequiresRestart = true);

    public static class EnvVarCatalog
    {
        private static readonly string[] AutoGpuCpu = ["auto", "gpu", "cpu"];

        private static EnvVarEntry Probe(string name, string probe, string what, string? issue = null) =>
            new(name, EnvVarKind.Text, EnvVarGroup.Probe, $"{probe}: {what}", "unset", issue, Developer: true);

        public static readonly IReadOnlyList<EnvVarEntry> All =
        [
            // ── Renderer ────────────────────────────────────────────────────────
            new("FF_FORCE_D3D11", EnvVarKind.Toggle, EnvVarGroup.Renderer,
                "Use the Direct3D 11 presenter even where Direct3D 12 would be picked.", "off", "#375"),
            new("FF_PREFER_D3D12", EnvVarKind.Toggle, EnvVarGroup.Renderer,
                "Prefer the Direct3D 12 presenter (opt-in; D3D11 is the default).", "off", "#375",
                Risk: EnvVarRisk.Caution, Warning: "D3D12 is opt-in because of past instability (#375)."),
            new("FF_NO_SHADER_CACHE", EnvVarKind.Toggle, EnvVarGroup.Renderer,
                "Bypass the compiled-shader blob cache: every shader compiles from source.", "off", "#456",
                Risk: EnvVarRisk.Caution, Warning: "Slower startup and first frames."),

            // ── GPU compute (shallow + general) ─────────────────────────────────
            new("FF_GPU_ORBIT", EnvVarKind.Toggle, EnvVarGroup.GpuCompute,
                "Run interpreted orbit colouring on the GPU.", "on", DefaultOn: true, AffectsRender: true),
            new("FF_GPU_BUDDHA", EnvVarKind.Choice, EnvVarGroup.GpuCompute,
                "Where Buddhabrot-family sampling runs.", "auto", Choices: AutoGpuCpu, AffectsRender: true),
            new("FF_GPU_MIRROR_SPHERES", EnvVarKind.Choice, EnvVarGroup.GpuCompute,
                "Where the Wada mirror-spheres tracer runs.", "auto", "#1439", Choices: AutoGpuCpu, AffectsRender: true),
            new("FF_BUDDHA_DISPATCH_BUDGET", EnvVarKind.Number, EnvVarGroup.GpuCompute,
                "Orbit iterations in the first Buddhabrot GPU dispatch; later dispatches are sized from measured time.",
                "400000000", Min: 1, Developer: true),
            new("FF_GPU_DEFER_READBACK", EnvVarKind.Toggle, EnvVarGroup.GpuCompute,
                "Read iteration / smooth / final-z buffers back from the GPU only when needed.", "on", "#1332", DefaultOn: true),
            new("FF_GPU_PERIODICITY", EnvVarKind.Toggle, EnvVarGroup.GpuCompute,
                "Periodicity checking in the GPU kernels (compiled variant).", "on", "#1342", DefaultOn: true),
            new("FF_GPU_WAVEFRONT", EnvVarKind.Toggle, EnvVarGroup.GpuCompute,
                "Wavefront (work-queue) scheduling in the GPU kernels.", "on", "#1340", DefaultOn: true),
            new("FF_GPU_WAVEFRONT_PERTURB", EnvVarKind.Choice, EnvVarGroup.GpuCompute,
                "Wavefront scheduling for the GPU perturbation kernels: unset = auto, 0 = off, 1 = on.", "auto", "#1384",
                Choices: ["auto", "0", "1"]),
            new("FF_GPU_WAVEFRONT_SEED", EnvVarKind.Toggle, EnvVarGroup.GpuCompute,
                "Seed the wavefront schedule from a preview pass.", "off", "#1386"),
            new("FF_GPU_WAVEFRONT_THREADS", EnvVarKind.Number, EnvVarGroup.GpuCompute,
                "Persistent thread count of the wavefront schedule.", "262144", "#1340", Min: 1, Developer: true),
            new("FF_GPU_GROUP_SHAPE", EnvVarKind.Text, EnvVarGroup.GpuCompute,
                "Override the autotuned thread-group shapes (format: see GpuGroupShape.cs).", "auto", "#1339",
                Developer: true),

            // ── GPU deep zoom ───────────────────────────────────────────────────
            new("FF_GPU_PERTURB", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "Run deep-zoom perturbation on the GPU (falls back to the CPU when the GPU is too slow).", "off", "#82",
                Risk: EnvVarRisk.Caution, Warning: "On a GPU with weak fp64 this is slower than the CPU (#1663).",
                AffectsRender: true),
            new("FF_GPU_PERTURB_PRECISION", EnvVarKind.Choice, EnvVarGroup.GpuDeepZoom,
                "GPU deep-zoom arithmetic: auto (fp64 where present), double, or fp32 (scaled fp32 / float-exp).", "auto",
                "#1331", Choices: ["auto", "double", "fp32"], AffectsRender: true),
            new("FF_GPU_FP32_MIN_ULPS", EnvVarKind.Number, EnvVarGroup.GpuDeepZoom,
                "Minimum fp32 ulps per pixel before the fp32 deep kernels may run; 0 turns the check off.", "8", "#1331",
                Min: 0, AffectsRender: true),
            new("FF_GPU_LA", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "GPU deep frames that want LA run the LA stage kernel instead of SA / BLA.", "off", "#1630",
                Risk: EnvVarRisk.Caution, Warning: "Slower than SA / BLA on the GPUs measured so far.", AffectsRender: true),
            new("FF_D3D_LA", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "Allow the LA stage kernel on Direct3D 11.", "off", "#1661",
                Risk: EnvVarRisk.Danger, Warning: "Hung the NVIDIA D3D11 driver (TDR) on a GT 710: can reset the GPU driver."),
            new("FF_GPU_SA", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "Series approximation in the GPU perturbation kernel.", "on", DefaultOn: true, AffectsRender: true),
            new("FF_GPU_BLA", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "Bilinear approximation in the GPU perturbation kernel.", "on", DefaultOn: true, AffectsRender: true),
            new("FF_GPU_DEEP_ORBIT", EnvVarKind.Toggle, EnvVarGroup.GpuDeepZoom,
                "GPU deep orbit-colouring kernel (with GPU perturbation on).", "on", DefaultOn: true, AffectsRender: true),
            new("FF_GPU_PERTURB_BUDGET_MS", EnvVarKind.Number, EnvVarGroup.GpuDeepZoom,
                "Projected GPU deep-frame time above which the dispatch aborts and the CPU takes over; 0 disables the check.",
                "3000", "#1490", Min: 0,
                Risk: EnvVarRisk.Caution, Warning: "0 or a large value lets a slow GPU run into the driver's TDR limit."),
            new("FF_PERTURB_BANDROWS", EnvVarKind.Number, EnvVarGroup.GpuDeepZoom,
                "Force the GPU perturbation band height; 0 = auto (from the time budget).", "0", "#1334", Min: 0,
                Developer: true),

            // ── Vulkan ──────────────────────────────────────────────────────────
            new("FF_VULKAN_MEMORY", EnvVarKind.Choice, EnvVarGroup.Vulkan,
                "Vulkan buffer placement: auto, host (all host memory), device-outputs (outputs in VRAM, staged).", "auto",
                "#1228", Choices: ["auto", "host", "device-outputs"]),
            new("FF_VK_PIPELINE_CACHE", EnvVarKind.Toggle, EnvVarGroup.Vulkan,
                "Persist the Vulkan pipeline cache between runs.", "on", "#1341", DefaultOn: true),
            new("FF_VULKAN_SERIAL_BANDS", EnvVarKind.Toggle, EnvVarGroup.Vulkan,
                "Wait for the queue to go idle after every band (the pre-#1334 behaviour).", "off", "#1334",
                Developer: true),
            new("FF_VULKAN_ALLOW_CPU", EnvVarKind.Toggle, EnvVarGroup.Vulkan,
                "Allow a CPU Vulkan device (lavapipe / llvmpipe) to run the compute kernels; for testing only.", "off",
                Risk: EnvVarRisk.Caution, Warning: "Slower than FF's CPU path.", Developer: true),

            // ── Deep zoom (CPU) ─────────────────────────────────────────────────
            new("FF_PERIODIC_REFERENCE", EnvVarKind.Toggle, EnvVarGroup.DeepZoomCpu,
                "Stop a periodic reference orbit at its period instead of the iteration cap.", "on", "#1614",
                DefaultOn: true, AffectsRender: true),

            // ── Linear approximation (LA) ───────────────────────────────────────
            new("FF_LA", EnvVarKind.Choice, EnvVarGroup.LinearApproximation,
                "Override the Deep zoom → Evaluator setting for every calculator: unset = per-view setting, 0 = off, 1 = on.",
                "auto", "#1629", Choices: ["auto", "0", "1"], AffectsRender: true),
            new("FF_LA_AT", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "Approximation transform (AT) before the LA stages.", "on", "#1627", DefaultOn: true, AffectsRender: true),
            new("FF_LA_AT_LOOSEN", EnvVarKind.Number, EnvVarGroup.LinearApproximation,
                "AT tolerance scale s ≥ 1 (1 = Imagina's AT).", "1", "#1627", Min: 1, Developer: true, AffectsRender: true),
            new("FF_LA_AT_OFFSET_BITS", EnvVarKind.Number, EnvVarGroup.LinearApproximation,
                "AT precision margin in bits (0 = FF's margin; Imagina uses 16).", "0", "#1627", Min: 0,
                Developer: true, AffectsRender: true),
            new("FF_LA_CLIMB", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "After a rebase, climb back to the top LA stage.", "on", "#1626", DefaultOn: true, AffectsRender: true),
            new("FF_LA_PERIOD", EnvVarKind.Choice, EnvVarGroup.LinearApproximation,
                "Period test the LA stages are cut with: default (threshold dip) or minmag (Imagina's minimum-|Z|).",
                "default", "#1626", Choices: ["default", "minmag"], Developer: true, AffectsRender: true),
            new("FF_LA_TAIL_BLA", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "BLA in the LA evaluator's plain tail.", "off", "#1626", Developer: true, AffectsRender: true),
            new("FF_LA_SIMD", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "4-lane AVX2 LA tail (where AVX2 is present).", "off", "#1628",
                Risk: EnvVarRisk.Caution, Warning: "Gained nothing on the CPU measured (#1663)."),
            new("FF_LA_GATHER", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "AVX2 gathers for the reference in the SIMD LA tail; for measuring.", "off", "#1628", Developer: true),
            new("FF_LA_STAGES", EnvVarKind.Toggle, EnvVarGroup.LinearApproximation,
                "Build the LA stages with the reference orbit even when no LA frame needs them.", "off", "#1625",
                Developer: true),

            // ── Denoise ─────────────────────────────────────────────────────────
            new("FF_OIDN_DISABLE", EnvVarKind.Toggle, EnvVarGroup.Denoise,
                "Treat Intel Open Image Denoise as absent.", "off", "#1338"),
            new("FF_OIDN_DIR", EnvVarKind.Text, EnvVarGroup.Denoise,
                "Folder to look for the Intel Open Image Denoise libraries in, before the default places.", "unset",
                "#1338"),

            // ── Security ────────────────────────────────────────────────────────
            new("FF_ROSLYN_USERCODE", EnvVarKind.Choice, EnvVarGroup.Security,
                "Which origins may compile raw C# user code.", "trusted-only",
                Choices: ["trusted-only", "allow-all", "deny-all"],
                Risk: EnvVarRisk.Danger, Warning: "allow-all lets externally loaded files compile and run C#."),

            // ── Diagnostics ─────────────────────────────────────────────────────
            new("FF_GPU_PERTURB_DEBUG", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Log why each deep frame did or did not take the GPU perturbation path (%TEMP%/ff_gpu_perturb_86.log).",
                "off", "#86", Developer: true),
            new("FF_INTERACTION_TRACE", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Trace each drag from pointer move to frame on screen (%TEMP%/ff_interaction_trace.csv; Tools/InteractionTrace analyses it).",
                "off", "#1762", Developer: true),
            new("FF_GL_DEBUG", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Print OpenGL errors and first-frame stats (Silk renderer).", "off", Developer: true),
            new("FF_ROSLYN_DEBUG", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Diagnostics for the Roslyn hot-load compile path.", "off", Developer: true),
            new("FF_LEAK_DEBUG", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Periodic memory / leak diagnostics in the render host.", "off", Developer: true),
            new("FF_LEAK_DEBUG_EVERY", EnvVarKind.Number, EnvVarGroup.Diagnostics,
                "Frames between leak-diagnostic reports.", "30", Min: 1, Developer: true),
            new("FF_LEAK_DEBUG_FORCEGC", EnvVarKind.Toggle, EnvVarGroup.Diagnostics,
                "Force a garbage collection before each leak-diagnostic report.", "off", Developer: true),

            // ── Probes (--xxxprobe measurement programs, Probes/) ───────────────
            Probe("FF_DEEPCOST_DUMP", "--deepcostprobe", "A/B dump target.", "#1613"),
            Probe("FF_DEEPCOST_EPS", "--deepcostprobe", "tolerance option.", "#1613"),
            Probe("FF_DEEPCOST_FFVIEWS", "--deepcostprobe", "run the built-in FF deep views.", "#1629"),
            Probe("FF_DEEPCOST_KFR", "--deepcostprobe", "folder of Kalles Fraktaler .kfr locations to measure.", "#1615"),
            Probe("FF_DEEPCOST_LA_AB", "--deepcostprobe", "A/B the same frame through the LA evaluator.", "#1626"),
            Probe("FF_DEEPCOST_LA_SIMD_AB", "--deepcostprobe", "A/B the scalar against the 4-lane LA tail.", "#1628"),
            Probe("FF_DEEPCOST_NOREF", "--deepcostprobe", "reference-orbit option.", "#1613"),
            Probe("FF_DEEPCOST_PERIODIC_AB", "--deepcostprobe", "A/B the periodic reference against the full-length one.", "#1614"),
            Probe("FF_DEEPCOST_REGIONS", "--deepcostprobe", "regions JSON file to measure.", "#1629"),
            Probe("FF_DEEPCOST_REPS", "--deepcostprobe", "repetitions per view (default 2).", "#1613"),
            Probe("FF_DEEPCOST_SCENE", "--deepcostprobe", "scene filter.", "#1613"),
            Probe("FF_DF_CASES", "--distancefieldprobe", "case filter.", "#1336"),
            Probe("FF_DF_PNG", "--distancefieldprobe", "write PNGs.", "#1336"),
            Probe("FF_DYNRES_TRACE", "--dynamicresolutionprobe", "trace option.", "#1333"),
            Probe("FF_DYNRES_VIEW", "--dynamicresolutionprobe", "substring of a view name to run alone.", "#1333"),
            Probe("FF_GPULA_BACKEND", "--gpulaprobe", "backend filter (D3D11 / Vulkan).", "#1630"),
            Probe("FF_GPULA_MODES", "--gpulaprobe", "mode filter.", "#1630"),
            Probe("FF_GPULA_REPS", "--gpulaprobe", "repetitions.", "#1630"),
            Probe("FF_GPULA_SCENE", "--gpulaprobe", "scene filter.", "#1630"),
            Probe("FF_GPULA_WARP", "--gpulaprobe", "use the software D3D11 device (WARP, no TDR).", "#1630"),
            Probe("FF_LAPROBE_BITS", "--laprobe", "reference precision in bits (default 320).", "#1624"),
            Probe("FF_LAPROBE_CLIMB", "--laprobe", "climb-back option.", "#1624"),
            Probe("FF_LAPROBE_SAMPLES", "--laprobe", "sample count.", "#1624"),
            Probe("FF_LAPROBE_SCENE", "--laprobe", "scene filter.", "#1624"),
            Probe("FF_OIDN_AUX3D", "--oidnprobe", "1 = 3D auxiliary-buffer pass.", "#1338"),
            Probe("FF_OIDN_PNG", "--oidnprobe", "write PNGs.", "#1338"),
            Probe("FF_SA_TOL", "Vulkan smoke perturbation SA probe", "series-approximation tolerance."),
            Probe("FF_SOLID_CASES", "--solidregionprobe", "case filter.", "#1343"),
            Probe("FF_SOLID_GPU", "--solidregionprobe", "1 = run the GPU variant.", "#1394"),
            Probe("FF_SOLID_VIEW", "--solidregionprobe", "substring of a view name to run alone.", "#1343"),

            // ── Tests ───────────────────────────────────────────────────────────
            new("FF_OIDN_ZIP", EnvVarKind.Text, EnvVarGroup.Test,
                "Path to the official OIDN release zip for the OIDN tests (skipped without it).", "unset", "#1338",
                Developer: true),
        ];

        private static readonly Dictionary<string, EnvVarEntry> s_byName =
            All.ToDictionary(e => e.Name, StringComparer.Ordinal);

        public static EnvVarEntry? Find(string name) => s_byName.GetValueOrDefault(name);

        /// <summary>The switches shown without the Advanced toggle.</summary>
        public static IEnumerable<EnvVarEntry> UserFacing => All.Where(e => !e.Developer);
    }
}
