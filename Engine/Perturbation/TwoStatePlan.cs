// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1912 (epic #1253, D7) — perturbation for formulas with a second complex state w:
//     z_{n+1} = f(z_n, w_n, c),   w_{n+1} = g(z_n, w_n, c),   z₀ = 0,   w₀ = 0 or the pixel c.
// That covers
//   • Phoenix, z² + c + p·z_{n−1}: w is the previous z (f reads it as PrevRef, g = z);
//   • Spider, z² + c_n with c_{n+1} = decay·c_n + z_{n+1}: w is the evolving c (w₀ = c, so the pixel
//     enters as δw₀ = ε, and f, g read only z and w);
//   • a User Equation that reads prev (g = z, w₀ = 0).
// The δ of each state is R2Plan's difference arithmetic (exact for these polynomials and quotients;
// conj / re / im / abs folds as there), with a δ per state: δz′ = f(Z + δz, W + δw, C + ε) − f(Z, W, C),
// and the same for w. The design plan (§4.4, §5.2 item 5) states the per-state δ; the scalar Phoenix
// tier it replaces carried a δ_prev too. The derivative the sink receives is ∂z/∂Re(c), carried as a
// forward-mode tangent of both states along Re c. There is no BLA in this slice (its matrices would be
// 4×4) and no rebase (restarting needs both states at the reference's start); glitches go through the
// generalised Pauldelbrot test on ∂f/∂z and secondary references, as for any formula that cannot
// rebase. Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.4.

using System;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.FFMath;
using static FracturingFog.Perturbation.R2Plan;

namespace FracturingFog.Perturbation;

public sealed class TwoStatePlan
{
    // (z, w, c, three more complex inputs) → a complex.
    internal delegate (double Re, double Im) F12(double zr, double zi, double wr, double wi, double cr, double ci,
                                                double ar, double ai, double br, double bi, double er, double ei);

    private static readonly string[] Names = ["zr", "zi", "wr", "wi", "cr", "ci", "ar", "ai", "br", "bi", "er", "ei"];

    private readonly AstNode _f, _g;
    private readonly F12 _df, _dg, _tf, _tg;
    private readonly F12? _pf, _pg;

    /// <summary>Escape radius squared (on z).</summary>
    public double Bailout2 { get; }

    /// <summary>True when w₀ is the pixel c (Spider); false when w₀ = 0 (prev).</summary>
    public bool WStartsAtC { get; }

    /// <summary>True when f or g divides (the pole test applies, as R2Plan's).</summary>
    public bool HasPoles => _pf != null || _pg != null;

    private TwoStatePlan(AstNode f, AstNode g, double bailout2, bool wStartsAtC)
    {
        _f = f; _g = g; Bailout2 = bailout2; WStartsAtC = wStartsAtC;
        // Delta / Pole: the inputs after (z, w, c) are δz, δw, ε. Tangent: t_z, t_w, t_c.
        static Cx[]? Leaves(AstNode n, Emitter em) => n switch
        {
            ZRef => em.Leaf(new Cx(em.P[0], em.P[1]), new Cx(em.P[6], em.P[7])),
            PrevRef => em.Leaf(new Cx(em.P[2], em.P[3]), new Cx(em.P[8], em.P[9])),
            CRef => em.Leaf(new Cx(em.P[4], em.P[5]), new Cx(em.P[10], em.P[11])),
            _ => null,
        };
        _df = CompileAs<F12>(f, Mode.Delta, Names, Leaves);
        _dg = CompileAs<F12>(g, Mode.Delta, Names, Leaves);
        _tf = CompileAs<F12>(f, Mode.Tangent, Names, Leaves);
        _tg = CompileAs<F12>(g, Mode.Tangent, Names, Leaves);
        _pf = HasDivNode(f) ? CompileAs<F12>(f, Mode.Pole, Names, Leaves) : null;
        _pg = HasDivNode(g) ? CompileAs<F12>(g, Mode.Pole, Names, Leaves) : null;
    }

    private static bool HasDivNode(AstNode n) => n switch
    {
        Div => true,
        Neg u => HasDivNode(u.Operand), Conj u => HasDivNode(u.Operand), ReOp u => HasDivNode(u.Operand),
        ImOp u => HasDivNode(u.Operand), AbsOp u => HasDivNode(u.Operand), Folded u => HasDivNode(u.Operand),
        Add a => HasDivNode(a.Left) || HasDivNode(a.Right),
        Sub s => HasDivNode(s.Left) || HasDivNode(s.Right),
        Mul m => HasDivNode(m.Left) || HasDivNode(m.Right),
        Pow p => HasDivNode(p.Base),
        _ => false,
    };

    // R2Plan's operator set, plus PrevRef (the second state).
    private static string? Unsupported(AstNode n) => n switch
    {
        PrevRef => null,
        Neg u => Unsupported(u.Operand), Conj u => Unsupported(u.Operand), ReOp u => Unsupported(u.Operand),
        ImOp u => Unsupported(u.Operand), Folded u => Unsupported(u.Operand),
        AbsOp u => IsReal(u.Operand) ? Unsupported(u.Operand) : "abs of a complex value (its modulus)",
        Add a => Unsupported(a.Left) ?? Unsupported(a.Right),
        Sub s => Unsupported(s.Left) ?? Unsupported(s.Right),
        Mul m => Unsupported(m.Left) ?? Unsupported(m.Right),
        Div d => Unsupported(d.Left) ?? Unsupported(d.Right),
        Pow p => p.Exponent < 0 ? "a negative power" : Unsupported(p.Base),
        _ => R2Plan.Unsupported(n),
    };

    /// <summary>True when the step reads prev (z_{n−1}).</summary>
    public static bool UsesPrev(AstNode n) => n switch
    {
        PrevRef => true,
        Neg u => UsesPrev(u.Operand), Conj u => UsesPrev(u.Operand), ReOp u => UsesPrev(u.Operand),
        ImOp u => UsesPrev(u.Operand), AbsOp u => UsesPrev(u.Operand), Folded u => UsesPrev(u.Operand),
        Add a => UsesPrev(a.Left) || UsesPrev(a.Right),
        Sub s => UsesPrev(s.Left) || UsesPrev(s.Right),
        Mul m => UsesPrev(m.Left) || UsesPrev(m.Right),
        Div d => UsesPrev(d.Left) || UsesPrev(d.Right),
        Pow p => UsesPrev(p.Base),
        _ => false,
    };

    /// <summary>A User Equation step that reads prev: w = z_{n−1} (g = z, w₀ = 0). Null with
    /// <paramref name="why"/> when the step uses an operator outside the set.</summary>
    public static TwoStatePlan? TryCreatePrev(AstNode step, double bailout2, out string? why)
    {
        why = Unsupported(step);
        return why == null ? new TwoStatePlan(step, new ZRef(), bailout2, wStartsAtC: false) : null;
    }

    private static AstNode Complex(double re, double im)
        => im == 0 ? new RealConst(re) : new Add(new RealConst(re), new Mul(new RealConst(im), new ImagUnit()));

    /// <summary>Phoenix, z² + c + p·z_{n−1}, with PhoenixKernel's bailout (512²).</summary>
    public static TwoStatePlan Phoenix(double pr, double pi)
        => new(new Add(new Add(new Pow(new ZRef(), 2), new CRef()), new Mul(Complex(pr, pi), new PrevRef())),
               new ZRef(), 512.0 * 512.0, wStartsAtC: false);

    /// <summary>Spider, z′ = z² + w, w′ = decay·w + z′ (w₀ = c), with SpiderKernel's bailout (512²).</summary>
    public static TwoStatePlan Spider(double decay)
    {
        AstNode zNext = new Add(new Pow(new ZRef(), 2), new PrevRef());
        return new(zNext, new Add(new Mul(new RealConst(decay), new PrevRef()), zNext), 512.0 * 512.0, wStartsAtC: true);
    }

    /// <summary>One step of both states at <paramref name="prec"/> bits (the reference orbit).</summary>
    public void RefStep(ref BigFloat zr, ref BigFloat zi, ref BigFloat wr, ref BigFloat wi, BigFloat cr, BigFloat ci, int prec)
    {
        var (fr, fi) = AstPlan.Eval(_f, zr, zi, wr, wi, cr, ci, prec);
        var (gr, gi) = AstPlan.Eval(_g, zr, zi, wr, wi, cr, ci, prec);
        zr = fr; zi = fi; wr = gr; wi = gi;
    }

    /// <summary>(δz, δw) ← their exact differences after one step, at reference (Z, W, C).</summary>
    public void DeltaStep(double Zr, double Zi, double Wr, double Wi, double Cr, double Ci,
                          ref double dzr, ref double dzi, ref double dwr, ref double dwi, double er, double ei)
    {
        var (a, b) = _df(Zr, Zi, Wr, Wi, Cr, Ci, dzr, dzi, dwr, dwi, er, ei);
        var (c, d) = _dg(Zr, Zi, Wr, Wi, Cr, Ci, dzr, dzi, dwr, dwi, er, ei);
        dzr = a; dzi = b; dwr = c; dwi = d;
    }

    /// <summary>The tangent of both states along Re(c), stepped at the pixel's (z, w, c).</summary>
    public void TangentStep(double zr, double zi, double wr, double wi, double cr, double ci,
                            ref double tzr, ref double tzi, ref double twr, ref double twi)
    {
        var (a, b) = _tf(zr, zi, wr, wi, cr, ci, tzr, tzi, twr, twi, 1, 0);
        var (c, d) = _tg(zr, zi, wr, wi, cr, ci, tzr, tzi, twr, twi, 1, 0);
        tzr = a; tzi = b; twr = c; twi = d;
    }

    // ∂f/∂z as its squared Frobenius size (both real directions of z).
    private double Dz2(double zr, double zi, double wr, double wi, double cr, double ci)
    {
        var (a, b) = _tf(zr, zi, wr, wi, cr, ci, 1, 0, 0, 0, 0, 0);
        var (c, d) = _tf(zr, zi, wr, wi, cr, ci, 0, 1, 0, 0, 0, 0);
        return a * a + b * b + c * c + d * d;
    }

    /// <summary>Pauldelbrot's test on f's z-derivative, generalised as R2Plan's.</summary>
    public bool IsGlitch(double zr, double zi, double wr, double wi, double Zr, double Zi, double Wr, double Wi,
                         double cr, double ci, double tau2)
        => Dz2(zr, zi, wr, wi, cr, ci) < tau2 * Dz2(Zr, Zi, Wr, Wi, cr, ci);

    /// <summary>min over f's and g's divisions of |Q + ΔQ|² / |Q|² (R2Plan.PoleRatio2).</summary>
    public double PoleRatio2(double Zr, double Zi, double Wr, double Wi, double Cr, double Ci,
                             double dzr, double dzi, double dwr, double dwi, double er, double ei)
    {
        double r = double.MaxValue;
        if (_pf != null) r = Math.Min(r, _pf(Zr, Zi, Wr, Wi, Cr, Ci, dzr, dzi, dwr, dwi, er, ei).Re);
        if (_pg != null) r = Math.Min(r, _pg(Zr, Zi, Wr, Wi, Cr, Ci, dzr, dzi, dwr, dwi, er, ei).Re);
        return r;
    }
}
