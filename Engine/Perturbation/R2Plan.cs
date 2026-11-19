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

    private R2Plan(AstNode step, double bailout2, (double, double) initialDerivative)
    {
        _step = step;
        Bailout2 = bailout2;
        InitialDerivative = initialDerivative;
        _delta = Compile(step, Mode.Delta);
        _tan = Compile(step, Mode.Tangent);
        _sec = Compile(step, Mode.Second);
        // Rebasing restarts at Z₀ = 0: valid as a reference only when 0 is a critical point for
        // every c, i.e. ∂f/∂z = 0 at z = 0. Checked at three generic c: a polynomial Jacobian that
        // is not identically zero is not exactly 0.0 at all of them.
        bool zero = true;
        foreach (var (cr, ci) in new[] { (0.3125, -0.6875), (-1.0625, 0.21875), (0.046875, 1.28125) })
        {
            var j = DzJacobian(0, 0, cr, ci);
            zero &= j.M11 == 0 && j.M12 == 0 && j.M21 == 0 && j.M22 == 0;
        }
        CanRebase = zero;
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

    internal static string? Unsupported(AstNode n) => n switch
    {
        ZRef or CRef or RealConst or ImagUnit => null,
        Neg u => Unsupported(u.Operand),
        Conj u => Unsupported(u.Operand),
        ReOp u => Unsupported(u.Operand),
        ImOp u => Unsupported(u.Operand),
        Add a => Unsupported(a.Left) ?? Unsupported(a.Right),
        Sub s => Unsupported(s.Left) ?? Unsupported(s.Right),
        Mul m => Unsupported(m.Left) ?? Unsupported(m.Right),
        Pow p => p.Exponent < 0 ? "a negative power" : Unsupported(p.Base),
        _ => n.GetType().Name,
    };

    public bool ParameterPlane => true;
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

    public Bla2 Level0R2(double zr, double zi, double cr, double ci, double epsilon)
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

    private enum Mode { Delta, Tangent, Second }

    private readonly record struct Cx(Expression Re, Expression Im);

    private sealed class Emitter
    {
        public readonly List<ParameterExpression> Vars = new();
        public readonly List<Expression> Body = new();
        public readonly ParameterExpression[] P;
        public readonly Mode Mode;

        public Emitter(Mode mode)
        {
            Mode = mode;
            string[] names = ["zr", "zi", "cr", "ci", "a", "b", "c", "d"];
            P = new ParameterExpression[8];
            for (int i = 0; i < 8; i++) P[i] = Expression.Parameter(typeof(double), names[i]);
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
        private Cx Mul(Cx x, Cx y) => new(
            Let(Expression.Subtract(Expression.Multiply(x.Re, y.Re), Expression.Multiply(x.Im, y.Im))),
            Let(Expression.Add(Expression.Multiply(x.Re, y.Im), Expression.Multiply(x.Im, y.Re))));

        // A jet: [0] the value; Delta: [1] the exact difference; Tangent: [1] the first derivative;
        // Second: [1] the first and [2] the second derivative along the direction.
        private Cx[] Leaf(Cx v, Cx first) => Mode == Mode.Second ? [v, first, K(0, 0)] : [v, first];

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
                Mode.Delta => [v, Add(Mul(x[0], y[1]), Mul(x[1], Add(y[0], y[1])))],
                Mode.Tangent => [v, Add(Mul(x[0], y[1]), Mul(x[1], y[0]))],
                _ => [v, Add(Mul(x[0], y[1]), Mul(x[1], y[0])),
                      Add(Add(Mul(x[2], y[0]), Scale(2.0, Mul(x[1], y[1]))), Mul(x[0], y[2]))],
            };
        }

        private Cx[] Const(double re, double im) => Mode == Mode.Second ? [K(re, im), K(0, 0), K(0, 0)] : [K(re, im), K(0, 0)];

        public Cx[] Emit(AstNode n)
        {
            switch (n)
            {
                case ZRef: return Leaf(new Cx(P[0], P[1]), new Cx(P[4], P[5]));
                case CRef: return Leaf(new Cx(P[2], P[3]), Mode == Mode.Second ? K(0, 0) : new Cx(P[6], P[7]));
                case RealConst k: return Const(k.Value, 0);
                case ImagUnit: return Const(0, 1);
                case Neg u: return Map(Emit(u.Operand), Neg);
                case Conj u: return Map(Emit(u.Operand), Conj);
                case ReOp u: return Map(Emit(u.Operand), Re);
                case ImOp u: return Map(Emit(u.Operand), Im);
                case Add s: return Zip(Emit(s.Left), Emit(s.Right), Add);
                case Sub s: return Zip(Emit(s.Left), Emit(s.Right), Sub);
                case Mul m: return MulJ(Emit(m.Left), Emit(m.Right));
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

    private static F8 Compile(AstNode step, Mode mode)
    {
        var em = new Emitter(mode);
        var jet = em.Emit(step);
        var outv = mode == Mode.Second ? jet[2] : jet[1];
        var ctor = typeof(ValueTuple<double, double>).GetConstructor([typeof(double), typeof(double)])!;
        em.Body.Add(Expression.New(ctor, outv.Re, outv.Im));
        return Expression.Lambda<F8>(Expression.Block(em.Vars, em.Body), em.P).Compile();
    }
}
