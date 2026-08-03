// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EmitterCommon.cs
//
// Shared types used by ScalarEmitter, Avx2Emitter and PerturbationEmitter.
//
// Each emitter walks an AstNode tree and returns one ComplexExpr per node
// — a (Re, Im) pair of C# expressions plus an ImZero flag. The string
// form is decided by the concrete emitter (a `double` literal vs a
// `Vector256<double>` ctor, scalar `*` vs `Fma.MultiplyAdd`, …). The
// shared tree walker lives here; per-target string formatting lives in
// the subclass.
//
// AST node bindings provided by the concrete emitter:
//   ZRef       → (ZRe, ZIm)         — current iterate or reference orbit
//   CRef       → (CRe, CIm)         — pixel coordinate or view centre
//   DRef       → (DRe, DIm)         — current dz/dc derivative state
//   DeltaRef   → (DeltaRe, DeltaIm) — Tier 4 perturbation: per-pixel δ
//   EpsRef     → (EpsRe, EpsIm)     — Tier 4 perturbation: per-pixel ε
//
// ImZero flag (Phase C, "imag-zero optimisation")
//   Real-valued constants and any expression built only from real-valued
//   constants are flagged ImZero=true. The arithmetic operators use the
//   flag to skip emitting dead `0.0` or `Vector256<double>.Zero` terms.

using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

/// <summary>A pair of C# expressions representing the real and imaginary
/// components of a complex value at some point in the AST walk. The
/// <see cref="ImZero"/> flag is true iff the value's imaginary part is
/// provably zero at code-emission time.</summary>
public readonly record struct ComplexExpr(string Re, string Im, bool ImZero = false);

public abstract class EmitterBase
{
    protected abstract string ZRe { get; }
    protected abstract string ZIm { get; }
    protected abstract string CRe { get; }
    protected abstract string CIm { get; }
    protected abstract string DRe { get; }
    protected abstract string DIm { get; }

    /// <summary>Bindings for the perturbation δ register. Subclasses that
    /// never see DeltaRef (the scalar / AVX2 z-update emitters) can leave
    /// these as the default throw; the perturbation emitter overrides.</summary>
    protected virtual string DeltaRe => throw new InvalidOperationException("DeltaRef not bound in this emitter");
    protected virtual string DeltaIm => throw new InvalidOperationException("DeltaRef not bound in this emitter");
    protected virtual string EpsRe   => throw new InvalidOperationException("EpsRef not bound in this emitter");
    protected virtual string EpsIm   => throw new InvalidOperationException("EpsRef not bound in this emitter");

    /// <summary>Bindings for the Phoenix previous-iterate (z_{n-1})
    /// register. The surrounding template declares (pr, pi) initialised
    /// to zero and reassigns `pr := zr; pi := zi` BEFORE applying the
    /// new-z assignment so the emitted step reads pre-step prev.
    /// Perturbation emitters don't see PrevRef (SupportsPerturbation=
    /// false when prev is present); they keep the default throw.</summary>
    protected virtual string PrevRe => throw new InvalidOperationException("PrevRef not bound in this emitter");
    protected virtual string PrevIm => throw new InvalidOperationException("PrevRef not bound in this emitter");

    /// <summary>Binding for the loop iteration index (real scalar). The
    /// surrounding template injects a per-loop `iter` / `iter_v` /
    /// `iter_q` / `iter_dd` local pulled from whichever loop-counter
    /// variable is in scope at that site. Perturbation emitters
    /// don't see IterRef (SupportsPerturbation=false when iter is
    /// present); they keep the default throw.</summary>
    protected virtual string IterRe => throw new InvalidOperationException("IterRef not bound in this emitter");

    /// <summary>Zero literal in the emitter's complex type — used for the
    /// IterRef Im part (which is always 0). Scalar: "0.0"; AVX2:
    /// "Vector256&lt;double&gt;.Zero"; DD/QD: "(DD)0.0" / "(QD)0.0".</summary>
    protected virtual string IterImLiteral => "0.0";

    /// <summary>Emit a real-valued constant as a complex (k, 0). Must
    /// return <c>ImZero = true</c>.</summary>
    protected abstract ComplexExpr Const(double value);

    /// <summary>Emit the imaginary unit (0, 1) in the emitter's complex
    /// type. Default reuses <see cref="Const"/> to assemble the (0, 1)
    /// pair — works for every concrete emitter because each Const already
    /// produces a valid Re slot in the target type (plain double, DD, QD,
    /// or Vector256/512 broadcast of a double). ImZero is false because
    /// the imaginary part is explicitly non-zero.</summary>
    protected virtual ComplexExpr ImagUnitExpr()
    {
        var zero = Const(0.0);
        var one  = Const(1.0);
        return new ComplexExpr(zero.Re, one.Re, ImZero: false);
    }

    protected abstract ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b);
    protected abstract ComplexExpr OpSub(ComplexExpr a, ComplexExpr b);
    protected abstract ComplexExpr OpMul(ComplexExpr a, ComplexExpr b);
    protected abstract ComplexExpr OpNeg(ComplexExpr a);

    /// <summary>Complex division a/b. Default implementation uses
    /// (a·conj(b)) / |b|². Subclasses override for SIMD or DD/QD targets.</summary>
    protected virtual ComplexExpr OpDiv(ComplexExpr a, ComplexExpr b) =>
        throw new InvalidOperationException("OpDiv not implemented in this emitter");

    /// <summary>Complex conjugate (re, im) → (re, -im).</summary>
    protected virtual ComplexExpr OpConj(ComplexExpr a) =>
        throw new InvalidOperationException("OpConj not implemented in this emitter");

    /// <summary>BurningShip fold (re, im) → (|re|, |im|). Non-holomorphic.</summary>
    protected virtual ComplexExpr OpFold(ComplexExpr a) =>
        throw new InvalidOperationException("OpFold not implemented in this emitter");

    /// <summary>Per-component real functions applied to Re and Im independently
    /// (floor/round/ceil/trunc/fract/sign). Preserve ImZero. Non-holomorphic.
    /// #27 Phase 6 (tranche 2).</summary>
    protected virtual ComplexExpr OpFloor(ComplexExpr a) =>
        throw new InvalidOperationException("OpFloor not implemented in this emitter");
    protected virtual ComplexExpr OpRound(ComplexExpr a) =>
        throw new InvalidOperationException("OpRound not implemented in this emitter");
    protected virtual ComplexExpr OpCeil(ComplexExpr a) =>
        throw new InvalidOperationException("OpCeil not implemented in this emitter");
    protected virtual ComplexExpr OpTrunc(ComplexExpr a) =>
        throw new InvalidOperationException("OpTrunc not implemented in this emitter");
    protected virtual ComplexExpr OpFract(ComplexExpr a) =>
        throw new InvalidOperationException("OpFract not implemented in this emitter");
    protected virtual ComplexExpr OpSign(ComplexExpr a) =>
        throw new InvalidOperationException("OpSign not implemented in this emitter");

    /// <summary>Complex sine. Holomorphic.</summary>
    protected virtual ComplexExpr OpSin(ComplexExpr a) =>
        throw new InvalidOperationException("OpSin not implemented in this emitter");

    /// <summary>Complex cosine. Holomorphic.</summary>
    protected virtual ComplexExpr OpCos(ComplexExpr a) =>
        throw new InvalidOperationException("OpCos not implemented in this emitter");

    /// <summary>Complex exponential. Holomorphic.</summary>
    protected virtual ComplexExpr OpExp(ComplexExpr a) =>
        throw new InvalidOperationException("OpExp not implemented in this emitter");

    /// <summary>Complex natural log. Holomorphic on C\{0}.</summary>
    protected virtual ComplexExpr OpLog(ComplexExpr a) =>
        throw new InvalidOperationException("OpLog not implemented in this emitter");

    /// <summary>Inverse trig / hyperbolic (asin/acos/atan/asinh/acosh/atanh).
    /// Emitted via System.Numerics.Complex to match SandboxExpression. #27 Phase 6.</summary>
    protected virtual ComplexExpr OpAsin(ComplexExpr a) =>
        throw new InvalidOperationException("OpAsin not implemented in this emitter");
    protected virtual ComplexExpr OpAcos(ComplexExpr a) =>
        throw new InvalidOperationException("OpAcos not implemented in this emitter");
    protected virtual ComplexExpr OpAtan(ComplexExpr a) =>
        throw new InvalidOperationException("OpAtan not implemented in this emitter");
    protected virtual ComplexExpr OpAsinh(ComplexExpr a) =>
        throw new InvalidOperationException("OpAsinh not implemented in this emitter");
    protected virtual ComplexExpr OpAcosh(ComplexExpr a) =>
        throw new InvalidOperationException("OpAcosh not implemented in this emitter");
    protected virtual ComplexExpr OpAtanh(ComplexExpr a) =>
        throw new InvalidOperationException("OpAtanh not implemented in this emitter");

    /// <summary>Principal argument lifted to complex: (atan2(im, re), 0).
    /// Non-holomorphic. Output ImZero is true so downstream Add/Mul can
    /// elide the zero imag part exactly like real-lift nodes.</summary>
    protected virtual ComplexExpr OpArg(ComplexExpr a) =>
        throw new InvalidOperationException("OpArg not implemented in this emitter");

    /// <summary>Binary atan2(y, x) lifted to complex. Non-holomorphic.</summary>
    protected virtual ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        throw new InvalidOperationException("OpAtan2 not implemented in this emitter");

    /// <summary>Real minimum lifted to complex (min, 0). Non-holomorphic.</summary>
    protected virtual ComplexExpr OpMin(ComplexExpr a, ComplexExpr b) =>
        throw new InvalidOperationException("OpMin not implemented in this emitter");

    /// <summary>Real maximum lifted to complex (max, 0). Non-holomorphic.</summary>
    protected virtual ComplexExpr OpMax(ComplexExpr a, ComplexExpr b) =>
        throw new InvalidOperationException("OpMax not implemented in this emitter");

    /// <summary>Real modulo (C# '%' on doubles) lifted to complex. Non-holomorphic.</summary>
    protected virtual ComplexExpr OpMod(ComplexExpr a, ComplexExpr b) =>
        throw new InvalidOperationException("OpMod not implemented in this emitter");

    /// <summary>Real part lifted to complex (Re, 0). Non-holomorphic. #27 Phase 6.</summary>
    protected virtual ComplexExpr OpRe(ComplexExpr a) =>
        throw new InvalidOperationException("OpRe not implemented in this emitter");

    /// <summary>Imaginary part lifted to complex (Im, 0). Non-holomorphic. #27 Phase 6.</summary>
    protected virtual ComplexExpr OpIm(ComplexExpr a) =>
        throw new InvalidOperationException("OpIm not implemented in this emitter");

    /// <summary>Magnitude lifted to complex (|x|, 0). Non-holomorphic. #27 Phase 6.</summary>
    protected virtual ComplexExpr OpAbs(ComplexExpr a) =>
        throw new InvalidOperationException("OpAbs not implemented in this emitter");

    /// <summary>Real-valued clamp lifted to complex (clamp(Re), 0). Non-holomorphic. #27 Phase 6.</summary>
    protected virtual ComplexExpr OpClamp(ComplexExpr x, ComplexExpr lo, ComplexExpr hi) =>
        throw new InvalidOperationException("OpClamp not implemented in this emitter");

    /// <summary>General power pow(base, exp) — arbitrary complex exponent.
    /// Both-real → Math.Pow; else Complex.Pow (zero-guarded, principal branch).
    /// Transcendental + non-holomorphic. #27 Phase 6 (tranche 2).</summary>
    protected virtual ComplexExpr OpPow(ComplexExpr a, ComplexExpr b) =>
        throw new InvalidOperationException("OpPow not implemented in this emitter");

    /// <summary>Piecewise selection: given a boolean expression and two
    /// pre-evaluated complex branches, return the selected complex value.
    /// Subclasses choose the strategy — scalar uses a C# ternary on the
    /// rendered cond expression, SIMD targets produce a mask vector and
    /// blend the two branches per lane.</summary>
    protected virtual ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV) =>
        throw new InvalidOperationException("OpIf not implemented in this emitter");

    public ComplexExpr Emit(AstNode node) => node switch
    {
        ZRef        => new ComplexExpr(ZRe,     ZIm,     ImZero: false),
        CRef        => new ComplexExpr(CRe,     CIm,     ImZero: false),
        DRef        => new ComplexExpr(DRe,     DIm,     ImZero: false),
        DeltaRef    => new ComplexExpr(DeltaRe, DeltaIm, ImZero: false),
        EpsRef      => new ComplexExpr(EpsRe,   EpsIm,   ImZero: false),
        PrevRef     => new ComplexExpr(PrevRe,  PrevIm,  ImZero: false),
        // IterRef is real-valued — ImZero=true lets downstream Add/Mul
        // elide dead-zero terms exactly like RealConst inputs do.
        IterRef     => new ComplexExpr(IterRe,  IterImLiteral, ImZero: true),
        RealConst k => Const(k.Value),
        ImagUnit    => ImagUnitExpr(),
        Neg n       => OpNeg(Emit(n.Operand)),
        Add a       => OpAdd(Emit(a.Left), Emit(a.Right)),
        Sub s       => OpSub(Emit(s.Left), Emit(s.Right)),
        Mul m       => OpMul(Emit(m.Left), Emit(m.Right)),
        Pow p       => EmitPow(p),
        PowC pc     => OpPow(Emit(pc.Base), Emit(pc.Exp)),
        Div d       => OpDiv(Emit(d.Left), Emit(d.Right)),
        Conj cj     => OpConj(Emit(cj.Operand)),
        Folded f    => OpFold(Emit(f.Operand)),
        Floor fl    => OpFloor(Emit(fl.Operand)),
        Round rd    => OpRound(Emit(rd.Operand)),
        Ceil ce     => OpCeil(Emit(ce.Operand)),
        Trunc tr    => OpTrunc(Emit(tr.Operand)),
        Fract fr    => OpFract(Emit(fr.Operand)),
        Sign sg     => OpSign(Emit(sg.Operand)),
        Sin s2      => OpSin(Emit(s2.Operand)),
        Cos c2      => OpCos(Emit(c2.Operand)),
        Exp ex      => OpExp(Emit(ex.Operand)),
        Log lg      => OpLog(Emit(lg.Operand)),
        Asin as1    => OpAsin(Emit(as1.Operand)),
        Acos ac1    => OpAcos(Emit(ac1.Operand)),
        Atan at1    => OpAtan(Emit(at1.Operand)),
        Asinh ah1   => OpAsinh(Emit(ah1.Operand)),
        Acosh ch1   => OpAcosh(Emit(ch1.Operand)),
        Atanh th1   => OpAtanh(Emit(th1.Operand)),
        Arg ar      => OpArg(Emit(ar.Operand)),
        Atan2 at    => OpAtan2(Emit(at.Y), Emit(at.X)),
        Min mn      => OpMin(Emit(mn.Left), Emit(mn.Right)),
        Max mx      => OpMax(Emit(mx.Left), Emit(mx.Right)),
        Mod md      => OpMod(Emit(md.Left), Emit(md.Right)),
        ReOp r3     => OpRe(Emit(r3.Operand)),
        ImOp im3    => OpIm(Emit(im3.Operand)),
        AbsOp ab    => OpAbs(Emit(ab.Operand)),
        Clamp cl    => OpClamp(Emit(cl.X), Emit(cl.Lo), Emit(cl.Hi)),
        // Eager-evaluate both branches so any SSA prelude they emit
        // runs unconditionally — matches SIMD lane semantics where
        // every lane evaluates every branch. The cost is paid in
        // intermediate Math.Sin/etc calls; the win is no branch-
        // dependent control flow and uniform width across lanes.
        If i        => OpIf(i.Cond, Emit(i.Then), Emit(i.Else)),
        _ => throw new InvalidOperationException($"Unhandled AST node: {node.GetType().Name}"),
    };

    private ComplexExpr EmitPow(Pow p)
    {
        if (p.Exponent == 0) return Const(1.0);
        var basev = Emit(p.Base);
        var acc = basev;
        for (int i = 1; i < p.Exponent; i++)
            acc = OpMul(acc, basev);
        return acc;
    }
}
