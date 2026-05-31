// AstPerturbation.cs
//
// Builds the symbolic δ-update used by Tier 4 (perturbation) deep zoom.
//
// Mathematically:
//
//     δ_{n+1}  =  p(Z_n + δ_n, C + ε)  −  p(Z_n, C)
//
//              =  Σ_{k+m≥1}  (1 / (k! m!)) · (∂^{k+m} p / ∂z^k ∂c^m)|_{Z,C} · δ^k · ε^m
//
// The Taylor expansion is exact for any polynomial in (z, c) because the
// series terminates at the polynomial's total degree. We don't have to
// truncate; the contributions of higher-order partials are identically
// zero. We iterate (k, m) up to a generous bound (32, which covers z^16
// times anything reasonable in the grammar) and skip terms whose partial
// simplifies to RealConst(0).
//
// The returned AST uses:
//   ZRef    → reference orbit Z_n (bound by perturbation emitter to Zr/Zi)
//   CRef    → view centre   C    (bound to Cr/Ci)
//   DeltaRef → per-pixel δ        (bound to dr/di)
//   EpsRef   → per-pixel ε        (bound to er/ei)

namespace FracturingFog.CalculatorGen.Parser;

public static class AstPerturbation
{
    /// <summary>Build a fully-symbolic δ-update AST from the polynomial
    /// step function. The result is run through the simplifier; constant
    /// 0 partials are skipped during construction so the output is tight
    /// without relying on further cancellation.</summary>
    public static AstNode BuildDeltaUpdate(AstNode stepFn)
    {
        AstNode result = new RealConst(0.0);
        const int maxOrder = 32;

        for (int k = 0; k <= maxOrder; k++)
        {
            for (int m = 0; m + k <= maxOrder; m++)
            {
                if (k + m == 0) continue;
                AstNode partial = stepFn;
                bool collapsed = false;
                for (int i = 0; i < k; i++)
                {
                    partial = AstSimplifier.Simplify(
                        AstDifferentiator.Diff(partial, AstDifferentiator.Var.Z));
                    if (partial is RealConst rcZ && rcZ.Value == 0.0) { collapsed = true; break; }
                }
                if (!collapsed)
                {
                    for (int j = 0; j < m; j++)
                    {
                        partial = AstSimplifier.Simplify(
                            AstDifferentiator.Diff(partial, AstDifferentiator.Var.C));
                        if (partial is RealConst rcC && rcC.Value == 0.0) { collapsed = true; break; }
                    }
                }
                if (collapsed) continue;
                if (partial is RealConst rc && rc.Value == 0.0) continue;

                double coef = 1.0 / (Factorial(k) * Factorial(m));
                AstNode term = (coef == 1.0)
                    ? partial
                    : new Mul(new RealConst(coef), partial);
                for (int i = 0; i < k; i++) term = new Mul(term, new DeltaRef());
                for (int j = 0; j < m; j++) term = new Mul(term, new EpsRef());

                result = new Add(result, term);
            }
        }

        return AstSimplifier.Simplify(result);
    }

    private static double Factorial(int n)
    {
        double r = 1.0;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }
}
