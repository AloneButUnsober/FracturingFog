// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1908 (epic #1253, D3) — a perturbation plan derived from an equation's AST, so a user's own
// formula (User Equation) renders on PerturbationEngine past double. The equation language is
// lowered to CalcGen's AST (CalcGenLowering.Lower) and CalcGen's symbolic machinery derives what the
// engine needs:
//   • the δ recurrence: AstPerturbation.BuildDeltaUpdate (the exact Taylor expansion, ε added last);
//   • the derivative update: AstDifferentiator.BuildDerivativeUpdate (dz′ = ∂f/∂z·dz + ∂f/∂c);
//   • BLA's A = ∂f/∂z, B = ∂f/∂c, and ½·∂²f/∂z² for the validity radius.
// They compile to delegates through System.Linq.Expressions (no Roslyn, no assembly load: the hot-
// load sandbox rules, #27 / #1259, are untouched). The reference orbit evaluates the step AST in
// BigFloat. This slice covers polynomials in z and c (+ − ×, integer powers, real / i constants);
// any other operator leaves the formula on its double path. Design: Docs/Technical/Deep-Zoom-All-
// Families-DesignPlan.md §5. FF claims no novelty for automatic derivation (mathr 2021, in
// Docs/Resources-Bibliography.md, generates hybrid perturbation code; CalcGen's AstPerturbation
// predates this slice).

using System;
using System.Linq.Expressions;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

public sealed class AstPlan : IPerturbationPlan
{
    // All compiled functions take (zr, zi, cr, ci, dr, di, er, ei, vr, vi) and return a complex.
    private delegate (double Re, double Im) Fn(double zr, double zi, double cr, double ci,
                                              double dr, double di, double er, double ei, double vr, double vi);

    private readonly AstNode _step;
    private readonly Fn _delta, _deriv, _a, _b, _half2;

    private AstPlan(AstNode step, double bailout2, bool canRebase)
    {
        _step = step;
        Bailout2 = bailout2;
        CanRebase = canRebase;
        _delta = Compile(AstPerturbation.BuildDeltaUpdate(step));
        _deriv = Compile(AstDifferentiator.BuildDerivativeUpdate(step));
        var a = AstDifferentiator.DpDz(step);
        _a = Compile(a);
        _b = Compile(AstDifferentiator.DpDc(step));
        _half2 = Compile(AstSimplifier.Simplify(new Mul(new RealConst(0.5),
            AstSimplifier.Simplify(AstDifferentiator.Diff(a, AstDifferentiator.Var.Z)))));
    }

    /// <summary>The plan for z_{n+1} = <paramref name="step"/>(z, c), z₀ = 0, the pixel c; null with
    /// <paramref name="why"/> when the step uses an operator this slice has no δ rule for.</summary>
    public static AstPlan? TryCreate(AstNode step, double bailout2, out string? why)
    {
        why = Unsupported(step);
        if (why != null) return null;
        // Rebasing restarts at Z₀ = 0: worthwhile only when 0 is a critical point for every c.
        var a0 = AstSimplifier.Simplify(AstSubstitute.Apply(AstDifferentiator.DpDz(step), new RealConst(0), new CRef()));
        bool canRebase = a0 is RealConst { Value: 0.0 };
        return new AstPlan(step, bailout2, canRebase);
    }

    // The first node outside the polynomial subset, or null.
    private static string? Unsupported(AstNode n) => n switch
    {
        ZRef or CRef or RealConst or ImagUnit => null,
        Neg u => Unsupported(u.Operand),
        Add a => Unsupported(a.Left) ?? Unsupported(a.Right),
        Sub s => Unsupported(s.Left) ?? Unsupported(s.Right),
        Mul m => Unsupported(m.Left) ?? Unsupported(m.Right),
        Pow p => p.Exponent < 0 ? "a negative power" : Unsupported(p.Base),
        _ => n.GetType().Name,
    };

    public bool ParameterPlane => true;
    public bool CanRebase { get; }
    public double Bailout2 { get; }
    public (double Re, double Im) InitialDerivative => (0, 0);   // z₀ = 0 does not depend on c

    public void RefStep(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int prec)
    {
        var (r, i) = Eval(_step, zr, zi, cr, ci, prec);
        zr = r; zi = i;
    }

    public void DeltaStep(double zr, double zi, double cr, double ci, ref double dr, ref double di, double er, double ei)
    {
        var (r, i) = _delta(zr, zi, cr, ci, dr, di, er, ei, 0, 0);
        dr = r; di = i;
    }

    public void DerivStep(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi)
    {
        var (r, i) = _deriv(zr, zi, cr, ci, 0, 0, 0, 0, dvr, dvi);
        dvr = r; dvi = i;
    }

    public bool IsGlitch(double zr, double zi, double Zr, double Zi, double cr, double ci, double tau2)
    {
        var (pr, pi) = _a(zr, zi, cr, ci, 0, 0, 0, 0, 0, 0);
        var (qr, qi) = _a(Zr, Zi, cr, ci, 0, 0, 0, 0, 0, 0);
        return pr * pr + pi * pi < tau2 * (qr * qr + qi * qi);
    }

    public Bla Level0(double zr, double zi, double cr, double ci, double epsilon)
    {
        var (ar, ai) = _a(zr, zi, cr, ci, 0, 0, 0, 0, 0, 0);
        var (br, bi) = _b(zr, zi, cr, ci, 0, 0, 0, 0, 0, 0);
        var (hr, hi) = _half2(zr, zi, cr, ci, 0, 0, 0, 0, 0, 0);
        // The first dropped term is ½f″·δ²; keeping it under ε·|A·δ| needs |δ| ≤ ε·|A|/|½f″|. Half
        // of that, as PowerPlan (where it reduces to ε·|Z|/(d − 1)). Linear in z: no limit from it.
        double h = Math.Sqrt(hr * hr + hi * hi), am = Math.Sqrt(ar * ar + ai * ai);
        double r = h > 0 ? 0.5 * epsilon * am / h : double.MaxValue;
        return new Bla(ar, ai, br, bi, r == double.MaxValue ? double.MaxValue : r * r, 1);
    }

    // ── BigFloat evaluation of the step (the reference orbit) ──

    private static (BigFloat, BigFloat) Eval(AstNode n, BigFloat zr, BigFloat zi, BigFloat cr, BigFloat ci, int p)
    {
        switch (n)
        {
            case ZRef: return (zr, zi);
            case CRef: return (cr, ci);
            case RealConst k: return (BigFloat.FromDouble(k.Value, p), BigFloat.Zero(p));
            case ImagUnit: return (BigFloat.Zero(p), BigFloat.FromDouble(1, p));
            case Neg u: { var (a, b) = Eval(u.Operand, zr, zi, cr, ci, p); return (-a, -b); }
            case Add s:
            {
                var (a, b) = Eval(s.Left, zr, zi, cr, ci, p); var (c, d) = Eval(s.Right, zr, zi, cr, ci, p);
                return (BigFloat.Add(a, c, p), BigFloat.Add(b, d, p));
            }
            case Sub s:
            {
                var (a, b) = Eval(s.Left, zr, zi, cr, ci, p); var (c, d) = Eval(s.Right, zr, zi, cr, ci, p);
                return (BigFloat.Sub(a, c, p), BigFloat.Sub(b, d, p));
            }
            case Mul m:
            {
                var (a, b) = Eval(m.Left, zr, zi, cr, ci, p); var (c, d) = Eval(m.Right, zr, zi, cr, ci, p);
                return MulB(a, b, c, d, p);
            }
            case Pow w:
            {
                var (a, b) = Eval(w.Base, zr, zi, cr, ci, p);
                (BigFloat, BigFloat) acc = (BigFloat.FromDouble(1, p), BigFloat.Zero(p));
                for (int k = 0; k < w.Exponent; k++) acc = MulB(acc.Item1, acc.Item2, a, b, p);
                return acc;
            }
            default: throw new NotSupportedException(n.GetType().Name);
        }
    }

    private static (BigFloat, BigFloat) MulB(BigFloat a, BigFloat b, BigFloat c, BigFloat d, int p)
        => (BigFloat.Sub(BigFloat.Mul(a, c, p), BigFloat.Mul(b, d, p), p),
            BigFloat.Add(BigFloat.Mul(a, d, p), BigFloat.Mul(b, c, p), p));

    // ── Compilation of the derived ASTs to delegates ──

    private static Fn Compile(AstNode node)
    {
        var ps = new ParameterExpression[10];
        string[] names = ["zr", "zi", "cr", "ci", "dr", "di", "er", "ei", "vr", "vi"];
        for (int i = 0; i < 10; i++) ps[i] = Expression.Parameter(typeof(double), names[i]);
        var (re, im) = Emit(node, ps);
        var ctor = typeof(ValueTuple<double, double>).GetConstructor([typeof(double), typeof(double)])!;
        return Expression.Lambda<Fn>(Expression.New(ctor, re, im), ps).Compile();
    }

    private static (Expression Re, Expression Im) Emit(AstNode n, ParameterExpression[] p)
    {
        Expression Zero() => Expression.Constant(0.0);
        switch (n)
        {
            case ZRef: return (p[0], p[1]);
            case CRef: return (p[2], p[3]);
            case DeltaRef: return (p[4], p[5]);
            case EpsRef: return (p[6], p[7]);
            case DRef: return (p[8], p[9]);
            case RealConst k: return (Expression.Constant(k.Value), Zero());
            case ImagUnit: return (Zero(), Expression.Constant(1.0));
            case Neg u: { var (a, b) = Emit(u.Operand, p); return (Expression.Negate(a), Expression.Negate(b)); }
            case Add s:
            {
                var (a, b) = Emit(s.Left, p); var (c, d) = Emit(s.Right, p);
                return (Expression.Add(a, c), Expression.Add(b, d));
            }
            case Sub s:
            {
                var (a, b) = Emit(s.Left, p); var (c, d) = Emit(s.Right, p);
                return (Expression.Subtract(a, c), Expression.Subtract(b, d));
            }
            case Mul m:
            {
                var (a, b) = Emit(m.Left, p); var (c, d) = Emit(m.Right, p);
                return MulE(a, b, c, d);
            }
            case Pow w:
            {
                if (w.Exponent == 0) return (Expression.Constant(1.0), Zero());
                var (a, b) = Emit(w.Base, p);
                // Bind the base once, then multiply (the exponents here are small).
                var br = Expression.Variable(typeof(double)); var bi = Expression.Variable(typeof(double));
                (Expression, Expression) acc = (br, bi);
                for (int k = 1; k < w.Exponent; k++) acc = MulE(acc.Item1, acc.Item2, br, bi);
                Expression Block(Expression body) => Expression.Block([br, bi],
                    Expression.Assign(br, a), Expression.Assign(bi, b), body);
                return (Block(acc.Item1), Block(acc.Item2));
            }
            default: throw new NotSupportedException(n.GetType().Name);
        }
    }

    private static (Expression, Expression) MulE(Expression a, Expression b, Expression c, Expression d)
        => (Expression.Subtract(Expression.Multiply(a, c), Expression.Multiply(b, d)),
            Expression.Add(Expression.Multiply(a, d), Expression.Multiply(b, c)));
}
