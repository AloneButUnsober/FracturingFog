// AstSimplifier.cs
//
// Algebraic peephole simplifier. The differentiator emits ASTs full of
// 0·x, 1·x, x+0, x^0 nodes; simplifying once after differentiation makes
// the emitted code readable and shrinks it ~5-10×. The simplifier is
// also useful for the top-of-file derivation comment.
//
// Rules applied bottom-up until fixed point:
//   Add(0, x)   → x        Add(x, 0)   → x
//   Sub(x, 0)   → x        Sub(0, x)   → -x
//   Mul(0, x)   → 0        Mul(x, 0)   → 0
//   Mul(1, x)   → x        Mul(x, 1)   → x
//   Mul(-1, x)  → -x       Mul(x, -1)  → -x
//   Pow(x, 0)   → 1        Pow(x, 1)   → x
//   Neg(0)      → 0        Neg(Neg(x)) → x
//
// Real-constant arithmetic on (RealConst op RealConst) is folded. The
// simplifier deliberately does NOT do full algebraic canonicalisation
// (commutative reordering, distributive expansion) — those can blow up
// the tree size and the emitter handles them fine.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstSimplifier
{
    public static AstNode Simplify(AstNode node)
    {
        // Fixed-point bottom-up simplification.
        AstNode prev;
        do
        {
            prev = node;
            node = SimplifyOnce(node);
        } while (!ReferenceEquals(node, prev) && !node.Equals(prev));
        return node;
    }

    private static AstNode SimplifyOnce(AstNode node) => node switch
    {
        Neg n   => SimplifyNeg(Simplify(n.Operand)),
        Add a   => SimplifyAdd(Simplify(a.Left), Simplify(a.Right)),
        Sub s   => SimplifySub(Simplify(s.Left), Simplify(s.Right)),
        Mul m   => SimplifyMul(Simplify(m.Left), Simplify(m.Right)),
        Pow p   => SimplifyPow(Simplify(p.Base), p.Exponent),
        Div d   => SimplifyDiv(Simplify(d.Left), Simplify(d.Right)),
        Conj cj => new Conj(Simplify(cj.Operand)),
        Folded f => new Folded(Simplify(f.Operand)),
        Sin s2  => new Sin(Simplify(s2.Operand)),
        Cos c2  => new Cos(Simplify(c2.Operand)),
        Exp ex  => new Exp(Simplify(ex.Operand)),
        Log lg  => new Log(Simplify(lg.Operand)),
        Arg ar  => new Arg(Simplify(ar.Operand)),
        Atan2 at => new Atan2(Simplify(at.Y), Simplify(at.X)),
        Min mn  => new Min(Simplify(mn.Left), Simplify(mn.Right)),
        Max mx  => new Max(Simplify(mx.Left), Simplify(mx.Right)),
        Mod md  => new Mod(Simplify(md.Left), Simplify(md.Right)),
        // Recurse into both branches; cond is left untouched since
        // CondTerm operands are AstNodes and could carry sub-trees we
        // could in theory simplify, but the emitter handles unsimplified
        // cond trees fine and skipping it keeps the simplifier from
        // having to mirror the entire grammar inside CondNode/CondTerm.
        If i    => new If(i.Cond, Simplify(i.Then), Simplify(i.Else)),
        _ => node,
    };

    private static AstNode SimplifyDiv(AstNode l, AstNode r)
    {
        if (l is RealConst lk && lk.Value == 0.0) return new RealConst(0.0);
        if (r is RealConst rk && rk.Value == 1.0) return l;
        if (r is RealConst rk2 && rk2.Value == -1.0) return SimplifyNeg(l);
        if (l is RealConst a && r is RealConst b && b.Value != 0.0)
            return new RealConst(a.Value / b.Value);
        return new Div(l, r);
    }

    private static AstNode SimplifyNeg(AstNode a) => a switch
    {
        RealConst k          => new RealConst(-k.Value),
        Neg n                => n.Operand,
        _                    => new Neg(a),
    };

    private static AstNode SimplifyAdd(AstNode l, AstNode r)
    {
        if (l is RealConst lk && lk.Value == 0.0) return r;
        if (r is RealConst rk && rk.Value == 0.0) return l;
        if (l is RealConst a && r is RealConst b) return new RealConst(a.Value + b.Value);
        return new Add(l, r);
    }

    private static AstNode SimplifySub(AstNode l, AstNode r)
    {
        if (r is RealConst rk && rk.Value == 0.0) return l;
        if (l is RealConst lk && lk.Value == 0.0) return SimplifyNeg(r);
        if (l is RealConst a && r is RealConst b) return new RealConst(a.Value - b.Value);
        return new Sub(l, r);
    }

    private static AstNode SimplifyMul(AstNode l, AstNode r)
    {
        if (l is RealConst lk)
        {
            if (lk.Value == 0.0) return new RealConst(0.0);
            if (lk.Value == 1.0) return r;
            if (lk.Value == -1.0) return SimplifyNeg(r);
        }
        if (r is RealConst rk)
        {
            if (rk.Value == 0.0) return new RealConst(0.0);
            if (rk.Value == 1.0) return l;
            if (rk.Value == -1.0) return SimplifyNeg(l);
        }
        if (l is RealConst a && r is RealConst b) return new RealConst(a.Value * b.Value);
        return new Mul(l, r);
    }

    private static AstNode SimplifyPow(AstNode b, int exp)
    {
        if (exp == 0) return new RealConst(1.0);
        if (exp == 1) return b;
        if (b is RealConst k) return new RealConst(Math.Pow(k.Value, exp));
        return new Pow(b, exp);
    }
}
