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

    /// <summary>Emit a real-valued constant as a complex (k, 0). Must
    /// return <c>ImZero = true</c>.</summary>
    protected abstract ComplexExpr Const(double value);

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

    public ComplexExpr Emit(AstNode node) => node switch
    {
        ZRef        => new ComplexExpr(ZRe,     ZIm,     ImZero: false),
        CRef        => new ComplexExpr(CRe,     CIm,     ImZero: false),
        DRef        => new ComplexExpr(DRe,     DIm,     ImZero: false),
        DeltaRef    => new ComplexExpr(DeltaRe, DeltaIm, ImZero: false),
        EpsRef      => new ComplexExpr(EpsRe,   EpsIm,   ImZero: false),
        RealConst k => Const(k.Value),
        Neg n       => OpNeg(Emit(n.Operand)),
        Add a       => OpAdd(Emit(a.Left), Emit(a.Right)),
        Sub s       => OpSub(Emit(s.Left), Emit(s.Right)),
        Mul m       => OpMul(Emit(m.Left), Emit(m.Right)),
        Pow p       => EmitPow(p),
        Div d       => OpDiv(Emit(d.Left), Emit(d.Right)),
        Conj cj     => OpConj(Emit(cj.Operand)),
        Folded f    => OpFold(Emit(f.Operand)),
        Sin s2      => OpSin(Emit(s2.Operand)),
        Cos c2      => OpCos(Emit(c2.Operand)),
        Exp ex      => OpExp(Emit(ex.Operand)),
        Log lg      => OpLog(Emit(lg.Operand)),
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
