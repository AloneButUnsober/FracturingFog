// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstSubstitute.cs
//
// Substitutes leaf nodes in an AST. Used by the perturbation builder to
// rewrite p(z, c) into p(Z+δ, C+ε): every ZRef is replaced by Add(Z, δ),
// every CRef by Add(C, ε). The original Z and C references in the
// replacement are preserved (they refer to the reference-orbit iterate
// and view centre at runtime); the δ / ε nodes are bound by the
// perturbation emitter.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstSubstitute
{
    /// <summary>Recursively replace every <see cref="ZRef"/> by
    /// <paramref name="zRepl"/> and every <see cref="CRef"/> by
    /// <paramref name="cRepl"/>. Other node types pass through.</summary>
    public static AstNode Apply(AstNode node, AstNode zRepl, AstNode cRepl) => node switch
    {
        ZRef       => zRepl,
        CRef       => cRepl,
        DRef       => node,
        DeltaRef   => node,
        EpsRef     => node,
        RealConst  => node,
        Neg n      => new Neg(Apply(n.Operand, zRepl, cRepl)),
        Add a      => new Add(Apply(a.Left, zRepl, cRepl), Apply(a.Right, zRepl, cRepl)),
        Sub s      => new Sub(Apply(s.Left, zRepl, cRepl), Apply(s.Right, zRepl, cRepl)),
        Mul m      => new Mul(Apply(m.Left, zRepl, cRepl), Apply(m.Right, zRepl, cRepl)),
        Pow p      => new Pow(Apply(p.Base, zRepl, cRepl), p.Exponent),
        Div d      => new Div(Apply(d.Left, zRepl, cRepl), Apply(d.Right, zRepl, cRepl)),
        Conj cj    => new Conj(Apply(cj.Operand, zRepl, cRepl)),
        Folded f   => new Folded(Apply(f.Operand, zRepl, cRepl)),
        _ => throw new InvalidOperationException($"AstSubstitute: unhandled {node.GetType().Name}"),
    };
}
