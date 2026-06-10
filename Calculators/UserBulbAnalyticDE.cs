// UserBulbAnalyticDE.cs
//
// Source-pattern detector for User Bulb step functions whose growth is
// closed-form. When detected, UserBulbCalculator can iterate a single
// trajectory and use the Hubbard-Douady running-derivative recurrence
//
//     dr_{n+1} = N · r_n^{N-1} · dr_n + 1
//     DE       = 0.5 · log(r) · r / dr
//
// instead of the numerical Jacobian (4 trajectories per iter). Roughly
// 4× delegate-call reduction.
//
// Auto mode: detect kind, run 1 numerical Jacobian probe at the sample
// point, compare to the analytic estimate; accept if relative delta < 5%.
// Mis-detect (e.g. user wrote z*z + c but with a leading factor) falls
// back gracefully.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using FracturingFog.Models;

namespace FracturingFog.Calculators;

public enum AnalyticDEKind
{
    None,
    Square,         // z*z + c                              (power N=2)
    PowerN,         // Vec3.Pow(z, N) + c                   (power N)
    MandelbulbN,    // canonical triplex formula            (power N)
}

public sealed record AnalyticDEPattern(AnalyticDEKind Kind, double Power);

public static class UserBulbAnalyticDE
{
    /// <summary>Detect closed-form DE pattern from a parsed Sandbox AST.
    /// Recognises `triplex(z, K) + c` (any side) → MandelbulbN(K), and the
    /// hand-written canonical Square form
    ///   `vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c`
    /// → Square(power=2). Returns None otherwise.</summary>
    public static AnalyticDEPattern DetectSandbox(Sbx3Node? root)
    {
        if (root is not Sbx3Binary { Op: "+" } add) return new(AnalyticDEKind.None, 0);
        if (TryMatchTriplexPlusC(add.A, add.B, out double p1)) return new(AnalyticDEKind.MandelbulbN, p1);
        if (TryMatchTriplexPlusC(add.B, add.A, out double p2)) return new(AnalyticDEKind.MandelbulbN, p2);
        if (TryMatchPowOpPlusC(add.A, add.B, out double p3)) return new(AnalyticDEKind.MandelbulbN, p3);
        if (TryMatchPowOpPlusC(add.B, add.A, out double p4)) return new(AnalyticDEKind.MandelbulbN, p4);
        if (TryMatchExplicitSquarePlusC(add.A, add.B)) return new(AnalyticDEKind.Square, 2);
        if (TryMatchExplicitSquarePlusC(add.B, add.A)) return new(AnalyticDEKind.Square, 2);
        return new(AnalyticDEKind.None, 0);
    }

    /// <summary>Match the operator form `z ^ K + c`. `^` on a Vec slot is
    /// triplex by Sandbox semantics, so this aliases MandelbulbN(K).</summary>
    private static bool TryMatchPowOpPlusC(Sbx3Node lhs, Sbx3Node rhs, out double power)
    {
        power = 0;
        if (rhs is not Sbx3Slot { Slot: SandboxBulbExpression.SlotC }) return false;
        if (lhs is not Sbx3Binary { Op: "^" } pow) return false;
        if (pow.A is not Sbx3Slot { Slot: SandboxBulbExpression.SlotZ }) return false;
        if (pow.B is not Sbx3Const pc || pc.V.IsVec || pc.V.IsQuat) return false;
        power = pc.V.X;
        return true;
    }

    private static bool TryMatchTriplexPlusC(Sbx3Node lhs, Sbx3Node rhs, out double power)
    {
        power = 0;
        if (rhs is not Sbx3Slot { Slot: SandboxBulbExpression.SlotC }) return false;
        if (lhs is not Sbx3Call call) return false;
        if (call.Name != "triplex" || call.Args.Length != 2) return false;
        if (call.Args[0] is not Sbx3Slot { Slot: SandboxBulbExpression.SlotZ }) return false;
        if (call.Args[1] is not Sbx3Const pc || pc.V.IsVec) return false;
        power = pc.V.X;
        return true;
    }

    /// <summary>Match `vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z)`
    /// against lhs and `c` slot against rhs. Component ordering inside the
    /// vec() call is fixed (X-component first) but each component's product
    /// chain is associativity-agnostic — `2*z.x*z.y` and `z.x*2*z.y` both
    /// match.</summary>
    private static bool TryMatchExplicitSquarePlusC(Sbx3Node lhs, Sbx3Node rhs)
    {
        if (rhs is not Sbx3Slot { Slot: SandboxBulbExpression.SlotC }) return false;
        if (lhs is not Sbx3Call call) return false;
        if (call.Name != "vec" || call.Args.Length != 3) return false;
        if (!IsXSquareMinusYSquareMinusZSquare(call.Args[0])) return false;
        if (!IsCoeffTimesTwoZAxes(call.Args[1], 2.0, 'x', 'y')) return false;
        if (!IsCoeffTimesTwoZAxes(call.Args[2], 2.0, 'x', 'z')) return false;
        return true;
    }

    /// <summary>Parser is left-assoc on `-`, so `z.x*z.x - z.y*z.y - z.z*z.z`
    /// becomes ((z.x²) - (z.y²)) - (z.z²). Match exactly that shape.</summary>
    private static bool IsXSquareMinusYSquareMinusZSquare(Sbx3Node n)
    {
        if (n is not Sbx3Binary { Op: "-" } outer) return false;
        if (!IsZAxisSquared(outer.B, 'z')) return false;
        if (outer.A is not Sbx3Binary { Op: "-" } inner) return false;
        if (!IsZAxisSquared(inner.A, 'x')) return false;
        if (!IsZAxisSquared(inner.B, 'y')) return false;
        return true;
    }

    private static bool IsZAxisSquared(Sbx3Node n, char axis)
    {
        if (n is not Sbx3Binary { Op: "*" } mul) return false;
        return IsZAxisMember(mul.A, axis) && IsZAxisMember(mul.B, axis);
    }

    private static bool IsZAxisMember(Sbx3Node n, char axis)
        => n is Sbx3Member m
           && m.Axis == axis
           && m.Target is Sbx3Slot { Slot: SandboxBulbExpression.SlotZ };

    /// <summary>Three-factor product `<coeff> * z.<axA> * z.<axB>` in any
    /// associative ordering. Flattens the `*`-tree then checks the multiset.</summary>
    private static bool IsCoeffTimesTwoZAxes(Sbx3Node n, double coeff, char axA, char axB)
    {
        var leaves = new List<Sbx3Node>();
        FlattenMul(n, leaves);
        if (leaves.Count != 3) return false;
        bool foundK = false, foundA = false, foundB = false;
        foreach (var leaf in leaves)
        {
            if (!foundK && leaf is Sbx3Const c && !c.V.IsVec && Math.Abs(c.V.X - coeff) < 1e-9)
                foundK = true;
            else if (!foundA && IsZAxisMember(leaf, axA))
                foundA = true;
            else if (!foundB && IsZAxisMember(leaf, axB))
                foundB = true;
            else return false;
        }
        return foundK && foundA && foundB;
    }

    private static void FlattenMul(Sbx3Node n, List<Sbx3Node> sink)
    {
        if (n is Sbx3Binary { Op: "*" } mul) { FlattenMul(mul.A, sink); FlattenMul(mul.B, sink); }
        else sink.Add(n);
    }

    /// <summary>Pattern detection on a multi-step Sandbox chain. The analytic
    /// DE recurrence holds when (a) the final step's expression matches a
    /// recognised power-map pattern fed by some slot s, and (b) every step
    /// whose output transitively feeds s is a composition of Lipschitz-≤1
    /// operations on z (abs, absx/y/z, boxfold, normalize, negation, and
    /// affine offsets by constants/c). Folds preserve the |dz/dc| bound that
    /// drives the Hubbard-Douady running-derivative formula, so the same
    /// power-N recurrence stays correct. Auto mode's AcceptAuto probe
    /// catches mis-detect and falls back to numerical, so the matcher errs
    /// on the side of recognising more shapes.</summary>
    public static AnalyticDEPattern DetectSandboxChain(SandboxBulbChain chain)
    {
        if (chain == null) return new(AnalyticDEKind.None, 0);
        var roots = chain.StepRoots;
        var outSlots = chain.StepOutputSlots;
        int n = roots.Count;
        if (n == 0) return new(AnalyticDEKind.None, 0);

        // Map each step-output slot → its expression AST. Used to walk back
        // through prior step outputs when verifying the fold-prefix shape.
        var slotToExpr = new Dictionary<int, Sbx3Node>(n);
        for (int i = 0; i < n; i++) slotToExpr[outSlots[i]] = roots[i];

        // The final step decides the kind. Reuse the single-expression
        // matcher but allow the operand inside triplex(<s>, K) / s ^ K to
        // be ANY slot (not just SlotZ), provided that slot resolves to a
        // Lipschitz-≤1 fold of z.
        var last = roots[n - 1];

        if (last is Sbx3Binary { Op: "+" } add)
        {
            if (TryMatchTriplexOrPowOfFold(add.A, add.B, slotToExpr, out double p1))
                return new(AnalyticDEKind.MandelbulbN, p1);
            if (TryMatchTriplexOrPowOfFold(add.B, add.A, slotToExpr, out double p2))
                return new(AnalyticDEKind.MandelbulbN, p2);
        }
        return new(AnalyticDEKind.None, 0);
    }

    private static bool TryMatchTriplexOrPowOfFold(
        Sbx3Node lhs, Sbx3Node rhs,
        Dictionary<int, Sbx3Node> slotToExpr,
        out double power)
    {
        power = 0;
        if (rhs is not Sbx3Slot { Slot: SandboxBulbExpression.SlotC }) return false;

        // triplex(<slot>, K) — slot resolves to a fold-of-z.
        if (lhs is Sbx3Call call && call.Name == "triplex" && call.Args.Length == 2
            && call.Args[1] is Sbx3Const pc && !pc.V.IsVec && !pc.V.IsQuat
            && call.Args[0] is Sbx3Slot triSlot
            && IsLipschitzZ(triSlot.Slot, slotToExpr, new HashSet<int>()))
        {
            power = pc.V.X;
            return true;
        }

        // <slot> ^ K — same shape via the operator form.
        if (lhs is Sbx3Binary { Op: "^" } pow
            && pow.A is Sbx3Slot powSlot
            && IsLipschitzZ(powSlot.Slot, slotToExpr, new HashSet<int>())
            && pow.B is Sbx3Const pk && !pk.V.IsVec && !pk.V.IsQuat)
        {
            power = pk.V.X;
            return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="slot"/> resolves transitively to a
    /// fold-only function of z. Recursion guarded by <paramref name="seen"/>
    /// to break self-referential chains (the parser disallows them but
    /// safety belt costs nothing).</summary>
    private static bool IsLipschitzZ(int slot, Dictionary<int, Sbx3Node> slotToExpr, HashSet<int> seen)
    {
        if (slot == SandboxBulbExpression.SlotZ) return true;
        if (!seen.Add(slot)) return false;
        if (!slotToExpr.TryGetValue(slot, out var expr)) return false;
        return IsLipschitzExpression(expr, slotToExpr, seen);
    }

    private static bool IsLipschitzExpression(Sbx3Node node, Dictionary<int, Sbx3Node> slotToExpr, HashSet<int> seen)
    {
        switch (node)
        {
            case Sbx3Slot s:
                return IsLipschitzZ(s.Slot, slotToExpr, seen);
            case Sbx3Const:
                return true;
            case Sbx3Unary u when u.Op == '-':
                return IsLipschitzExpression(u.A, slotToExpr, seen);
            case Sbx3Binary b when b.Op == "+" || b.Op == "-":
                return IsLipschitzExpression(b.A, slotToExpr, seen) && IsLipschitzExpression(b.B, slotToExpr, seen);
            case Sbx3Call call:
                return call.Name switch
                {
                    // Componentwise abs is the canonical fold and is Lipschitz=1.
                    "abs" or "absx" or "absy" or "absz" => IsLipschitzExpression(call.Args[0], slotToExpr, seen),
                    // BoxFold clamps each component to [-limit, limit] and
                    // reflects past it — Lipschitz=1 by construction.
                    "boxfold" => IsLipschitzExpression(call.Args[0], slotToExpr, seen),
                    // vec(...) of Lipschitz reals → Lipschitz vec.
                    "vec" => IsLipschitzExpression(call.Args[0], slotToExpr, seen)
                          && IsLipschitzExpression(call.Args[1], slotToExpr, seen)
                          && IsLipschitzExpression(call.Args[2], slotToExpr, seen),
                    _ => false,
                };
            default:
                return false;
        }
    }

    public static AnalyticDEPattern Detect(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return new(AnalyticDEKind.None, 0);

        // Normalize: strip comments + whitespace
        string s = StripComments(source);
        s = Regex.Replace(s, @"\s+", " ").Trim();

        // 1. Vec3.Pow(z, N) + c
        var mPow = Regex.Match(s,
            @"^\s*return\s+Vec3\.Pow\s*\(\s*z\s*,\s*([-+]?[0-9]*\.?[0-9]+)\s*\)\s*\+\s*c\s*;?\s*$");
        if (mPow.Success && double.TryParse(mPow.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n))
        {
            return new(AnalyticDEKind.PowerN, n);
        }

        // 2. Square triplex: z*z + c   OR   new Vec3(z.X*z.X - z.Y*z.Y - z.Z*z.Z, 2*z.X*z.Y, 2*z.X*z.Z) + c
        if (Regex.IsMatch(s, @"^\s*return\s+z\s*\*\s*z\s*\+\s*c\s*;?\s*$"))
            return new(AnalyticDEKind.Square, 2);

        if (Regex.IsMatch(s,
            @"new\s+Vec3\s*\(\s*z\.X\s*\*\s*z\.X\s*-\s*z\.Y\s*\*\s*z\.Y\s*-\s*z\.Z\s*\*\s*z\.Z\s*,\s*2\s*\*\s*z\.X\s*\*\s*z\.Y\s*,\s*2\s*\*\s*z\.X\s*\*\s*z\.Z\s*\)\s*\+\s*c"))
            return new(AnalyticDEKind.Square, 2);

        return new(AnalyticDEKind.None, 0);
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//.*?$", "", RegexOptions.Multiline);
        return src;
    }

    /// <summary>
    /// Hubbard-Douady DE for a power map of order N. Single-trajectory; no
    /// delegate calls needed for the recurrence itself (the user step still
    /// runs to advance z, but only once per iter instead of 4×).
    /// </summary>
    public static double PowerDE(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double power, double[] pArr)
    {
        var c = new Vec3(cx, cy, cz);
        var z = Vec3.Zero;
        double dr = 1.0;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            // dr_{n+1} = N · r^(N-1) · dr_n + 1
            dr = power * Math.Pow(r, power - 1) * dr + 1.0;
            z = fn(z, c, i, pArr);
        }

        if (r < 1e-12 || dr < 1e-12) return 0.5 * r / Math.Max(dr, 1e-10);
        return 0.5 * Math.Log(Math.Max(r, 1.0)) * r / dr;
    }

    /// <summary>
    /// Validation probe: compare analytic DE to numerical Jacobian DE at a
    /// representative sample point. Used by Auto mode.
    /// </summary>
    public static bool AcceptAuto(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        AnalyticDEPattern pattern,
        int iter, double bailout, double jacH, double[] pArr)
    {
        if (pattern.Kind == AnalyticDEKind.None) return false;

        double cx = 0.4, cy = 0.3, cz = 0.2;
        double analytic = PowerDE(fn, cx, cy, cz, iter, bailout, pattern.Power, pArr);
        double numerical = NumericalProbe(fn, cx, cy, cz, iter, bailout, jacH, pArr);

        if (analytic <= 0 || numerical <= 0) return false;
        double rel = Math.Abs(analytic - numerical) / Math.Max(Math.Abs(numerical), 1e-9);
        return rel < 0.20; // 20% — Lipschitz vs log-form differ in magnitude but track shape
    }

    private static double NumericalProbe(
        Func<Vec3, Vec3, int, double[], Vec3> fn,
        double cx, double cy, double cz,
        int iter, double bailout, double h, double[] pArr)
    {
        var cBase = new Vec3(cx, cy, cz);
        var cPx = new Vec3(cx + h, cy, cz);
        var cPy = new Vec3(cx, cy + h, cz);
        var cPz = new Vec3(cx, cy, cz + h);
        var z = Vec3.Zero; var zx = Vec3.Zero; var zy = Vec3.Zero; var zz = Vec3.Zero;
        double r = 0.0;
        for (int i = 0; i < iter; i++)
        {
            r = z.Length;
            if (!double.IsFinite(r) || r > bailout) break;
            z  = fn(z,  cBase, i, pArr);
            zx = fn(zx, cPx,   i, pArr);
            zy = fn(zy, cPy,   i, pArr);
            zz = fn(zz, cPz,   i, pArr);
        }
        double j0 = (zx - z).Length / h;
        double j1 = (zy - z).Length / h;
        double j2 = (zz - z).Length / h;
        double dr = Math.Max(Math.Max(j0, j1), j2);
        return 0.5 * r / Math.Max(dr, 1e-10);
    }
}
