// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1909 (epic #1253, D4) — a perturbation plan for steps that are polynomial in z, c and their
// conjugates / real and imaginary parts: Tricorn (conj(z)² + c) and user formulas with conj, re and
// im. Such a step is not complex-analytic, so it is treated as a real polynomial map of R² (the
// "R² lowering"):
//   • δ: difference arithmetic, operator by operator. Each node carries its value V at the reference
//     and its exact difference D = node(Z + δ, C + ε) − node(Z, C). Sums and conj / re / im are
//     linear, so D maps as V does; a product has D = V₁·D₂ + D₁·(V₂ + D₂). Every term is of order
//     δ or ε (no large-minus-large), and for a polynomial the result is the exact difference.
//     This is the operator-by-operator perturbation of mathr 2021 (Docs/Resources-Bibliography.md,
//     #mathr-deep-zoom-2021, "Hybrid Fractals"); FF claims no novelty for it.
//   • BLA: forward-mode tangents give the 2×2 Jacobians A = ∂f/∂z and B = ∂f/∂c (Bla2), a second-
//     order tangent bounds the dropped terms for the radius (operator norms: mathr 2022,
//     #mathr-deep-zoom-2022).
//   • the derivative: the 2×2 Jacobian of the orbit with respect to the pixel; the sink gets its
//     first column, ∂z/∂Re(c) (User Equation's numeric Jacobian is the same column).
// The compiled functions are System.Linq.Expressions delegates, as AstPlan's (#1908). Design:
// Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.2.
//
// #1910 (D5) adds abs folds: abs of a real value and Folded ((|re|, |im|)), which give Burning Ship,
// its Perpendicular / Celtic variants and user abs. Their difference is laser blaster's diffabs case
// analysis, as given by mathr 2021 (Docs/Resources-Bibliography.md, #mathr-deep-zoom-2021):
//     diffabs(X, x) = |X + x| − |X| = X ≥ 0 ? (X + x ≥ 0 ? x : −(2X + x)) : (X + x > 0 ? 2X + x : −x),
// exact, with no large-minus-large. A fold is linear only while its argument keeps its sign, so the
// BLA radius also stays inside the distance to each fold's sign change: |U| / ‖∇_z U‖ (less the
// pixel's ε through ∇_c U), halved — mathr 2022's min(|X|, |Y|) / 2 for Burning Ship
// (#mathr-deep-zoom-2022), generalised to a fold of any real sub-expression. abs of a complex value
// is its modulus (a square root), not a fold, and stays outside this plan.
//
// #1911 (D6) adds division, for Magnet 1 / 2 and user "/". The quotient's difference is taken
// without cancellation:
//     (P + ΔP)/(Q + ΔQ) − P/Q = (ΔP·Q − P·ΔQ) / (Q·(Q + ΔQ)),
// a rearrangement over a common denominator (FF claims no novelty; the design plan §4.4 states it).
// A pixel whose Q + ΔQ cancels most of Q (by a pole the reference is not by) is flagged
// (PoleRatio2) and goes to another reference or to direct iteration. Derivatives use the quotient
// rule; the second-order radius grows the closer Q is to 0, so the BLA shrinks by poles.
//
// #1913 (D8) adds sin, cos, exp, log, sqrt and pow with a constant exponent: their differences come
// from PerturbFunctions (e^V·expm1(D), log1p(D/V), V^p·expm1(p·L), sum-to-product), with the principal
// branch kept exactly (log's 2πk). log and pow are not analytic on their cut or at 0: the BLA radius
// stays inside the distance to the cut (Limit), and a pixel whose argument cancels the reference's by
// 0 is flagged (Pole). The reference evaluates them in BigFloat (BigFloatMath, #1914). A plan can also
// be on the dynamic plane (z₀ = the pixel, c a constant in the AST): Glynn.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

public sealed class R2Plan : IRealPerturbationPlan
{
    // (zr, zi, cr, ci, a, b, c, d) → a complex (as (re, im)).
    private delegate (double Re, double Im) F8(double zr, double zi, double cr, double ci,
                                              double a, double b, double c, double d);

    private readonly AstNode _step;
    private readonly F8 _delta;   // (Z, C, δ, ε) → the new δ
    private readonly F8 _tan;     // (z, c, t_z, t_c) → the derivative of f along (t_z, t_c)
    private readonly F8 _sec;     // (z, c, t, _) → the second derivative of f along z + s·t
    private readonly F8? _limit;  // (z, c, |ε|max, _) → the δ that reaches the nearest fold's sign change (#1910)
    private readonly F8? _pole;   // (Z, C, δ, ε) → min |Q + ΔQ|² / |Q|² over the divisions (#1911)

    private R2Plan(AstNode step, double bailout2, (double, double) initialDerivative, bool parameterPlane = true)
    {
        _step = step;
        ParameterPlane = parameterPlane;
        Bailout2 = bailout2;
        InitialDerivative = initialDerivative;
        _delta = Compile(step, Mode.Delta);
        _tan = Compile(step, Mode.Tangent);
        _sec = Compile(step, Mode.Second);
        _limit = HasFold(step) || HasCut(step) ? Compile(step, Mode.Limit) : null;
        _pole = HasDiv(step) || HasCut(step) ? Compile(step, Mode.Pole) : null;
        // Rebasing restarts at Z₀ = 0: valid as a reference only when 0 is a critical point for
        // every c, i.e. ∂f/∂z = 0 at z = 0. Checked at three generic c: a polynomial Jacobian that
        // is not identically zero is not exactly 0.0 at all of them.
        bool zero = true;
        foreach (var (cr, ci) in new[] { (0.3125, -0.6875), (-1.0625, 0.21875), (0.046875, 1.28125) })
        {
            var j = DzJacobian(0, 0, cr, ci);
            zero &= j.M11 == 0 && j.M12 == 0 && j.M21 == 0 && j.M22 == 0;
        }
        CanRebase = zero && parameterPlane;
    }

    /// <summary>The plan for z_{n+1} = <paramref name="step"/>(z, c), z₀ = 0, the pixel c; null with
    /// <paramref name="why"/> when the step uses an operator outside this plan's set (+ − ×, integer
    /// powers, real / i constants, conj, re, im).</summary>
    public static R2Plan? TryCreate(AstNode step, double bailout2, out string? why,
                                    (double Re, double Im) initialDerivative = default)
    {
        why = Unsupported(step);
        return why == null ? new R2Plan(step, bailout2, initialDerivative) : null;
    }

    /// <summary>Tricorn, conj(z)² + c, with TricornKernel's bailout (512²) and its dz₀ = 1.</summary>
    public static R2Plan Tricorn()
        => new(new Add(new Pow(new Conj(new ZRef()), 2), new CRef()), 512.0 * 512.0, (1.0, 0.0));

    /// <summary>Burning Ship, (|Re z| + i·|Im z|)² + c, with BurningShipKernel's bailout (512²) and its dz₀ = 1.</summary>
    public static R2Plan BurningShip()
        => new(new Add(new Pow(new Add(new AbsOp(new ReOp(new ZRef())), new Mul(new ImagUnit(), new AbsOp(new ImOp(new ZRef())))), 2),
                       new CRef()), 512.0 * 512.0, (1.0, 0.0));

    /// <summary>Magnet type I, ((z² + c − 1) / (2z + c − 2))², with MagnetOneKernel's bailout (|z|² ≥ 100).</summary>
    public static R2Plan Magnet1()
    {
        AstNode z = new ZRef(), c = new CRef(), one = new RealConst(1), two = new RealConst(2);
        var num = new Sub(new Add(new Pow(z, 2), c), one);
        var den = new Sub(new Add(new Mul(two, z), c), two);
        return new(new Pow(new Div(num, den), 2), 100.0, (0.0, 0.0));
    }

    /// <summary>Magnet type II, ((z³ + 3(c − 1)z + (c − 1)(c − 2)) / (3z² + 3(c − 2)z + (c − 1)(c − 2) + 1))²,
    /// with MagnetTwoKernel's bailout (|z|² ≥ 100). (c² − 3c + 3 = (c − 1)(c − 2) + 1.)</summary>
    public static R2Plan Magnet2()
    {
        AstNode z = new ZRef(), c = new CRef(), one = new RealConst(1), two = new RealConst(2), three = new RealConst(3);
        AstNode a = new Sub(c, one), b = new Sub(c, two), ab = new Mul(a, b);
        var num = new Add(new Add(new Pow(z, 3), new Mul(three, new Mul(a, z))), ab);
        var den = new Add(new Add(new Mul(three, new Pow(z, 2)), new Mul(three, new Mul(b, z))), new Add(ab, one));
        return new(new Pow(new Div(num, den), 2), 100.0, (0.0, 0.0));
    }

    /// <summary>#1913 — Glynn, z^1.5 + c on the dynamic plane (z₀ = the pixel), GlynnKernel's bailout (|z|² ≥ 4).
    /// The kernel reseeds an orbit that lands exactly on 0; that measure-zero case is not reproduced.</summary>
    public static R2Plan Glynn(double cr, double ci)
        => new(new Add(new PowC(new ZRef(), new RealConst(1.5)),
                       ci == 0 ? new RealConst(cr) : new Add(new RealConst(cr), new Mul(new RealConst(ci), new ImagUnit()))),
               4.0, (1.0, 0.0), parameterPlane: false);

    // #1913 — a node whose function has a branch cut / a singular point at 0 (log, sqrt, pow).
    private static bool HasCut(AstNode n) => n switch
    {
        Log or Sqrt or PowC => true,
        Sin u => HasCut(u.Operand), Cos u => HasCut(u.Operand), Exp u => HasCut(u.Operand),
        Neg u => HasCut(u.Operand), Conj u => HasCut(u.Operand), ReOp u => HasCut(u.Operand),
        ImOp u => HasCut(u.Operand), AbsOp u => HasCut(u.Operand), Folded u => HasCut(u.Operand),
        Add a => HasCut(a.Left) || HasCut(a.Right),
        Sub s => HasCut(s.Left) || HasCut(s.Right),
        Mul m => HasCut(m.Left) || HasCut(m.Right),
        Div d => HasCut(d.Left) || HasCut(d.Right),
        Pow p => HasCut(p.Base),
        _ => false,
    };

    // A sub-expression with no z, c or prev: a constant (pow's exponent).
    private static bool IsConstant(AstNode n) => n switch
    {
        RealConst or ImagUnit => true,
        Neg u => IsConstant(u.Operand),
        Add a => IsConstant(a.Left) && IsConstant(a.Right),
        Sub s => IsConstant(s.Left) && IsConstant(s.Right),
        Mul m => IsConstant(m.Left) && IsConstant(m.Right),
        Div d => IsConstant(d.Left) && IsConstant(d.Right),
        Pow p => IsConstant(p.Base),
        _ => false,
    };

    internal static System.Numerics.Complex ConstValue(AstNode n) => n switch
    {
        RealConst k => k.Value,
        ImagUnit => System.Numerics.Complex.ImaginaryOne,
        Neg u => -ConstValue(u.Operand),
        Add a => ConstValue(a.Left) + ConstValue(a.Right),
        Sub s => ConstValue(s.Left) - ConstValue(s.Right),
        Mul m => ConstValue(m.Left) * ConstValue(m.Right),
        Div d => ConstValue(d.Left) / ConstValue(d.Right),
        Pow p => System.Numerics.Complex.Pow(ConstValue(p.Base), p.Exponent),
        _ => throw new NotSupportedException(n.GetType().Name),
    };

    private static bool HasDiv(AstNode n) => n switch
    {
        Div => true,
        Neg u => HasDiv(u.Operand), Conj u => HasDiv(u.Operand), ReOp u => HasDiv(u.Operand),
        ImOp u => HasDiv(u.Operand), AbsOp u => HasDiv(u.Operand), Folded u => HasDiv(u.Operand),
        Add a => HasDiv(a.Left) || HasDiv(a.Right),
        Sub s => HasDiv(s.Left) || HasDiv(s.Right),
        Mul m => HasDiv(m.Left) || HasDiv(m.Right),
        Pow p => HasDiv(p.Base),
        _ => false,
    };

    // True when n's value is real for every z, c (its imaginary part is the constant 0).
    internal static bool IsReal(AstNode n) => n switch
    {
        RealConst or ReOp or ImOp or AbsOp => true,
        Neg u => IsReal(u.Operand),
        Conj u => IsReal(u.Operand),
        Add a => IsReal(a.Left) && IsReal(a.Right),
        Sub s => IsReal(s.Left) && IsReal(s.Right),
        Mul m => IsReal(m.Left) && IsReal(m.Right),
        Div d => IsReal(d.Left) && IsReal(d.Right),
        Pow p => IsReal(p.Base),
        _ => false,
    };

    private static bool HasFold(AstNode n) => n switch
    {
        AbsOp or Folded => true,
        Neg u => HasFold(u.Operand),
        Conj u => HasFold(u.Operand),
        ReOp u => HasFold(u.Operand),
        ImOp u => HasFold(u.Operand),
        Add a => HasFold(a.Left) || HasFold(a.Right),
        Sub s => HasFold(s.Left) || HasFold(s.Right),
        Mul m => HasFold(m.Left) || HasFold(m.Right),
        Div d => HasFold(d.Left) || HasFold(d.Right),
        Pow p => HasFold(p.Base),
        _ => false,
    };

    internal static string? Unsupported(AstNode n) => n switch
    {
        ZRef or CRef or RealConst or ImagUnit => null,
        AbsOp u => IsReal(u.Operand) ? Unsupported(u.Operand) : "abs of a complex value (its modulus)",
        Folded u => Unsupported(u.Operand),
        Neg u => Unsupported(u.Operand),
        Conj u => Unsupported(u.Operand),
        ReOp u => Unsupported(u.Operand),
        ImOp u => Unsupported(u.Operand),
        Add a => Unsupported(a.Left) ?? Unsupported(a.Right),
        Sub s => Unsupported(s.Left) ?? Unsupported(s.Right),
        Mul m => Unsupported(m.Left) ?? Unsupported(m.Right),
        Div d => Unsupported(d.Left) ?? Unsupported(d.Right),   // #1911
        Sin u => Unsupported(u.Operand), Cos u => Unsupported(u.Operand), Exp u => Unsupported(u.Operand),   // #1913
        Log u => Unsupported(u.Operand), Sqrt u => Unsupported(u.Operand),
        PowC w => IsConstant(w.Exp) ? Unsupported(w.Base) : "pow with a non-constant exponent",
        Pow p => p.Exponent < 0 ? "a negative power" : Unsupported(p.Base),
        _ => n.GetType().Name,
    };

    public bool ParameterPlane { get; }

    /// <summary>#1911 — the step divides.</summary>
    public bool HasPoles => _pole != null;

    public double PoleRatio2(double zr, double zi, double cr, double ci, double dr, double di, double er, double ei)
        => _pole != null ? _pole(zr, zi, cr, ci, dr, di, er, ei).Re : double.MaxValue;
    public bool CanRebase { get; }
    public double Bailout2 { get; }
    public (double Re, double Im) InitialDerivative { get; }

    public void RefStep(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int prec)
    {
        var (r, i) = AstPlan.Eval(_step, zr, zi, cr, ci, prec);
        zr = r; zi = i;
    }

    public void DeltaStep(double zr, double zi, double cr, double ci, ref double dr, ref double di, double er, double ei)
    {
        var (r, i) = _delta(zr, zi, cr, ci, dr, di, er, ei);
        dr = r; di = i;
    }

    /// <summary>∂f/∂z as a 2×2 matrix: column 1 along Re z, column 2 along Im z.</summary>
    public RealMat2 DzJacobian(double zr, double zi, double cr, double ci)
    {
        var (a, b) = _tan(zr, zi, cr, ci, 1, 0, 0, 0);
        var (c, d) = _tan(zr, zi, cr, ci, 0, 1, 0, 0);
        return new RealMat2(a, c, b, d);
    }

    public bool IsGlitch(double zr, double zi, double Zr, double Zi, double cr, double ci, double tau2)
    {
        // Pauldelbrot's test with the Jacobian's (squared Frobenius) size in place of |f′|².
        static double F2(RealMat2 m) => m.M11 * m.M11 + m.M12 * m.M12 + m.M21 * m.M21 + m.M22 * m.M22;
        return F2(DzJacobian(zr, zi, cr, ci)) < tau2 * F2(DzJacobian(Zr, Zi, cr, ci));
    }

    public Bla2 Level0R2(double zr, double zi, double cr, double ci, double epsilon, double dcMax)
    {
        var a = DzJacobian(zr, zi, cr, ci);
        var (b11, b21) = _tan(zr, zi, cr, ci, 0, 0, 1, 0);
        var (b12, b22) = _tan(zr, zi, cr, ci, 0, 0, 0, 1);
        // The dropped term along a unit direction u = (cos θ, sin θ) is ½·H(u)·|δ|², with
        // H(u) = Hxx cos²θ + 2 Hxy cosθ sinθ + Hyy sin²θ = P + Q cos 2θ + R sin 2θ,
        // P = (Hxx + Hyy)/2, Q = (Hxx − Hyy)/2, R = Hxy; so |H(u)| ≤ |P| + ‖[Q R]‖ (tight for
        // z² and conj(z)², where P = 0). Keeping ½·h·r² under ε·σ_min(A)·r gives r ≤ 2ε·σ_min/h;
        // half of that, as AstPlan (it reduces to AstPlan's radius for a complex-analytic step).
        var (xr, xi) = _sec(zr, zi, cr, ci, 1, 0, 0, 0);
        var (yr, yi) = _sec(zr, zi, cr, ci, 0, 1, 0, 0);
        var (pr, pi) = _sec(zr, zi, cr, ci, 1, 1, 0, 0);
        double hxyR = 0.5 * (pr - xr - yr), hxyI = 0.5 * (pi - xi - yi);
        double h = Math.Sqrt(0.25 * ((xr + yr) * (xr + yr) + (xi + yi) * (xi + yi)))
                 + new RealMat2(0.5 * (xr - yr), hxyR, 0.5 * (xi - yi), hxyI).Norm;
        double r = h > 0 ? epsilon * a.MinGain / h : double.MaxValue;
        // #1910 — and inside half the distance to the nearest fold's sign change.
        if (_limit != null) r = Math.Min(r, 0.5 * _limit(zr, zi, cr, ci, dcMax, 0, 0, 0).Re);
        return new Bla2(a, new RealMat2(b11, b12, b21, b22), r == double.MaxValue ? double.MaxValue : r * r, 1);
    }

    public void JacobianStep(double zr, double zi, double cr, double ci, ref RealMat2 j)
    {
        // Each column t of J moves to (∂f/∂z)·t + ∂f/∂c·e (e the matching unit pixel direction).
        var (a, b) = _tan(zr, zi, cr, ci, j.M11, j.M21, 1, 0);
        var (c, d) = _tan(zr, zi, cr, ci, j.M12, j.M22, 0, 1);
        j = new RealMat2(a, c, b, d);
    }

    // The conformal members are not used for a real plan (IRealPerturbationPlan).
    public Bla Level0(double zr, double zi, double cr, double ci, double epsilon)
        => throw new NotSupportedException("R2Plan skips with Bla2 (Level0R2).");

    public void DerivStep(double zr, double zi, double cr, double ci, ref double dvr, ref double dvi)
        => throw new NotSupportedException("R2Plan carries a 2×2 Jacobian (JacobianStep).");

    // ── Compilation: a jet per node, emitted in SSA form (every value bound once) ──

    // Limit (#1910): jets of four first derivatives (along Re z, Im z, Re c, Im c) and a running
    // minimum, over the folds, of the δ that reaches the fold's sign change.
    // Pole (#1911): Delta's jets, and a running minimum of |Q + ΔQ|² / |Q|² over the divisions.
    internal enum Mode { Delta, Tangent, Second, Limit, Pole }

    internal readonly record struct Cx(Expression Re, Expression Im);

    // #1912 — shared with TwoStatePlan, which names its own parameters and resolves its own leaves
    // (z, prev / the second state, c) before the defaults below.
    internal sealed class Emitter
    {
        public readonly List<ParameterExpression> Vars = new();
        public readonly List<Expression> Body = new();
        public readonly ParameterExpression[] P;
        public readonly Mode Mode;

        public readonly ParameterExpression? MinLimit;   // Mode.Limit

        private readonly Func<AstNode, Emitter, Cx[]?>? _leaves;

        public Emitter(Mode mode, string[]? names = null, Func<AstNode, Emitter, Cx[]?>? leaves = null)
        {
            Mode = mode;
            names ??= ["zr", "zi", "cr", "ci", "a", "b", "c", "d"];
            _leaves = leaves;
            P = new ParameterExpression[names.Length];
            for (int i = 0; i < names.Length; i++) P[i] = Expression.Parameter(typeof(double), names[i]);
            if (mode is Mode.Limit or Mode.Pole)
            {
                MinLimit = Expression.Variable(typeof(double), "limit");
                Vars.Add(MinLimit);
                Body.Add(Expression.Assign(MinLimit, Expression.Constant(double.MaxValue)));
            }
        }

        private Expression Let(Expression e)
        {
            if (e is ConstantExpression or ParameterExpression) return e;
            var v = Expression.Variable(typeof(double));
            Vars.Add(v);
            Body.Add(Expression.Assign(v, e));
            return v;
        }

        private static readonly Expression Zero = Expression.Constant(0.0);
        private static Cx K(double re, double im) => new(Expression.Constant(re), Expression.Constant(im));
        private Cx Add(Cx x, Cx y) => new(Let(Expression.Add(x.Re, y.Re)), Let(Expression.Add(x.Im, y.Im)));
        private Cx Sub(Cx x, Cx y) => new(Let(Expression.Subtract(x.Re, y.Re)), Let(Expression.Subtract(x.Im, y.Im)));
        private Cx Neg(Cx x) => new(Let(Expression.Negate(x.Re)), Let(Expression.Negate(x.Im)));
        private Cx Conj(Cx x) => new(x.Re, Let(Expression.Negate(x.Im)));
        private static Cx Re(Cx x) => new(x.Re, Zero);
        private static Cx Im(Cx x) => new(x.Im, Zero);
        private Cx Scale(double k, Cx x) => new(Let(Expression.Multiply(Expression.Constant(k), x.Re)),
                                                Let(Expression.Multiply(Expression.Constant(k), x.Im)));
        // x / y (complex), as x·conj(y) / |y|².
        private Cx CDiv(Cx x, Cx y)
        {
            var den = Let(Expression.Add(Expression.Multiply(y.Re, y.Re), Expression.Multiply(y.Im, y.Im)));
            return new(Let(Expression.Divide(Expression.Add(Expression.Multiply(x.Re, y.Re), Expression.Multiply(x.Im, y.Im)), den)),
                       Let(Expression.Divide(Expression.Subtract(Expression.Multiply(x.Im, y.Re), Expression.Multiply(x.Re, y.Im)), den)));
        }

        private Cx Mul(Cx x, Cx y) => new(
            Let(Expression.Subtract(Expression.Multiply(x.Re, y.Re), Expression.Multiply(x.Im, y.Im))),
            Let(Expression.Add(Expression.Multiply(x.Re, y.Im), Expression.Multiply(x.Im, y.Re))));

        // A jet: [0] the value; Delta: [1] the exact difference; Tangent: [1] the first derivative;
        // Second: [1] the first and [2] the second derivative along the direction.
        internal Cx[] Leaf(Cx v, Cx first) => Mode == Mode.Second ? [v, first, K(0, 0)] : [v, first];

        // Mode.Limit leaves: z moves along Re z / Im z, c along Re c / Im c.
        private static Cx[] ZLimit(Cx z) => [z, K(1, 0), K(0, 1), K(0, 0), K(0, 0)];
        private static Cx[] CLimit(Cx c) => [c, K(0, 0), K(0, 0), K(1, 0), K(0, 1)];

        private static Expression Ge0(Expression x) => Expression.GreaterThanOrEqual(x, Zero);

        // |X + x| − |X| by cases (diffabs): every branch is of the size of x or of X + x.
        private Expression DiffAbs(Expression X, Expression x)
        {
            var s = Let(Expression.Add(X, x));
            var two = Expression.Constant(2.0);
            return Let(Expression.Condition(Ge0(X),
                Expression.Condition(Ge0(s), x, Expression.Negate(Expression.Add(Expression.Multiply(two, X), x))),
                Expression.Condition(Expression.GreaterThan(s, Zero), Expression.Add(Expression.Multiply(two, X), x), Expression.Negate(x))));
        }

        // Fold the real component (im = false) or the imaginary one of a jet; the other component is
        // left as is (for abs of a real value it is the constant 0).
        private Expression[] FoldPart(Cx[] x, bool im)
        {
            Expression V(int i) => im ? x[i].Im : x[i].Re;
            var r = new Expression[x.Length];
            r[0] = Let(Expression.Call(typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!, V(0)));
            if (Mode is Mode.Delta or Mode.Pole) { r[1] = DiffAbs(V(0), V(1)); return r; }
            // Derivatives: the side of the kink the value is on (≥ 0 as the kernels' sign).
            var sgn = Let(Expression.Condition(Ge0(V(0)), Expression.Constant(1.0), Expression.Constant(-1.0)));
            for (int i = 1; i < x.Length; i++) r[i] = Let(Expression.Multiply(sgn, V(i)));
            if (Mode == Mode.Limit)
            {
                // The δ that moves this fold's argument U to 0: (|U| − ‖∇_c U‖·|ε|max) / ‖∇_z U‖.
                Expression Sq(Expression e) => Expression.Multiply(e, e);
                var gz = Let(Expression.Call(typeof(Math).GetMethod(nameof(Math.Sqrt))!, Expression.Add(Sq(V(1)), Sq(V(2)))));
                var gc = Let(Expression.Call(typeof(Math).GetMethod(nameof(Math.Sqrt))!, Expression.Add(Sq(V(3)), Sq(V(4)))));
                var room = Let(Expression.Subtract(r[0], Expression.Multiply(gc, P[4])));
                var cand = Expression.Condition(Expression.LessThanOrEqual(room, Zero), Zero,
                    Expression.Condition(Expression.GreaterThan(gz, Zero), Expression.Divide(room, gz), Expression.Constant(double.MaxValue)));
                Body.Add(Expression.Assign(MinLimit!, Expression.Call(typeof(Math).GetMethod(nameof(Math.Min), [typeof(double), typeof(double)])!, MinLimit!, cand)));
            }
            return r;
        }

        private Cx[] AbsReal(Cx[] x)
        {
            var f = FoldPart(x, im: false);
            var r = new Cx[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = new Cx(f[i], Zero);
            return r;
        }

        private Cx[] Fold(Cx[] x)
        {
            var a = FoldPart(x, im: false); var b = FoldPart(x, im: true);
            var r = new Cx[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = new Cx(a[i], b[i]);
            return r;
        }

        private Cx[] Map(Cx[] x, Func<Cx, Cx> f)
        {
            var r = new Cx[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = f(x[i]);
            return r;
        }

        private Cx[] Zip(Cx[] x, Cx[] y, Func<Cx, Cx, Cx> f)
        {
            var r = new Cx[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = f(x[i], y[i]);
            return r;
        }

        private Cx[] MulJ(Cx[] x, Cx[] y)
        {
            var v = Mul(x[0], y[0]);
            return Mode switch
            {
                // (V₁ + D₁)(V₂ + D₂) − V₁V₂ = V₁·D₂ + D₁·(V₂ + D₂)
                Mode.Delta or Mode.Pole => [v, Add(Mul(x[0], y[1]), Mul(x[1], Add(y[0], y[1])))],
                Mode.Tangent => [v, Add(Mul(x[0], y[1]), Mul(x[1], y[0]))],
                Mode.Limit => [v, Add(Mul(x[0], y[1]), Mul(x[1], y[0])), Add(Mul(x[0], y[2]), Mul(x[2], y[0])),
                               Add(Mul(x[0], y[3]), Mul(x[3], y[0])), Add(Mul(x[0], y[4]), Mul(x[4], y[0]))],
                _ => [v, Add(Mul(x[0], y[1]), Mul(x[1], y[0])),
                      Add(Add(Mul(x[2], y[0]), Scale(2.0, Mul(x[1], y[1]))), Mul(x[0], y[2]))],
            };
        }

        // #1911 — a quotient's jet.
        private Cx[] DivJ(Cx[] x, Cx[] y)
        {
            var q = CDiv(x[0], y[0]);
            switch (Mode)
            {
                case Mode.Delta:
                    // (ΔP·Q − P·ΔQ) / (Q·(Q + ΔQ)): no difference of two nearly equal quotients.
                    return [q, CDiv(Sub(Mul(x[1], y[0]), Mul(x[0], y[1])), Mul(y[0], Add(y[0], y[1])))];
                case Mode.Pole:
                {
                    var qd = Add(y[0], y[1]);
                    Expression M2(Cx v) => Expression.Add(Expression.Multiply(v.Re, v.Re), Expression.Multiply(v.Im, v.Im));
                    var ratio = Let(Expression.Divide(M2(qd), M2(y[0])));
                    Body.Add(Expression.Assign(MinLimit!, Expression.Call(typeof(Math).GetMethod(nameof(Math.Min), [typeof(double), typeof(double)])!, MinLimit!, ratio)));
                    return [q, CDiv(Sub(Mul(x[1], y[0]), Mul(x[0], y[1])), Mul(y[0], qd))];
                }
                case Mode.Second:
                {
                    // q′ = (P′ − q·Q′)/Q, q″ = (P″ − 2·q′·Q′ − q·Q″)/Q
                    var t = CDiv(Sub(x[1], Mul(q, y[1])), y[0]);
                    var u = CDiv(Sub(Sub(x[2], Scale(2.0, Mul(t, y[1]))), Mul(q, y[2])), y[0]);
                    return [q, t, u];
                }
                default:
                {
                    var r = new Cx[x.Length];
                    r[0] = q;
                    for (int k = 1; k < x.Length; k++) r[k] = CDiv(Sub(x[k], Mul(q, y[k])), y[0]);
                    return r;
                }
            }
        }

        // #1913 — an elementary function's jet. kind: 0 exp, 1 log, 2 sin, 3 cos, 4 pow p (sqrt = pow ½).
        private Cx[] FuncJ(int kind, Cx[] x, System.Numerics.Complex p)
        {
            var PF = typeof(PerturbFunctions);
            Expression Pr = Expression.Constant(p.Real), Pi = Expression.Constant(p.Imaginary), Kd = Expression.Constant(kind);
            Cx Field(ParameterExpression q, string a, string b) => new(Expression.Property(q, a), Expression.Property(q, b));
            ParameterExpression LetQ(Expression call)
            {
                var v = Expression.Variable(typeof(Q4));
                Vars.Add(v); Body.Add(Expression.Assign(v, call));
                return v;
            }
            Expression M2(Cx v) => Expression.Add(Expression.Multiply(v.Re, v.Re), Expression.Multiply(v.Im, v.Im));
            Expression MinInto(Expression cand)
                => Expression.Assign(MinLimit!, Expression.Call(typeof(Math).GetMethod(nameof(Math.Min), [typeof(double), typeof(double)])!, MinLimit!, cand));
            bool cut = kind is 1 or 4;
            if (Mode is Mode.Delta or Mode.Pole)
            {
                var name = kind switch { 0 => nameof(PerturbFunctions.ExpD), 1 => nameof(PerturbFunctions.LogD), 2 => nameof(PerturbFunctions.SinD), 3 => nameof(PerturbFunctions.CosD), _ => nameof(PerturbFunctions.PowD) };
                var m = PF.GetMethod(name)!;
                var q = LetQ(kind == 4 ? Expression.Call(m, x[0].Re, x[0].Im, x[1].Re, x[1].Im, Pr, Pi)
                                       : Expression.Call(m, x[0].Re, x[0].Im, x[1].Re, x[1].Im));
                if (Mode == Mode.Pole && cut)
                {
                    // |V + D|² / |V|²: the pixel's argument by 0 where the reference's is not.
                    var sum = new Cx(Let(Expression.Add(x[0].Re, x[1].Re)), Let(Expression.Add(x[0].Im, x[1].Im)));
                    Body.Add(MinInto(Expression.Divide(M2(sum), M2(x[0]))));
                }
                return [Field(q, "A", "B"), Field(q, "C", "D")];
            }
            var d1 = LetQ(Expression.Call(PF.GetMethod(nameof(PerturbFunctions.Deriv1))!, Kd, x[0].Re, x[0].Im, Pr, Pi));
            Cx f = Field(d1, "A", "B"), fp = Field(d1, "C", "D");
            var r = new Cx[x.Length];
            r[0] = f;
            for (int k = 1; k < x.Length; k++) r[k] = Mul(fp, x[k]);
            if (Mode == Mode.Second)
            {
                var d2 = LetQ(Expression.Call(PF.GetMethod(nameof(PerturbFunctions.Deriv2))!, Kd, x[0].Re, x[0].Im, Pr, Pi));
                r[2] = Add(Mul(Field(d2, "A", "B"), Mul(x[1], x[1])), Mul(fp, x[2]));
            }
            if (Mode == Mode.Limit && cut)
            {
                // Stay off the cut: (distance − ‖∇_c V‖·|ε|max) / ‖∇_z V‖ (Frobenius norms, an upper bound).
                var gz = Let(Expression.Call(typeof(Math).GetMethod(nameof(Math.Sqrt))!, Expression.Add(M2(x[1]), M2(x[2]))));
                var gc = Let(Expression.Call(typeof(Math).GetMethod(nameof(Math.Sqrt))!, Expression.Add(M2(x[3]), M2(x[4]))));
                var dist = Let(Expression.Call(PF.GetMethod(nameof(PerturbFunctions.CutDistance))!, x[0].Re, x[0].Im));
                var room = Let(Expression.Subtract(dist, Expression.Multiply(gc, P[4])));
                Body.Add(MinInto(Expression.Condition(Expression.LessThanOrEqual(room, Zero), Zero,
                    Expression.Condition(Expression.GreaterThan(gz, Zero), Expression.Divide(room, gz), Expression.Constant(double.MaxValue)))));
            }
            return r;
        }

        private Cx[] Const(double re, double im) => Mode switch
        {
            Mode.Second => [K(re, im), K(0, 0), K(0, 0)],
            Mode.Limit => [K(re, im), K(0, 0), K(0, 0), K(0, 0), K(0, 0)],
            _ => [K(re, im), K(0, 0)],
        };

        public Cx[] Emit(AstNode n)
        {
            if (_leaves != null && _leaves(n, this) is { } leaf) return leaf;
            switch (n)
            {
                case ZRef: return Mode == Mode.Limit ? ZLimit(new Cx(P[0], P[1])) : Leaf(new Cx(P[0], P[1]), new Cx(P[4], P[5]));
                case CRef: return Mode == Mode.Limit ? CLimit(new Cx(P[2], P[3]))
                    : Leaf(new Cx(P[2], P[3]), Mode == Mode.Second ? K(0, 0) : new Cx(P[6], P[7]));
                case AbsOp u: return AbsReal(Emit(u.Operand));   // a real operand (Unsupported checks)
                case Folded u: return Fold(Emit(u.Operand));
                case RealConst k: return Const(k.Value, 0);
                case ImagUnit: return Const(0, 1);
                case Neg u: return Map(Emit(u.Operand), Neg);
                case Conj u: return Map(Emit(u.Operand), Conj);
                case ReOp u: return Map(Emit(u.Operand), Re);
                case ImOp u: return Map(Emit(u.Operand), Im);
                case Add s: return Zip(Emit(s.Left), Emit(s.Right), Add);
                case Sub s: return Zip(Emit(s.Left), Emit(s.Right), Sub);
                case Mul m: return MulJ(Emit(m.Left), Emit(m.Right));
                case Div d: return DivJ(Emit(d.Left), Emit(d.Right));   // #1911
                case Exp u: return FuncJ(0, Emit(u.Operand), 0);           // #1913
                case Log u: return FuncJ(1, Emit(u.Operand), 0);
                case Sin u: return FuncJ(2, Emit(u.Operand), 0);
                case Cos u: return FuncJ(3, Emit(u.Operand), 0);
                case Sqrt u: return FuncJ(4, Emit(u.Operand), 0.5);
                case PowC w: return FuncJ(4, Emit(w.Base), ConstValue(w.Exp));
                case Pow w:
                {
                    if (w.Exponent == 0) return Const(1, 0);
                    var b = Emit(w.Base);
                    var acc = b;
                    for (int k = 1; k < w.Exponent; k++) acc = MulJ(acc, b);
                    return acc;
                }
                default: throw new NotSupportedException(n.GetType().Name);
            }
        }
    }

    private static F8 Compile(AstNode step, Mode mode) => CompileAs<F8>(step, mode, null, null);

    // #1912 — any delegate shape: the parameters are the emitter's (names), in order.
    internal static TDel CompileAs<TDel>(AstNode step, Mode mode, string[]? names, Func<AstNode, Emitter, Cx[]?>? leaves)
        where TDel : Delegate
    {
        var em = new Emitter(mode, names, leaves);
        var jet = em.Emit(step);
        var outv = mode == Mode.Second ? jet[2] : jet[1];
        var ctor = typeof(ValueTuple<double, double>).GetConstructor([typeof(double), typeof(double)])!;
        em.Body.Add(mode is Mode.Limit or Mode.Pole
            ? Expression.New(ctor, em.MinLimit!, Expression.Constant(0.0))
            : Expression.New(ctor, outv.Re, outv.Im));
        return Expression.Lambda<TDel>(Expression.Block(em.Vars, em.Body), em.P).Compile();
    }
}
