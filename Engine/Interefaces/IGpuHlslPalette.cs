// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/IGpuHlslPalette.cs  — T3.1 phase 2
//
// Opt-in capability for IColorMap implementations whose Map() pipeline can
// also run on the GPU. ColorGen-emitted themes implement this automatically;
// hand-written themes may add it if a developer hand-translates the body.
//
// The contract is intentionally small:
//   • HlslSource returns the body of a `float3 EvalPalette(...)` HLSL
//     function (NOT the function signature — the kernel composes that).
//     The signature exposes every DSL input as a float arg in the order
//     declared by GpuPaletteInputOrder below.
//   • HlslPrelude returns any helper/declarations that go BEFORE EvalPalette
//     (cg_palette_N, cg_fromHsv, cg_mods, etc.). Emitted once per shader.
//   • PaletteId uniquely identifies this theme's HLSL source for kernel
//     shader caching — when the id changes, the kernel recompiles. Use a
//     content hash for content-derived identity, or a fixed string per
//     theme type.
//
// Themes that do NOT implement this interface stay on the CPU palette
// path, regardless of whether GPU compute is otherwise enabled.

namespace FracturingFog.Interefaces;

public interface IGpuHlslPalette
{
    /// <summary>HLSL body of `EvalPalette(...)` — let-bindings + final
    /// `return <vec3>;`. Excludes the function signature, braces, and any
    /// helper functions it depends on (those live in <see cref="HlslPrelude"/>).</summary>
    string HlslPaletteBody { get; }

    /// <summary>Helper definitions (cg_palette_N, cg_fromHsv, etc.) emitted
    /// before EvalPalette. Empty string when no helpers needed.</summary>
    string HlslPrelude { get; }

    /// <summary>Stable identifier of this theme's compiled HLSL source.
    /// Same id → kernel reuses cached compiled shader; different id →
    /// recompile. Hash, GUID, or theme type-name all work.</summary>
    string PaletteId { get; }
}

/// <summary>Canonical input order for the HLSL EvalPalette signature.
/// Both the emitter and the kernel use this list verbatim — change here,
/// change there.</summary>
public static class GpuPaletteInputOrder
{
    public static readonly string[] FloatInputs =
    {
        "in_smooth", "in_dist", "in_iter", "in_maxIter",
        "in_t", "in_nx", "in_ny", "in_zr", "in_zi",
        "in_dzr", "in_dzi", "in_arg", "in_mag",
        "in_isInSet", "in_pxScale",
    };
}

// ── F16 (#603) — orbit-accumulator inputs on the GPU. ────────────────────────
//
// An orbit ColorGen theme references per-iteration accumulators (trapMin,
// stripeAvg, …) that the escape-only kernel doesn't compute. A theme that
// wants those on the GPU implements IGpuOrbitPalette in ADDITION to
// IGpuHlslPalette: the kernel then splices a per-iteration sampling loop that
// accumulates ONLY the referenced inputs (the mask), extends EvalPalette with
// the orbit params, and passes the accumulated means at the colour-write site.
//
// Orbit themes advertise no orbit inputs (mask None) → they render on the CPU,
// exactly as before, so this stays inert until a theme opts in. Scope of the
// first slice is the shallow-escape kernel + exterior pixels (see
// MandelbrotKernelSource.BuildColorOrbit); deep-zoom perturbation + interior
// colouring are separate slices.

/// <summary>Which per-iteration orbit accumulators a GPU palette needs. Bit
/// order is the canonical order used by the emitter and the kernel — matches
/// <see cref="GpuOrbitInputOrder.DslNames"/> index-for-index. Keep in lockstep
/// with the CPU <c>InterpretedOrbitColorMap.Sample</c> gates.</summary>
[System.Flags]
public enum GpuOrbitInputs
{
    None          = 0,
    TrapMin       = 1 << 0,
    TrapCross     = 1 << 1,
    TrapRing      = 1 << 2,
    TrapHyperbola = 1 << 3,
    TrapHexagon   = 1 << 4,
    StripeAvg     = 1 << 5,
    TiaAvg        = 1 << 6,
    Curvature     = 1 << 7,
    Lyapunov      = 1 << 8,
    Gaussian      = 1 << 9,
    ExpSmooth     = 1 << 10,
}

/// <summary>Canonical orbit-input order — DSL name per <see cref="GpuOrbitInputs"/>
/// bit, low bit first. The kernel appends an <c>in_&lt;name&gt;</c> float param
/// to EvalPalette for each (always all of them, unused ones passed 0) and gates
/// the per-iteration accumulation code on the mask.</summary>
public static class GpuOrbitInputOrder
{
    public static readonly string[] DslNames =
    {
        "trapMin", "trapCross", "trapRing", "trapHyperbola", "trapHexagon",
        "stripeAvg", "tiaAvg", "curvature", "lyapunov", "gaussian", "expSmooth",
    };

    public static readonly GpuOrbitInputs[] Bits =
    {
        GpuOrbitInputs.TrapMin, GpuOrbitInputs.TrapCross, GpuOrbitInputs.TrapRing,
        GpuOrbitInputs.TrapHyperbola, GpuOrbitInputs.TrapHexagon, GpuOrbitInputs.StripeAvg,
        GpuOrbitInputs.TiaAvg, GpuOrbitInputs.Curvature, GpuOrbitInputs.Lyapunov,
        GpuOrbitInputs.Gaussian, GpuOrbitInputs.ExpSmooth,
    };
}

/// <summary>Opt-in for a GPU palette that also needs per-iteration orbit
/// sampling. The kernel builds an orbit-accumulating variant when
/// <see cref="OrbitInputs"/> is non-<see cref="GpuOrbitInputs.None"/>.</summary>
public interface IGpuOrbitPalette : IGpuHlslPalette
{
    /// <summary>The orbit accumulators this palette's HLSL body reads. The
    /// kernel accumulates exactly these per iteration. <see cref="GpuOrbitInputs.None"/>
    /// ⇒ no GPU orbit (renders on the CPU).</summary>
    GpuOrbitInputs OrbitInputs { get; }
}
