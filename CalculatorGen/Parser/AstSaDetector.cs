// AstSaDetector.cs
//
// Detects whether the equation has the shape z^d + c (or c + z^d) for
// some integer d in 2..5. The SA prelude's recurrence has closed-form
// coefficients for that family — for any other shape, SA is off.
//
// Matches against AST shapes the parser actually produces:
//   z*z + c            → Mul(ZRef, ZRef) chain → d=2
//   z*z*z + c          → Mul(Mul(z,z), z) → d=3
//   z^4 + c            → Pow(ZRef, 4) → d=4
//   c + z*z            → symmetric — Add commutes
// Anything more complex (constants, sums, products of c) → no SA.

namespace FracturingFog.CalculatorGen.Parser;

public static class AstSaDetector
{
    /// <summary>Returns the degree d (2..5) when the equation matches
    /// z^d + c; 0 otherwise.</summary>
    public static int DetectZdPlusC(AstNode root)
    {
        if (root is not Add a) return 0;
        // c + z^d
        if (a.Left is CRef && TryCountZ(a.Right, out int d1) && d1 is >= 2 and <= 5) return d1;
        // z^d + c
        if (a.Right is CRef && TryCountZ(a.Left, out int d2) && d2 is >= 2 and <= 5) return d2;
        return 0;
    }

    private static bool TryCountZ(AstNode n, out int d)
    {
        switch (n)
        {
            case ZRef:
                d = 1; return true;
            case Pow p when p.Base is ZRef && p.Exponent >= 1:
                d = p.Exponent; return true;
            case Mul m:
                if (TryCountZ(m.Left, out int dl) && TryCountZ(m.Right, out int dr))
                { d = dl + dr; return true; }
                break;
        }
        d = 0; return false;
    }

    /// <summary>
    /// Generalised SA detector. Returns the z-only polynomial F(z) plus
    /// its total z-degree when the equation has the shape F(z) + c (or
    /// c + F(z)), where F is any polynomial in z with no CRef, Conj,
    /// Folded, or Div nodes. Returns (null, 0) otherwise.
    ///
    /// Examples that match:
    ///   z*z + c            → F = z*z,         degree 2
    ///   z^3 + a*z + c      → F = z^3 + a*z,   degree 3
    ///   2*z^2 - 0.5*z + c  → F = 2*z^2 − 0.5*z, degree 2
    ///   c + z^4 + z        → F = z^4 + z,     degree 4
    ///
    /// Generic SA recurrence is then derived from F's symbolic
    /// derivatives p_k(z) = ∂^k F / ∂z^k via Taylor expansion at Z_n:
    ///   S_{n+1,K} = Σ_{k=1..min(d,K)} (1/k!) · p_k(Z_n) · (δ^k)_K + [K==1]
    /// </summary>
    public static (AstNode? polyZ, int degree) DetectPolyInZPlusC(AstNode root)
    {
        if (root is not Add a) return (null, 0);
        AstNode? polyZ = null;
        if (a.Left is CRef && IsPureZPolynomial(a.Right, out int d1) && d1 >= 2)
            { polyZ = a.Right; return (polyZ, d1); }
        if (a.Right is CRef && IsPureZPolynomial(a.Left, out int d2) && d2 >= 2)
            { polyZ = a.Left; return (polyZ, d2); }
        return (null, 0);
    }

    /// <summary>True when <paramref name="n"/> is a polynomial in z
    /// only — no CRef anywhere. Computes the maximum z-degree.</summary>
    private static bool IsPureZPolynomial(AstNode n, out int degree)
    {
        degree = 0;
        switch (n)
        {
            case RealConst:
                degree = 0; return true;
            case ZRef:
                degree = 1; return true;
            case CRef:
                return false;
            case Conj:
            case Folded:
            case Sin:
            case Cos:
            case Exp:
            case Log:
            case Arg:
            case Atan2:
            case Min:
            case Max:
            case Mod:
            case If:
            case PrevRef:
            case IterRef:
                return false;
            case Neg ng:
                return IsPureZPolynomial(ng.Operand, out degree);
            case Pow p when p.Base is ZRef && p.Exponent >= 0:
                degree = p.Exponent; return true;
            case Pow pp:
                if (IsPureZPolynomial(pp.Base, out int db) && pp.Exponent >= 0)
                { degree = db * pp.Exponent; return true; }
                return false;
            case Add ad:
                if (IsPureZPolynomial(ad.Left, out int dla)
                 && IsPureZPolynomial(ad.Right, out int dra))
                { degree = System.Math.Max(dla, dra); return true; }
                return false;
            case Sub sb:
                if (IsPureZPolynomial(sb.Left, out int dls)
                 && IsPureZPolynomial(sb.Right, out int drs))
                { degree = System.Math.Max(dls, drs); return true; }
                return false;
            case Mul ml:
                if (IsPureZPolynomial(ml.Left, out int dlm)
                 && IsPureZPolynomial(ml.Right, out int drm))
                { degree = dlm + drm; return true; }
                return false;
            case Div:
                return false;
        }
        return false;
    }
}
