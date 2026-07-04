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
                _accelerator = _context.GetPreferredDevice(preferCPU: false).CreateAccelerator(_context);
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
