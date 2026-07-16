// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Avx512PerturbationEmitter.cs
//
// Emits the per-iteration δ-update body using Vector512<double> (8 lanes,
// AVX-512). Each ComplexExpr in the walk represents an 8-wide vector of
// complex δ values — one lane per pixel. Companion to PerturbationEmitter
// (scalar) and Avx2Emitter (4-wide z-update).
//
// Bindings (locals the surrounding scope must provide):
//   ZRef     → (Zr, Zi)   reference orbit iterate, BROADCAST to all lanes
//   CRef     → (Cr, Ci)   view-centre c (broadcast; rarely used in δ body)
//   DeltaRef → (dr, di)   per-lane δ
//   EpsRef   → (er, ei)   per-lane ε
//
// Output: SSA-temp prelude + two new-value assignments:
//   Vector512<double> dr_new = …;
//   Vector512<double> di_new = …;
//
// Imag-zero optimisation is preserved (real-only subexpressions skip the
// dead-zero Im temp).
//
// AVX-512 vs AVX2: same algebra, 2× the lane count. The lane-management
// code (active mask, bailout broadcast, blend) is the same shape; only
// the intrinsic class names change (Avx512F vs Avx2/Fma).
//
// The generated calculator guards with `Avx512F.IsSupported` at the call
// site. When unsupported the template's scalar perturbation path runs
// instead — no functional difference, just lane count.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx512PerturbationEmitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;
    private readonly string _tempPrefix;

    public Avx512PerturbationEmitter(string indent = "                    ", string tempPrefix = "p")
    {
        _indent = indent;
        _tempPrefix = tempPrefix;
    }

    protected override string ZRe     => "Zr_v";
    protected override string ZIm     => "Zi_v";
    protected override string CRe     => "Cr_v";
    protected override string CIm     => "Ci_v";
    protected override string DRe     => throw new InvalidOperationException("DRef unsupported in AVX-512 perturbation");
    protected override string DIm     => throw new InvalidOperationException("DRef unsupported in AVX-512 perturbation");
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
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector512<double>.Zero", ImZero: true);
    }

    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
    }

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        return BindReOnly($"Vector512.Create({lit})");
    }

    protected override ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Add({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx512F.Add({a.Re}, {b.Re})", $"Avx512F.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Subtract({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(
                NewBoundRe($"Avx512F.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx512F.Subtract(Vector512<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx512F.Subtract({a.Re}, {b.Re})", $"Avx512F.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Multiply({a.Re}, {b.Re})");
        if (a.ImZero)
            return Bind($"Avx512F.Multiply({a.Re}, {b.Re})",
                        $"Avx512F.Multiply({a.Re}, {b.Im})");
        if (b.ImZero)
            return Bind($"Avx512F.Multiply({a.Re}, {b.Re})",
                        $"Avx512F.Multiply({a.Im}, {b.Re})");
        // Full complex: (ac − bd, ad + bc) — emitted with FMA intrinsics.
        string re = $"Avx512F.FusedMultiplyAddNegated({a.Im}, {b.Im}, Avx512F.Multiply({a.Re}, {b.Re}))";
        string im = $"Avx512F.FusedMultiplyAdd({a.Re}, {b.Im}, Avx512F.Multiply({a.Im}, {b.Re}))";
        return Bind(re, im);
    }

    protected override ComplexExpr OpNeg(ComplexExpr a)
    {
        if (a.ImZero)
            return BindReOnly($"Avx512F.Subtract(Vector512<double>.Zero, {a.Re})");
        return Bind($"Avx512F.Subtract(Vector512<double>.Zero, {a.Re})",
                    $"Avx512F.Subtract(Vector512<double>.Zero, {a.Im})");
    }

    public string EmitDeltaBody(AstNode root)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector512<double> dr_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector512<double> di_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
