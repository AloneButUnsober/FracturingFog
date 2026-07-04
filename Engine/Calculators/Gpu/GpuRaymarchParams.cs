// GpuRaymarchParams.cs
//
// P7 infra — shared camera/ray/light/march struct passed to every per-fractal
// ILGPU kernel under Engine/Calculators/Gpu/. Holds the cross-fractal setup
// (camera basis, viewport, light, sphere-clip radius, march cap, hit epsilon,
// in-set color). Fractal-specific knobs (e.g. Mandelbulb Power, Mandelbox
// scale/min-radius, KIFS iteration count, Kleinian inversion radius) live in
// a per-fractal companion struct beside that fractal's kernel.
//
// Layout note: pass-by-value into the kernel. Keep blittable (no managed refs,
// no auto-properties). Padding-safe since ILGPU marshals by field order.
//
// Pattern reference: UserBulbGpuCalculator.GpuRenderParams is the precedent.
// As per-fractal GPU calculators land, the UserBulb struct will fold into this
// + a UserBulb-specific companion struct.

namespace FracturingFog.Calculators.Gpu;

/// <summary>
/// Per-frame raymarch parameters shared by every GPU fractal kernel.
/// Mirrors the CPU calculator's primary-ray setup: camera basis, viewport,
/// light direction, sphere clip, march cap, hit epsilon, miss color.
/// </summary>
public struct GpuRaymarchParams
{
    /// <summary>Render target width in pixels.</summary>
    public int Width;
    /// <summary>Render target height in pixels.</summary>
    public int Height;

    /// <summary>Camera world position.</summary>
    public double CamX, CamY, CamZ;
    /// <summary>Camera look-at target world position (used for sphere-clip
    /// origin offset, not for rebuilding the basis — that's pre-baked into
    /// Fwd/Right/Up).</summary>
    public double TargetX, TargetY, TargetZ;
    /// <summary>Camera forward (look-at) basis vector. Unit length.</summary>
    public double FwdX, FwdY, FwdZ;
    /// <summary>Camera right basis vector. Unit length.</summary>
    public double RightX, RightY, RightZ;
    /// <summary>Camera up basis vector. Unit length.</summary>
    public double UpX, UpY, UpZ;

    /// <summary><c>tan(0.5 * fovYRadians)</c> — half-height of viewport at unit depth.</summary>
    public double FovScale;
    /// <summary><c>Width / Height</c>.</summary>
    public double Aspect;
    /// <summary>Screen-space pan in NDC-ish units, U axis.</summary>
    public double PanU;
    /// <summary>Screen-space pan in NDC-ish units, V axis (already sign-flipped
    /// to match the CPU drag convention).</summary>
    public double PanV;

    /// <summary>Primary directional light, world-space unit vector.</summary>
    public double LightX, LightY, LightZ;

    /// <summary>Hard cap on sphere-trace steps per pixel.</summary>
    public int MaxSteps;
    /// <summary>Hit threshold — when DE returns less than this, treat as surface.</summary>
    public double Eps;
    /// <summary>Sphere clip radius squared. Rays missing this sphere short-circuit
    /// to <see cref="InSetColor"/> with no DE evaluation. 0 = no clip.</summary>
    public double CullRadiusSq;

    /// <summary>Color written for miss / in-set / sphere-clipped pixels.</summary>
    public uint InSetColor;
}
