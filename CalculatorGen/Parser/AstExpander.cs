// AstExpander.cs
//
// Distributive expansion of multiplications and integer powers so that
// the surrounding subtraction in `p(Z+δ, C+ε) − p(Z, C)` can cancel
// like terms via the simplifier.
//
// Rules (applied bottom-up to a fixed point):
//
//   (a + b) · x   →   a·x + b·x          // left-distribute
//   x · (a + b)   →   x·a + x·b          // right-distribute
//   (a − b) · x   →   a·x − b·x
//   x · (a − b)   →   x·a − x·b
//
//   (a + b)^n     →   binomial-expanded sum of Mul/Pow products
//   (a − b)^n     →   binomial with alternating signs
//
// The expander does NOT reorder or collect like terms — it produces a
// fully distributed sum-of-products tree. The simplifier (folding 0·x,
// 1·x and constant arithmetic) then collapses the noise. For the
// perturbation use-case this is enough: the Z^n and C terms that came
// from p(Z, C) cancel against the subtracted original by structural
// equivalence after both sides are fully expanded.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstExpander
{
    /// <summary>Expand to a fixed point. Each pass distributes Mul over
    /// Add/Sub once and unrolls one level of Pow on a non-leaf base; a
    /// second pass propagates the new Adds outward.</summary>
    public static AstNode Expand(AstNode node)
    {
        AstNode prev;
        do
        {
            prev = node;
            node = ExpandOnce(node);
        } while (!ReferenceEquals(node, prev) && !node.Equals(prev));
        return node;
    }

    private static AstNode ExpandOnce(AstNode node) => node switch
    {
        Neg n   => new Neg(Expand(n.Operand)),
        Add a   => new Add(Expand(a.Left), Expand(a.Right)),
        Sub s   => new Sub(Expand(s.Left), Expand(s.Right)),
        Mul m   => DistributeMul(Expand(m.Left), Expand(m.Right)),
        Pow p   => ExpandPow(Expand(p.Base), p.Exponent),
        // Div / Conj / Folded pass through. The perturbation Taylor
        // builder doesn't differentiate through Div/Conj/Folded (their
        // derivatives are 0 or quotient-rule); the expander leaves them
        // intact so the emitters render them as primitives.
        Div d   => new Div(Expand(d.Left), Expand(d.Right)),
        Conj cj => new Conj(Expand(cj.Operand)),
        Folded f => new Folded(Expand(f.Operand)),
        _ => node,
    };

    private static AstNode DistributeMul(AstNode l, AstNode r)
    {
        // Left side is a sum: (a + b) · r  → a·r + b·r
        if (l is Add la) return new Add(DistributeMul(la.Left, r), DistributeMul(la.Right, r));
        if (l is Sub ls) return new Sub(DistributeMul(ls.Left, r), DistributeMul(ls.Right, r));
        // Right side is a sum: l · (a + b)  → l·a + l·b
        if (r is Add ra) return new Add(DistributeMul(l, ra.Left), DistributeMul(l, ra.Right));
        if (r is Sub rs) return new Sub(DistributeMul(l, rs.Left), DistributeMul(l, rs.Right));
        return new Mul(l, r);
    }

    private static AstNode ExpandPow(AstNode b, int exp)
    {
        if (exp <= 1) return exp == 1 ? b : new RealConst(1.0);
        // Only expand Pow when the base is a polynomial in subexpressions
        // (i.e. would benefit from distribution). For pure ZRef^n etc. we
        // could leave the Pow node intact — but feeding it through repeated
        // Mul distribution is also fine and lets the simplifier fold.
        AstNode acc = b;
        for (int i = 1; i < exp; i++)
            acc = DistributeMul(acc, b);
        return acc;
    }
}
