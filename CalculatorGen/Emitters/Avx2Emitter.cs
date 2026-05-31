// Avx2Emitter.cs
//
// Emits the per-iteration body using Vector256<double> (4 lanes, AVX2 +
// FMA). Each ComplexExpr in the walk represents a register-sized vector
// of complex values — lanes are four neighbouring pixels along x.
//
// The emitter accumulates a prelude of SSA-style temporary assignments
// (one per intermediate ComplexExpr) so the rendered code is readable
// instead of a single tower of nested intrinsic calls. The JIT folds
// constant temps so there's no runtime cost.
//
// Important: one Avx2Emitter instance per body emission. The prelude
// accumulates per-instance; reusing it across two bodies would leak temp
// names and confuse the rendered output.
//
// FMA usage
//   • Avx2 + Fma are separate intrinsic classes in .NET. We emit
//     Fma.MultiplyAdd(a, b, c)      = a·b + c
//     Fma.MultiplyAddNegated(a, b, c) = -(a·b) + c
//     for the complex multiply step: (ac − bd, ad + bc).
//   • The generated calculator guards with `Avx2.IsSupported &&
//     Fma.IsSupported` at the call site and falls back to scalar; that
//     fallback is wired by the template, not this emitter.
//
// Imag-zero optimisation
//   When a subexpression is provably real (ImZero=true on its
//   ComplexExpr), the emitter binds only the Re temp and reuses
//   `Vector256<double>.Zero` as the Im placeholder. Mul shortcuts to
//   scalar-by-complex form; Add/Sub elide the dead-zero add. Cuts the
//   emitted prelude by ~30 % for typical polynomial steps + derivative
//   updates.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx2Emitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;
    private readonly string _tempPrefix;

    public Avx2Emitter(string indent = "                ", string tempPrefix = "t")
    {
        _indent = indent;
        _tempPrefix = tempPrefix;
    }

    protected override string ZRe => "zr";
    protected override string ZIm => "zi";
    protected override string CRe => "cr";
    protected override string CIm => "ci";
    protected override string DRe => "dr";
    protected override string DIm => "di";

    private string NewTemp(string kind)
    {
        _tmpId++;
        return $"{_tempPrefix}{kind}{_tmpId}";
    }

    /// <summary>Bind a Re temp and an Im temp.</summary>
    private ComplexExpr Bind(string re, string im)
    {
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    /// <summary>Bind only a Re temp; Im is the zero literal. Used when the
    /// subexpression is provably real-valued.</summary>
    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector256<double>.Zero", ImZero: true);
    }

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        return BindReOnly($"Vector256.Create({lit})");
    }

    protected override ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Add({a.Re}, {b.Re})");
        if (a.ImZero) // a.Im is the zero vector; result.Im = b.Im
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Add({a.Re}, {b.Re})", $"Avx.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Subtract({a.Re}, {b.Re})");
        if (a.ImZero) // result.Im = -b.Im
            return new ComplexExpr(
                NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx.Subtract(Vector256<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero) // result.Im = a.Im
            return new ComplexExpr(NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Subtract({a.Re}, {b.Re})", $"Avx.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Multiply({a.Re}, {b.Re})");
        if (a.ImZero) // (a.Re + 0i)·(b.Re + b.Im·i) = a.Re·b.Re + a.Re·b.Im·i
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Re}, {b.Im})");
        if (b.ImZero) // (a.Re + a.Im·i)·(b.Re + 0i) = a.Re·b.Re + a.Im·b.Re·i
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Im}, {b.Re})");
        // Full complex: (ac − bd, ad + bc)
        string re = $"Fma.MultiplyAddNegated({a.Im}, {b.Im}, Avx.Multiply({a.Re}, {b.Re}))";
        string im = $"Fma.MultiplyAdd({a.Re}, {b.Im}, Avx.Multiply({a.Im}, {b.Re}))";
        return Bind(re, im);
    }

    protected override ComplexExpr OpNeg(ComplexExpr a)
    {
        if (a.ImZero)
            return BindReOnly($"Avx.Subtract(Vector256<double>.Zero, {a.Re})");
        return Bind($"Avx.Subtract(Vector256<double>.Zero, {a.Re})",
                    $"Avx.Subtract(Vector256<double>.Zero, {a.Im})");
    }

    /// <summary>Bind a single Re temp without claiming ImZero. Used when
    /// Add/Sub need to bind the Re temp but the Im part is reused
    /// from one of the inputs (no new bind needed).</summary>
    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
    }

    /// <summary>Render the prelude SSA temps + the two final new-value
    /// assignments. <paramref name="prefix"/> determines the output names
    /// (e.g. "z" → zr_new, zi_new ; "d" → dr_new, di_new).</summary>
    public string EmitNewValueBody(AstNode root, string prefix)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector256<double> ").Append(prefix).Append("r_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector256<double> ").Append(prefix).Append("i_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
