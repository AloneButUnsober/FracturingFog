// Abstractions/Help/IHardwareInfoProvider.cs
//
// Phase X.0 / Slice 0.3b — injected dependency for the OS-specific
// hardware info that lights up the Help -> Hardware tab. Lets
// HostHelpContentProvider live in the cross-platform Hosting assembly
// without referencing Vortice DXGI / D3D11 directly; the Windows-only
// WindowsD3D11HardwareInfoProvider implementation ships in
// FracturingFog.Rendering.D3D and the host bootstrap installs it.

using System.Text;

namespace FracturingFog.Help
{
    /// <summary>
    /// OS / backend-specific helper that appends GPU enumeration text to
    /// the Hardware tab. Implementations write into the supplied
    /// StringBuilder; HostHelpContentProvider handles section headers,
    /// CPU/memory/SIMD fields, and falls back to "not available" text
    /// when no provider is installed.
    /// </summary>
    public interface IHardwareInfoProvider
    {
        /// <summary>Append per-adapter info — vendor / device IDs, VRAM,
        /// driver-side names. Caller already emitted the "=== GPU Adapters
        /// ===" header. Implementations should swallow exceptions and
        /// surface a one-line fallback rather than throw.</summary>
        void AppendGpuAdapters(StringBuilder sb);

        /// <summary>Append max GPU feature level / API version. Caller
        /// already emitted the section header.</summary>
        void AppendGpuFeatureLevel(StringBuilder sb);

        /// <summary>True when the machine has a discrete GPU (as opposed to an
        /// integrated one). Feeds the Animation Roadmap Phase 6 animated-param
        /// ceiling — a discrete GPU raises the 3D ceiling. Default returns
        /// false (conservative — assume an iGPU) so non-Windows / non-D3D
        /// providers need not implement it.</summary>
        bool HasDiscreteGpu() => false;
    }
}
