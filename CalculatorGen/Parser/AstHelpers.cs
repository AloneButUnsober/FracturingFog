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
        _ => false,
    };
}
