// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstDifferentiator.cs
//
// Symbolic differentiation of polynomial-in-(z,c) ASTs. Used to:
//   • Track dz/dc per iteration (Inigo Quilez normals + Milnor/Hubbard
//     exterior distance estimate).
//   • Provide the dp/dz coefficient (the "A" term) for Bilinear
//     Approximation (Tier 5, future).
//   • Provide the dp/dc coefficient (the "B" term) for BLA and the
//     constant term in perturbation expansion (Tier 4, future).
//
// The differentiator is intentionally minimal — it operates on the
// restricted polynomial grammar so each rule is a closed-form one-liner.
// Output is paired with the simplifier to collapse the inevitable
// 0·x and 1·x nodes.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstDifferentiator
{
    public enum Var { Z, C }

    /// <summary>Symbolic derivative of <paramref name="node"/> with
    /// respect to <paramref name="v"/>. The returned tree is NOT
    /// simplified; pass it through <see cref="AstSimplifier.Simplify"/>.</summary>
    public static AstNode Diff(AstNode node, Var v) => node switch
    {
        ZRef       => v == Var.Z ? new RealConst(1.0) : new RealConst(0.0),
        CRef       => v == Var.C ? new RealConst(1.0) : new RealConst(0.0),
        DRef       => new RealConst(0.0),               // opaque under both vars
        DeltaRef   => new RealConst(0.0),               // perturbation: opaque
        EpsRef     => new RealConst(0.0),               // perturbation: opaque
        // Phoenix prev (z_{n-1}): opaque to symbolic diff. Tracking
        // dprev/dc properly needs a parallel derivative state vector
        // updated as `dprev := dz; dz := step_derivative`. Until that
        // ships, treating prev as opaque produces a WRONG dz/dc for
        // Phoenix equations — gated off via SupportsDe=false in
        // CalculatorGenApi so the wrong value is never consumed.
        PrevRef    => new RealConst(0.0),
        // Iteration index: real scalar, derivative w.r.t. z or c is 0.
        IterRef    => new RealConst(0.0),
        RealConst  => new RealConst(0.0),
        // Imaginary unit: complex constant — derivative w.r.t. either real
        // variable is 0. The chain rule still produces the right value
        // through Mul: d(i·z)/dz = 0·z + i·1 = i.
        ImagUnit   => new RealConst(0.0),
        Neg n      => new Neg(Diff(n.Operand, v)),
        Add a      => new Add(Diff(a.Left, v), Diff(a.Right, v)),
        Sub s      => new Sub(Diff(s.Left, v), Diff(s.Right, v)),
        // Product rule: (fg)' = f'g + fg'
        Mul m      => new Add(new Mul(Diff(m.Left, v),  m.Right),
                              new Mul(m.Left,            Diff(m.Right, v))),
        // Power-of-AST chain rule: (u^n)' = n · u^(n-1) · u'
        Pow p      => p.Exponent == 0
                       ? new RealConst(0.0)
                       : new Mul(new RealConst(p.Exponent),
                                 new Mul(new Pow(p.Base, p.Exponent - 1),
                                         Diff(p.Base, v))),
        // Quotient rule: (f/g)' = (f'g − fg') / g²
        Div d      => new Div(
                          new Sub(new Mul(Diff(d.Left, v),  d.Right),
                                  new Mul(d.Left,            Diff(d.Right, v))),
                          new Mul(d.Right, d.Right)),
        // Anti-holomorphic: Wirtinger ∂conj(z)/∂z = 0. Equations using
        // conj produce a zero derivative chain — the distance estimate
        // becomes meaningless. The SupportsDe flag emitted by CalcGen
        // gates the call site so the colour map gets the smooth count
        // only.
        Conj       => new RealConst(0.0),
        Folded     => new RealConst(0.0),
        // Per-component floor/round/ceil/trunc/fract/sign: piecewise-constant
        // (derivative 0 a.e.). Non-holomorphic; DE gated off upstream.
        Floor      => new RealConst(0.0),
        Round      => new RealConst(0.0),
        Ceil       => new RealConst(0.0),
        Trunc      => new RealConst(0.0),
        Fract      => new RealConst(0.0),
        Sign       => new RealConst(0.0),
        // arg / atan2 are non-holomorphic (Wirtinger-real derivative). Treat
        // them like Conj/Folded: ∂/∂z arg(u) = 0 in the holomorphic chain.
        // SupportsDe is gated off by Contains<Arg> / Contains<Atan2> upstream.
        Arg        => new RealConst(0.0),
        Atan2      => new RealConst(0.0),
        // Inverse trig / hyperbolic: holomorphic, but analytic-DE derivative
        // rules are not implemented — DE is gated off upstream (folded into the
        // DE gate), so this opaque 0 is never used; it just keeps the
        // unconditional derivUpdate build from throwing.
        Asin       => new RealConst(0.0),
        Acos       => new RealConst(0.0),
        Atan       => new RealConst(0.0),
        Asinh      => new RealConst(0.0),
        Acosh      => new RealConst(0.0),
        Atanh      => new RealConst(0.0),
        // min/max/mod: subgradient at boundary, treated as opaque for the
        // chain rule. Distance estimate is disabled when present anyway.
        Min        => new RealConst(0.0),
        Max        => new RealConst(0.0),
        Mod        => new RealConst(0.0),
        // pow(base, exp): general power. Analytic DE is gated off upstream
        // (PowC folded into hasArg), so the derivative is never used — but
        // the unconditional derivUpdate build must not throw. Treat opaque.
        PowC       => new RealConst(0.0),
        // #27 Phase 6 — re/im/abs/clamp are non-holomorphic real-lifts. Opaque
        // to the chain rule (∂/∂v = 0); SupportsDe is gated off upstream via
        // Contains<ReOp>/… so this derivative is never actually used, but the
        // unconditional derivUpdate build must not throw.
        ReOp       => new RealConst(0.0),
        ImOp       => new RealConst(0.0),
        AbsOp      => new RealConst(0.0),
        Clamp      => new RealConst(0.0),
        // Transcendentals — holomorphic chain rules:
        //   d/dv sin(u) =  cos(u) · u'
        //   d/dv cos(u) = -sin(u) · u'
        //   d/dv exp(u) =  exp(u) · u'
        //   d/dv log(u) =  u' / u
        Sin s2     => new Mul(new Cos(s2.Operand), Diff(s2.Operand, v)),
        Cos c2     => new Mul(new Neg(new Sin(c2.Operand)), Diff(c2.Operand, v)),
        Exp ex     => new Mul(new Exp(ex.Operand), Diff(ex.Operand, v)),
        Log lg     => new Div(Diff(lg.Operand, v), lg.Operand),
        // Piecewise: differentiate each branch independently — the
        // condition itself is real-valued and never feeds the complex
        // chain. The boundary locus where Cond changes truth value is a
        // measure-zero set; distance estimate is meaningful on either
        // side but undefined exactly on it. We don't attempt to detect
        // boundary pixels here; the renderer can flag them later if it
        // wants Hausdorff-correct DE.
        If i       => new If(i.Cond, Diff(i.Then, v), Diff(i.Else, v)),
        _ => throw new InvalidOperationException($"Cannot differentiate {node.GetType().Name}"),
    };

    /// <summary>
    /// Build the per-iteration update rule for dz/dc given the step
    /// function z_{n+1} = p(z, c):
    ///
    ///     dz_{n+1}/dc  =  (∂p/∂z) · (dz_n/dc)  +  (∂p/∂c)
    ///
    /// The returned AST is simplified and references the symbolic
    /// <see cref="DRef"/> node for the current dz/dc value.
    /// </summary>
    public static AstNode BuildDerivativeUpdate(AstNode stepFn)
    {
        var dpdz = AstSimplifier.Simplify(Diff(stepFn, Var.Z));
        var dpdc = AstSimplifier.Simplify(Diff(stepFn, Var.C));
        var update = new Add(new Mul(dpdz, new DRef()), dpdc);
        return AstSimplifier.Simplify(update);
    }

    /// <summary>Convenience: simplified ∂p/∂z.</summary>
    public static AstNode DpDz(AstNode stepFn)
        => AstSimplifier.Simplify(Diff(stepFn, Var.Z));

    /// <summary>Convenience: simplified ∂p/∂c.</summary>
    public static AstNode DpDc(AstNode stepFn)
        => AstSimplifier.Simplify(Diff(stepFn, Var.C));
}
