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
