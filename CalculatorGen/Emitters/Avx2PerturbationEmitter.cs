// Avx2PerturbationEmitter.cs
//
// Emits the per-iteration δ-update body using Vector256<double> (4 lanes,
// AVX2 + FMA). Each ComplexExpr in the walk represents a 4-wide vector of
// complex δ values — one lane per pixel. Companion to
// Avx512PerturbationEmitter (8-wide) and PerturbationEmitter (scalar).
//
// Bindings (locals the surrounding scope must provide):
//   ZRef     → (Zr_v, Zi_v)   reference orbit iterate, BROADCAST to all lanes
//   CRef     → (Cr_v, Ci_v)   view-centre c (broadcast; rarely used in δ body)
//   DeltaRef → (dr, di)       per-lane δ
//   EpsRef   → (er_v, ei_v)   per-lane ε
//
// Output: SSA-temp prelude + two new-value assignments:
//   Vector256<double> dr_new = …;
//   Vector256<double> di_new = …;
//
// Imag-zero optimisation is preserved (real-only subexpressions skip the
// dead-zero Im temp).
//
// AVX2 vs AVX-512: same algebra, half the lane count, FMA intrinsics come
// from the Fma class rather than baked into Avx512F.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx2PerturbationEmitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;
    private readonly string _tempPrefix;

    public Avx2PerturbationEmitter(string indent = "                    ", string tempPrefix = "p2")
    {
        _indent = indent;
        _tempPrefix = tempPrefix;
    }

    protected override string ZRe     => "Zr_v";
    protected override string ZIm     => "Zi_v";
    protected override string CRe     => "Cr_v";
    protected override string CIm     => "Ci_v";
    protected override string DRe     => throw new InvalidOperationException("DRef unsupported in AVX-2 perturbation");
    protected override string DIm     => throw new InvalidOperationException("DRef unsupported in AVX-2 perturbation");
    protected override string DeltaRe => "dr";
    protected override string DeltaIm => "di";
    protected override string EpsRe   => "er_v";
    protected override string EpsIm   => "ei_v";

    private string NewTemp(string kind)
    {
        _tmpId++;
        return $"{_tempPrefix}{kind}{_tmpId}";
    }

    private ComplexExpr Bind(string re, string im)
    {
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector256<double>.Zero", ImZero: true);
    }

    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
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
        if (a.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Add({a.Re}, {b.Re})", $"Avx.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Subtract({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(
                NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx.Subtract(Vector256<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Subtract({a.Re}, {b.Re})", $"Avx.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Multiply({a.Re}, {b.Re})");
        if (a.ImZero)
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Re}, {b.Im})");
        if (b.ImZero)
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Im}, {b.Re})");
        // Full complex: (ac − bd, ad + bc) — emitted with FMA intrinsics.
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

    public string EmitDeltaBody(AstNode root)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector256<double> dr_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector256<double> di_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
