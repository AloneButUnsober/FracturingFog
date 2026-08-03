// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstHelpers.cs
//
// Lightweight AST queries the generator uses to gate which output
// bodies it can emit. Examples:
//   • Equations containing Conj or Folded are non-holomorphic — the
//     dz/dc chain rule produces zero and the distance estimate
//     becomes meaningless. CalcGen emits SupportsDe = false so the
//     runtime can skip the DE call and the colour map gets the
//     smooth count only.
//   • Equations containing Div / Conj / Folded confuse the
//     perturbation Taylor expansion (Div produces 1/g terms the
//     distributive expander can't simplify; Conj/Folded aren't
//     analytic). CalcGen emits SupportsPerturbation = false and the
//     generated calculator falls back to the AVX2 / scalar z-update.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstHelpers
{
    /// <summary>Recursive search for any node whose runtime type matches
    /// <typeparamref name="T"/>. Returns true on first hit.</summary>
    public static bool Contains<T>(AstNode node) where T : AstNode => node switch
    {
        T _    => true,
        Neg n  => Contains<T>(n.Operand),
        Add a  => Contains<T>(a.Left)   || Contains<T>(a.Right),
        Sub s  => Contains<T>(s.Left)   || Contains<T>(s.Right),
        Mul m  => Contains<T>(m.Left)   || Contains<T>(m.Right),
        Pow p  => Contains<T>(p.Base),
        Div d  => Contains<T>(d.Left)   || Contains<T>(d.Right),
        Conj c => Contains<T>(c.Operand),
        Folded f => Contains<T>(f.Operand),
        Sin s  => Contains<T>(s.Operand),
        Cos co => Contains<T>(co.Operand),
        Exp e  => Contains<T>(e.Operand),
        Log lg => Contains<T>(lg.Operand),
        Arg ar => Contains<T>(ar.Operand),
        Atan2 at => Contains<T>(at.Y) || Contains<T>(at.X),
        Min mn => Contains<T>(mn.Left) || Contains<T>(mn.Right),
        Max mx => Contains<T>(mx.Left) || Contains<T>(mx.Right),
        Mod md => Contains<T>(md.Left) || Contains<T>(md.Right),
        ReOp r3  => Contains<T>(r3.Operand),
        ImOp im3 => Contains<T>(im3.Operand),
        AbsOp ab => Contains<T>(ab.Operand),
        Clamp cl => Contains<T>(cl.X) || Contains<T>(cl.Lo) || Contains<T>(cl.Hi),
        // Piecewise — recurse into both branches and into any AstNodes
        // embedded inside the condition's CondTerms (re(...)/im(...)/abs(...)
        // each carry a complex sub-expression).
        If i   => Contains<T>(i.Then) || Contains<T>(i.Else) || CondContains<T>(i.Cond),
        _ => false,
    };

    private static bool CondContains<T>(CondNode c) where T : AstNode => c switch
    {
        Cmp cmp => CondTermContains<T>(cmp.Left) || CondTermContains<T>(cmp.Right),
        _ => false,
    };

    private static bool CondTermContains<T>(CondTerm t) where T : AstNode => t switch
    {
        CondRe r  => Contains<T>(r.Of),
        CondIm im => Contains<T>(im.Of),
        CondAbs2 a => Contains<T>(a.Of),
        CondArg ag => Contains<T>(ag.Of),
        _ => false,
    };
}
