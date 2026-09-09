// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuAcceleratorHost.cs
//
// P7 infra — process-wide ILGPU Context + Accelerator lifecycle. Per-fractal
// GPU calculators borrow the singleton accelerator instead of each
// constructing and disposing their own (which is what UserBulbGpuCalculator
// does today — fine for one-of-a-kind, wasteful once 7 calculators all do it).
//
// Lazy init on first request. Init failure latched — subsequent callers see
// the same TryAcquire == false result without re-attempting (and without
// re-spamming the error). Caller-side: GPU calculators wrap their kernel
// loads in try/catch + LastError so a per-kernel JIT failure doesn't poison
// the whole host.
//
// Dispose called from AppDomain.ProcessExit / AvaloniaShell shutdown.
// Idempotent — safe to call twice.

using System;

using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;

namespace FracturingFog.Calculators.Gpu;

/// <summary>
/// Process-wide ILGPU accelerator owner. GPU calculators acquire the shared
/// accelerator via <see cref="TryAcquire"/> rather than creating their own
/// — keeps device count to one regardless of how many fractal-specific GPU
/// kernels are loaded.
/// </summary>
public static class GpuAcceleratorHost
{
    private static readonly object _lock = new();
    private static Context? _context;
    private static Accelerator? _accelerator;
    private static bool _initAttempted;
    private static bool _initFailed;

    // Test-only override. When set, TryAcquire returns this accelerator instead
    // of the lazily-probed process default, letting the #742/#749 tests pin the
    // GPU 3D kernels to a specific device (ILGPU CPU vs a discrete GPU). The test
    // owns the accelerator's lifetime; Clear resets so later callers re-probe.
    // Never set on any production path.
    //
    // [ThreadStatic] so the override is scoped to the test thread that set it:
    // xUnit runs test classes in parallel, and a process-global override would
    // bleed into concurrent GPU tests on other threads, forcing them to JIT +
    // run the heavy kernels on the CPU accelerator (wedging the suite). The
    // override tests call the calculators synchronously on their own thread, so
    // thread-scoping still delivers the override where it is needed.
    [ThreadStatic]
    private static Accelerator? _testOverride;

    /// <summary>Last init failure message, empty when no failure has been
    /// recorded.</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>Try to acquire the process-wide accelerator. Returns true and
    /// sets <paramref name="accelerator"/> on success. Returns false on init
    /// failure (no GPU, no compatible driver, OOM during context create);
    /// callers should fall back to CPU. The first failure is latched — repeat
    /// calls return false immediately without retrying.</summary>
    public static bool TryAcquire(out Accelerator accelerator)
    {
        lock (_lock)
        {
            if (_testOverride != null)
            {
                accelerator = _testOverride;
                return true;
            }
            if (_accelerator != null)
            {
                accelerator = _accelerator;
                return true;
            }
            if (_initFailed)
            {
                accelerator = null!;
                return false;
            }
            if (_initAttempted)
            {
                // Init in progress on another path / partial init — treat as failure.
                accelerator = null!;
                return false;
            }
            _initAttempted = true;
            try
            {
                _context = Context.Create(b => b.Default());
                // Real fp64 GPU only. No such device -> fail so callers use the
                // CPU ShadingPipeline instead of JIT-ing the kernels on the CPU
                // accelerator (#749 — too slow, lower quality than the pipeline).
                var dev = SelectFloat64Device(_context, allowCpu: false);
                if (dev == null)
                {
                    LastError = "no Float64-capable GPU device; using CPU pipeline";
                    _initFailed = true;
                    _context.Dispose();
                    _context = null;
                    accelerator = null!;
                    return false;
                }
                _accelerator = dev.CreateAccelerator(_context);
                accelerator = _accelerator;
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"GPU accelerator init failed: {ex.Message}";
                _initFailed = true;
                _context?.Dispose();
                _context = null;
                _accelerator = null;
                accelerator = null!;
                return false;
            }
        }
    }

    /// <summary>True when <paramref name="d"/> can run our double-precision
    /// kernels. Every GPU 3D-fractal kernel is Float64 (double); an OpenCL device
    /// that lacks fp64 (integrated Intel/AMD iGPUs) throws
    /// <c>CapabilityNotSupportedException: Float64 … not supported</c> at JIT and
    /// the whole GPU path silently falls back to the CPU (#749). CPU and CUDA
    /// devices always provide fp64 — only OpenCL advertises it as optional, so
    /// only that backend is gated.</summary>
    internal static bool SupportsFloat64(Device d)
    {
        try { return d.Capabilities is not CLCapabilityContext cl || cl.Float64; }
        catch { return true; } // CPU device: Capabilities not populated, fp64 always available.
    }

    /// <summary>Pick the accelerator device our fp64 kernels can actually run on.
    /// Prefers a Float64-capable non-CPU device — a real GPU. Never returns an
    /// fp64-less device (the #749 bug that let <c>GetPreferredDevice(preferCPU:false)</c>
    /// choose an Intel iGPU without double support). When <paramref name="allowCpu"/>
    /// is true the CPU device (always fp64) is an accepted fallback; when false
    /// the method returns <c>null</c> so the caller can drop to its own CPU code
    /// path instead of JIT-ing these heavy raymarch kernels on the ILGPU CPU
    /// accelerator — for the 3D-fractal families that CPU path is the full CPU
    /// <c>ShadingPipeline</c>, which is both faster (no per-kernel JIT) and higher
    /// quality than the GPU kernel would be on a CPU accelerator.</summary>
    internal static Device? SelectFloat64Device(Context ctx, bool allowCpu)
    {
        Device? cpu = null;
        Device? gpu = null;
        foreach (var d in ctx.Devices)
        {
            if (!SupportsFloat64(d)) continue;
            if (d.AcceleratorType == AcceleratorType.CPU) cpu ??= d;
            else gpu ??= d;
        }
        return gpu ?? (allowCpu ? cpu : null);
    }

    /// <summary>Test-only. Pin <see cref="TryAcquire"/> to <paramref name="acc"/>
    /// (or clear with null) so the #742 drift-bound test can run the GPU 3D
    /// kernels on a chosen device. The caller owns the accelerator's lifetime.
    /// Not for production use.</summary>
    public static void SetTestOverride(Accelerator? acc)
    {
        lock (_lock) _testOverride = acc;
    }

    /// <summary>True when the accelerator was acquired successfully at least
    /// once. Used by GPU calculators to decide whether to attempt kernel
    /// load without going through TryAcquire's lock.</summary>
    public static bool IsReady
    {
        get { lock (_lock) return _accelerator != null; }
    }

    /// <summary>Dispose the shared accelerator + context. Idempotent. Reset
    /// the init-attempted latch so a subsequent TryAcquire can re-probe (e.g.
    /// after a driver hot-swap during a long-running headless session).</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _accelerator?.Dispose();
            _context?.Dispose();
            _accelerator = null;
            _context = null;
            _initAttempted = false;
            _initFailed = false;
            LastError = string.Empty;
        }
    }
}
