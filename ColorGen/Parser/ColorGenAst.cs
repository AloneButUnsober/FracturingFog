// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ColorGenAst.cs
//
// AST node types + symbol table for the ColorGen DSL. Nodes carry a CgType
// tag (Scalar or Vec3) populated by the parser. Binary operators auto-
// broadcast scalar↔vec3 (Vec3 +-*/% Scalar applies elementwise; comparisons
// require Scalar). The emitter consumes the tagged tree to render C#.
//
// DSL inputs available at runtime (resolved by the emitter to C# locals):
//
//   Scalars
//     smooth     — smooth iteration count at escape
//     dist       — exterior distance estimate (0 for in-set)
//     iter       — iteration count at escape (int → cast to double)
//     maxIter    — max iterations for this frame (int → cast to double)
//     t          — convenience: smooth / max(1, maxIter)
//     nx, ny     — surface normal components in [-1, 1] (0,0 for in-set)
//     zr, zi     — final z at escape
//     dzr, dzi   — final dz/dc at escape
//     arg        — atan2(zi, zr) at escape (radians)
//     mag        — hypot(zr, zi) at escape
//     isInSet    — 1.0 when iter >= maxIter, else 0.0
//
//   Constants
//     pi, tau (= 2π), e (Euler), phi (golden ratio)

using System;
using System.Collections.Generic;

namespace FracturingFog.ColorGen.Parser;

public enum CgType
{
    Scalar,
    Vec3,
}

public abstract class CgNode
{
    public CgType Type { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class CgNumber : CgNode
{
    public double Value { get; init; }
}

/// <summary>Reference to a built-in input or a let-bound local.</summary>
public sealed class CgVar : CgNode
{
    public string Name { get; init; } = "";
    /// <summary>True when the name resolves to a built-in input; false for
    /// let-bound locals. Both emit as C# identifiers but the emitter uses
    /// distinct prefixes ("in_" vs "v_") so user names cannot collide with
    /// built-ins.</summary>
    public bool IsBuiltIn { get; init; }
}

public enum CgBinOp { Add, Sub, Mul, Div, Mod, Pow, Lt, Le, Gt, Ge, Eq, Ne, And, Or }

public sealed class CgBinary : CgNode
{
    public CgBinOp Op { get; init; }
    public CgNode Lhs { get; init; } = null!;
    public CgNode Rhs { get; init; } = null!;
}

public enum CgUnaryOp { Neg, Pos, Not }

public sealed class CgUnary : CgNode
{
    public CgUnaryOp Op { get; init; }
    public CgNode Operand { get; init; } = null!;
}

public sealed class CgTernary : CgNode
{
    public CgNode Cond { get; init; } = null!;
    public CgNode IfTrue { get; init; } = null!;
    public CgNode IfFalse { get; init; } = null!;
}

/// <summary>Channel access on a Vec3: .r .g .b. Result type Scalar.</summary>
public sealed class CgChannel : CgNode
{
    public CgNode Target { get; init; } = null!;
    public char Channel { get; init; } // 'r' | 'g' | 'b'
}

public sealed class CgCall : CgNode
{
    public string Name { get; init; } = "";
    public List<CgNode> Args { get; init; } = new();
}

public abstract class CgStmt
{
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class CgLet : CgStmt
{
    public string Name { get; init; } = "";
    public CgNode Value { get; init; } = null!;
}

public sealed class CgReturn : CgStmt
{
    public CgNode Value { get; init; } = null!;
}

public sealed class CgProgram
{
    public List<CgStmt> Statements { get; } = new();
    /// <summary>Convenience handle to the final return statement (validated
    /// by the parser — exactly one, must be the last statement).</summary>
    public CgReturn? Return { get; set; }
}

/// <summary>
/// Names of the built-in scalar inputs the host injects into the rendered
/// Map() body. Recognised by the parser when resolving identifiers and
/// reproduced verbatim by the emitter (prefixed with "in_" inside the
/// generated method so user `let` bindings cannot clobber them).
/// </summary>
public static class CgInputs
{
    public static readonly HashSet<string> Scalars = new(StringComparer.Ordinal)
    {
        "smooth", "dist", "iter", "maxIter", "t",
        "nx", "ny", "zr", "zi", "dzr", "dzi",
        "arg", "mag", "isInSet", "pxScale",
        // F15 (#591) — orbit-accumulator inputs. A program referencing any of
        // these becomes orbit-aware (the host samples the orbit per iteration
        // and binds these at escape); CPU-only (the GPU palette is disabled).
        "trapMin", "stripeAvg", "tiaAvg",
    };

    /// <summary>Subset of <see cref="Scalars"/> that require per-iteration orbit
    /// sampling (F15). A ColorGen program referencing any of these is rendered
    /// through the orbit-aware interpreter path.</summary>
    public static readonly HashSet<string> OrbitScalars = new(StringComparer.Ordinal)
    {
        "trapMin", "stripeAvg", "tiaAvg",
    };

    public static readonly Dictionary<string, double> Constants = new(StringComparer.Ordinal)
    {
        ["pi"]  = Math.PI,
        ["tau"] = Math.PI * 2.0,
        ["e"]   = Math.E,
        ["phi"] = 1.6180339887498949,
    };
}

/// <summary>
/// Signature table for built-in functions. ArgArity is the minimum count;
/// IsVariadic == true allows extra trailing args (palette stops). RetType
/// is the call's result type. When ArgTypeOverrides is non-empty it pins
/// individual argument slots to a required type — anything else
/// auto-broadcasts (Vec3 over Scalar) at the call site.
/// </summary>
public readonly record struct CgFnSig(
    int ArgArity,
    bool IsVariadic,
    CgType RetType,
    CgType[]? RequiredArgTypes = null);

public static class CgFunctions
{
    // Scalar → Scalar
    private static readonly CgFnSig SS  = new(1, false, CgType.Scalar);
    private static readonly CgFnSig SSS = new(2, false, CgType.Scalar);
    private static readonly CgFnSig SSSS = new(3, false, CgType.Scalar);

    public static readonly Dictionary<string, CgFnSig> Table = new(StringComparer.Ordinal)
    {
        // Trig / exp / log — scalar.
        ["sin"]   = SS, ["cos"] = SS, ["tan"] = SS,
        ["asin"]  = SS, ["acos"] = SS, ["atan"] = SS,
        ["sinh"]  = SS, ["cosh"] = SS, ["tanh"] = SS,
        ["exp"]   = SS, ["log"] = SS, ["log2"] = SS, ["log10"] = SS,
        ["sqrt"]  = SS, ["abs"] = SS, ["sign"] = SS,
        ["floor"] = SS, ["ceil"] = SS, ["round"] = SS, ["fract"] = SS,
        ["saturate"] = SS,
        ["radians"] = SS, ["degrees"] = SS,
        // Two-arg scalar.
        ["atan2"] = SSS, ["hypot"] = SSS,
        ["min"]   = SSS, ["max"] = SSS, ["mod"] = SSS,
        ["pow"]   = SSS, ["step"] = SSS,
        // Three-arg scalar.
        ["clamp"] = SSSS, ["smoothstep"] = SSSS,
        // mix: (Scalar a, Scalar b, Scalar t) → Scalar; OR (Vec3, Vec3, Scalar) → Vec3.
        // Modeled as variable: see resolution in parser.
        ["mix"]   = new CgFnSig(3, false, CgType.Scalar /* refined by parser */),
        // Hashes (one and two scalar args → scalar).
        ["hash"]  = SS, ["hash2"] = SSS,
        // Vec3 constructors (3-scalar → Vec3).
        ["rgb"]   = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Scalar, CgType.Scalar, CgType.Scalar }),
        ["hsv"]   = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Scalar, CgType.Scalar, CgType.Scalar }),
        ["hsl"]   = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Scalar, CgType.Scalar, CgType.Scalar }),
        // OkLab / OkLCh constructors (Phase C / F9) — perceptually uniform.
        //   oklab(L, a, b): L in [0,1], a/b roughly [-0.4,0.4] → sRGB Vec3.
        //   oklch(L, C, h): h in RADIANS (a = C·cos h, b = C·sin h).
        // Both convert OkLab→linear sRGB→gamma-encoded sRGB (packer-ready).
        ["oklab"] = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Scalar, CgType.Scalar, CgType.Scalar }),
        ["oklch"] = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Scalar, CgType.Scalar, CgType.Scalar }),
        // mix_oklab(va, vb, t): blend two sRGB Vec3s through OkLab — smooth
        // mid-tones between distant hues (no muddy grey crossing).
        ["mix_oklab"] = new CgFnSig(3, false, CgType.Vec3, new[] { CgType.Vec3, CgType.Vec3, CgType.Scalar }),
        // palette(t, c0, c1, …) — variadic vec3 stops, cyclic interpolation.
        ["palette"] = new CgFnSig(3, true, CgType.Vec3),
        // cosine(t, a, b, c, d) — Inigo Quilez cosine palette:
        //   colour = a + b * cos(tau * (c*t + d))
        // a,b,c,d are Vec3 coefficient vectors; t is scalar. Standard in
        // shader-fractal tools; produces smooth cyclic gradients with no stops.
        ["cosine"] = new CgFnSig(5, false, CgType.Vec3,
            new[] { CgType.Scalar, CgType.Vec3, CgType.Vec3, CgType.Vec3, CgType.Vec3 }),
        // Brightness / contrast / gamma — (Vec3, Scalar) → Vec3.
        ["brightness"] = new CgFnSig(2, false, CgType.Vec3, new[] { CgType.Vec3, CgType.Scalar }),
        ["contrast"]   = new CgFnSig(2, false, CgType.Vec3, new[] { CgType.Vec3, CgType.Scalar }),
        ["gamma"]      = new CgFnSig(2, false, CgType.Vec3, new[] { CgType.Vec3, CgType.Scalar }),
    };
}
