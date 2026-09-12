// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MathmlImporter.cs
//
// #764 — import pasted presentation MathML into the User Equation dialog by
// converting it to the CalcGen DSL. The reverse of AstMathmlPrinter (#756).
//
// Strategy (per the #757 spike, Docs/Technical/UE-Math-Import-Feasibility.md):
// walk the MathML tree, emit a DSL string (generously parenthesised — the DSL
// parser normalises precedence), then round-trip it through EquationParser.Parse
// so the result is guaranteed valid or we report a clear error. Anything outside
// the DSL vocabulary (unknown identifiers/functions, sums, integrals, matrices
// that are not a cases block) is rejected with a message naming the construct —
// never silently dropped.
//
// Scope: our own AstMathmlPrinter output plus common external variants
// (namespace present/absent, <mfenced>, invisible-times / function-application
// operators, numeric and common named entities, msub z_{n-1}). tan/sinh/cosh/
// tanh are accepted because the DSL supports them even though there is no AST
// node — we emit DSL text, not an AstNode.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FracturingFog.CalculatorGen.Parser;

/// <summary>Result of a MathML→DSL import. <see cref="Ok"/> false carries a
/// human-readable <see cref="Error"/>; the dialog surfaces it in the status bar.</summary>
public sealed record MathmlImportResult(bool Ok, string Dsl, string? Error);

public static class MathmlImporter
{
    private sealed class ImportException(string message) : Exception(message);

    // Function name -> (dsl name, arity). ln maps to the DSL's natural log `log`.
    private static readonly Dictionary<string, (string Dsl, int Arity)> Funcs = new(StringComparer.Ordinal)
    {
        ["sin"] = ("sin", 1), ["cos"] = ("cos", 1), ["tan"] = ("tan", 1),
        ["sinh"] = ("sinh", 1), ["cosh"] = ("cosh", 1), ["tanh"] = ("tanh", 1),
        ["exp"] = ("exp", 1), ["log"] = ("log", 1), ["ln"] = ("log", 1),
        ["sqrt"] = ("sqrt", 1), ["sqr"] = ("sqr", 1), ["arg"] = ("arg", 1),
        ["asin"] = ("asin", 1), ["acos"] = ("acos", 1), ["atan"] = ("atan", 1),
        ["arcsin"] = ("asin", 1), ["arccos"] = ("acos", 1), ["arctan"] = ("atan", 1),
        ["asinh"] = ("asinh", 1), ["acosh"] = ("acosh", 1), ["atanh"] = ("atanh", 1),
        ["arcsinh"] = ("asinh", 1), ["arccosh"] = ("acosh", 1), ["arctanh"] = ("atanh", 1),
        ["abs"] = ("abs", 1), ["re"] = ("re", 1), ["im"] = ("im", 1),
        ["conj"] = ("conj", 1), ["fold"] = ("fold", 1),
        ["floor"] = ("floor", 1), ["round"] = ("round", 1), ["ceil"] = ("ceil", 1),
        ["trunc"] = ("trunc", 1), ["fract"] = ("fract", 1), ["sign"] = ("sign", 1),
        ["min"] = ("min", 2), ["max"] = ("max", 2), ["mod"] = ("mod", 2),
        ["atan2"] = ("atan2", 2), ["pow"] = ("pow", 2), ["clamp"] = ("clamp", 3),
    };

    // Common MathML named entities → literal unicode, so XDocument.Parse (which
    // has no DTD) doesn't choke. Numeric refs (&#x22C5;) are already valid XML.
    private static readonly (string ent, string ch)[] NamedEntities =
    {
        ("&InvisibleTimes;", "⁢"), ("&ApplyFunction;", "⁡"),
        ("&sdot;", "⋅"), ("&middot;", "·"), ("&times;", "×"),
        ("&minus;", "−"), ("&plus;", "+"), ("&divide;", "÷"),
        ("&le;", "≤"), ("&ge;", "≥"), ("&ne;", "≠"),
        ("&leq;", "≤"), ("&geq;", "≥"),
        ("&PlusMinus;", "±"), ("&nbsp;", " "),
        ("&pi;", "π"), ("&pgr;", "π"),
    };

    /// <summary>Convert a presentation-MathML string to a CalcGen DSL string.</summary>
    public static MathmlImportResult Import(string? mathml)
    {
        string source = mathml ?? string.Empty;   // non-null for LoadMathRoot below
        if (string.IsNullOrWhiteSpace(source))
            return new MathmlImportResult(false, string.Empty, "Clipboard has no text to import.");

        string dsl;
        try
        {
            XElement root = LoadMathRoot(source);
            dsl = ConvertRow(Meaningful(root.Elements()));
            if (string.IsNullOrWhiteSpace(dsl))
                throw new ImportException("no convertible math content found.");
        }
        catch (ImportException ex)
        {
            return new MathmlImportResult(false, string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            return new MathmlImportResult(false, string.Empty, $"not valid MathML XML ({ex.Message}).");
        }

        // Guarantee the output is real DSL — otherwise report rather than paste junk.
        try { EquationParser.Parse(dsl); }
        catch (Exception ex)
        {
            return new MathmlImportResult(false, dsl, $"produced DSL the parser rejected: {ex.Message}");
        }
        return new MathmlImportResult(true, dsl, null);
    }

    // ── XML loading ──────────────────────────────────────────────────────────
    private static XElement LoadMathRoot(string mathml)
    {
        string s = mathml.Trim();
        s = Regex.Replace(s, "<!DOCTYPE[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\?xml[^>]*\?>", string.Empty, RegexOptions.IgnoreCase);
        foreach (var (ent, ch) in NamedEntities) s = s.Replace(ent, ch);
        // A stray fragment (no <math> wrapper) still parses if it has one root;
        // wrap in <math> so multi-sibling fragments load too.
        if (!s.TrimStart().StartsWith("<math", StringComparison.OrdinalIgnoreCase))
            s = "<math>" + s + "</math>";

        var doc = XDocument.Parse(s);
        XElement el = doc.Root ?? throw new ImportException("empty MathML.");
        if (el.Name.LocalName == "semantics") el = Meaningful(el.Elements()).FirstOrDefault()
            ?? throw new ImportException("empty <semantics>.");
        return el;
    }

    // ── element helpers ──────────────────────────────────────────────────────
    private static string LN(XElement e) => e.Name.LocalName;
    private static string Txt(XElement e) => (e.Value ?? string.Empty).Trim();

    // Drop layout-only / annotation nodes that carry no expression meaning.
    private static List<XElement> Meaningful(IEnumerable<XElement> els)
    {
        var outl = new List<XElement>();
        foreach (var e in els)
        {
            switch (LN(e))
            {
                case "mspace": case "annotation": case "annotation-xml": continue;
                case "mstyle": case "mpadded": case "semantics":
                    outl.AddRange(Meaningful(e.Elements())); break;  // unwrap
                default: outl.Add(e); break;
            }
        }
        return outl;
    }

    // ── token stream over a sibling sequence ─────────────────────────────────
    private enum TK { Operand, Func, Op, LParen, RParen, Comma }
    private readonly record struct Tok(TK Kind, string Text);

    private static string ConvertRow(List<XElement> els)
    {
        var toks = Tokenize(els);
        int pos = 0;
        string e = ParseAddSub(toks, ref pos);
        if (pos != toks.Count) throw new ImportException("unexpected trailing math after a complete expression.");
        return e;
    }

    private static List<Tok> Tokenize(List<XElement> els)
    {
        var toks = new List<Tok>();
        foreach (var el in els)
        {
            switch (LN(el))
            {
                case "mo":
                    string t = Norm(Txt(el));
                    if (t is "⁡" or "⁢" or "") break;           // apply / invisible-times / empty
                    if (t is "(" or "[" or "{") toks.Add(new(TK.LParen, "("));
                    else if (t is ")" or "]" or "}") toks.Add(new(TK.RParen, ")"));
                    else if (t == ",") toks.Add(new(TK.Comma, ","));
                    else if (IsInfix(t)) toks.Add(new(TK.Op, t));
                    else throw new ImportException($"unsupported operator '{t}'.");
                    break;
                case "mi":
                    string name = Txt(el);
                    if (Funcs.ContainsKey(name)) toks.Add(new(TK.Func, name));
                    else toks.Add(new(TK.Operand, AtomDsl(name)));
                    break;
                case "mn":
                    toks.Add(new(TK.Operand, Number(Txt(el))));
                    break;
                default:
                    toks.Add(new(TK.Operand, ConvertElement(el)));
                    break;
            }
        }
        return toks;
    }

    // Normalise the many unicode operator spellings to an ASCII-ish canonical.
    private static string Norm(string t) => t switch
    {
        "−" or "–" or "—" => "-",             // minus / en / em dash
        "⋅" or "·" or "×" or "∗" or "*" => "*", // dot / middot / times / asterisk
        "∕" or "⁄" or "/" => "/",                   // division slash / fraction slash
        "≥" or ">=" => ">=", "≤" or "<=" => "<=",
        "≠" or "!=" => "!=", "≡" => "==",
        _ => t,
    };

    private static bool IsInfix(string t) =>
        t is "+" or "-" or "*" or "/" or ">" or "<" or ">=" or "<=" or "==" or "!=" or "=";

    // ── precedence climbing (emits parenthesised DSL) ────────────────────────
    private static string ParseAddSub(List<Tok> toks, ref int pos)
    {
        string left = ParseMulDiv(toks, ref pos);
        while (pos < toks.Count && toks[pos].Kind == TK.Op && (toks[pos].Text is "+" or "-"))
        {
            string op = toks[pos].Text; pos++;
            string right = ParseMulDiv(toks, ref pos);
            left = $"({left} {op} {right})";
        }
        return left;
    }

    private static string ParseMulDiv(List<Tok> toks, ref int pos)
    {
        string left = ParseUnary(toks, ref pos);
        while (pos < toks.Count)
        {
            if (toks[pos].Kind == TK.Op && (toks[pos].Text is "*" or "/"))
            {
                string op = toks[pos].Text; pos++;
                string right = ParseUnary(toks, ref pos);
                left = $"({left}{op}{right})";
            }
            else if (StartsPrimary(toks[pos]))   // implicit multiplication: 2z, z(z+c)
            {
                string right = ParseUnary(toks, ref pos);
                left = $"({left}*{right})";
            }
            else break;
        }
        return left;
    }

    private static string ParseUnary(List<Tok> toks, ref int pos)
    {
        if (pos < toks.Count && toks[pos].Kind == TK.Op && toks[pos].Text == "-")
        {
            pos++;
            return $"(-{ParseUnary(toks, ref pos)})";
        }
        return ParsePrimary(toks, ref pos);
    }

    private static bool StartsPrimary(Tok t) => t.Kind is TK.Operand or TK.Func or TK.LParen;

    private static string ParsePrimary(List<Tok> toks, ref int pos)
    {
        if (pos >= toks.Count) throw new ImportException("expression ended unexpectedly.");
        Tok t = toks[pos];
        switch (t.Kind)
        {
            case TK.Operand: pos++; return t.Text;
            case TK.LParen:
            {
                pos++;
                string e = ParseAddSub(toks, ref pos);
                Expect(toks, ref pos, TK.RParen, "')'");
                return $"({e})";
            }
            case TK.Func:
            {
                pos++;
                var (dsl, arity) = Funcs[t.Text];
                Expect(toks, ref pos, TK.LParen, $"'(' after {t.Text}");
                var args = new List<string> { ParseAddSub(toks, ref pos) };
                while (pos < toks.Count && toks[pos].Kind == TK.Comma) { pos++; args.Add(ParseAddSub(toks, ref pos)); }
                Expect(toks, ref pos, TK.RParen, $"')' closing {t.Text}(...)");
                if (args.Count != arity)
                    throw new ImportException($"{t.Text} expects {arity} argument(s), got {args.Count}.");
                return $"{dsl}({string.Join(", ", args)})";
            }
            default:
                throw new ImportException($"unexpected '{t.Text}' in expression.");
        }
    }

    private static void Expect(List<Tok> toks, ref int pos, TK kind, string what)
    {
        if (pos >= toks.Count || toks[pos].Kind != kind)
            throw new ImportException($"expected {what}.");
        pos++;
    }

    // ── structural elements ──────────────────────────────────────────────────
    private static string ConvertElement(XElement el)
    {
        switch (LN(el))
        {
            case "mi": return AtomDsl(Txt(el));
            case "mn": return Number(Txt(el));
            case "mrow": return ConvertMrow(el);
            case "mfenced": return "(" + ConvertRow(Meaningful(el.Elements())) + ")";
            case "mstyle": case "mpadded": case "semantics":
                return ConvertRow(Meaningful(el.Elements()));
            case "msup": return ConvertSup(el);
            case "msub": return ConvertSub(el);
            case "mfrac":
            {
                var k = Meaningful(el.Elements());
                if (k.Count != 2) throw new ImportException("<mfrac> needs exactly two children.");
                return $"(({ConvertElement(k[0])})/({ConvertElement(k[1])}))";
            }
            case "msqrt": return $"sqrt({ConvertRow(Meaningful(el.Elements()))})";
            case "mroot":
            {
                var k = Meaningful(el.Elements());
                if (k.Count == 2 && Number(Txt(k[1])) == "2") return $"sqrt({ConvertElement(k[0])})";
                throw new ImportException("only square roots (<msqrt> or <mroot> index 2) are supported.");
            }
            case "mover": return ConvertOver(el);
            case "mo": throw new ImportException($"unexpected operator '{Txt(el)}'.");
            default:
                throw new ImportException($"unsupported MathML element <{LN(el)}>.");
        }
    }

    private static string ConvertMrow(XElement el)
    {
        var k = Meaningful(el.Elements());
        if (k.Count >= 2 && LN(k[0]) == "mo" && LN(k[^1]) == "mo")
        {
            string open = Txt(k[0]);
            var inner = k.GetRange(1, k.Count - 2);
            if (open is "|")           return $"abs({ConvertRow(inner)})";
            if (open is "⌊")      return $"floor({ConvertRow(inner)})";   // ⌊ ⌋
            if (open is "⌈")      return $"ceil({ConvertRow(inner)})";    // ⌈ ⌉
        }
        // Piecewise: { <mtable>…</mtable>
        if (k.Count >= 2 && LN(k[0]) == "mo" && Txt(k[0]) == "{")
        {
            var table = k.FirstOrDefault(x => LN(x) == "mtable");
            if (table != null) return ConvertCases(table);
        }
        return ConvertRow(k);
    }

    private static string ConvertSup(XElement el)
    {
        var k = Meaningful(el.Elements());
        if (k.Count != 2) throw new ImportException("<msup> needs a base and an exponent.");
        if (IsEulerE(k[0])) return $"exp({ConvertElement(k[1])})";
        string baseDsl = ConvertElement(k[0]);
        // Integer exponent → the DSL '^' operator; anything else → pow(base,exp).
        if (LN(k[1]) == "mn" && int.TryParse(Txt(k[1]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 0)
            return $"({baseDsl})^{p}";
        return $"pow({baseDsl}, {ConvertElement(k[1])})";
    }

    private static string ConvertSub(XElement el)
    {
        // Only z_{n-1} (the previous iterate) is representable in the DSL.
        var k = Meaningful(el.Elements());
        if (k.Count == 2 && LN(k[0]) == "mi" && Txt(k[0]) == "z")
        {
            string sub = ConvertRow(Meaningful(k[1].Name.LocalName == "mrow" ? k[1].Elements() : new[] { k[1] }));
            if (sub.Replace(" ", string.Empty) is "(n-1)" or "n-1") return "prev";
        }
        throw new ImportException("only z_{n-1} (prev) subscripts are supported.");
    }

    private static string ConvertOver(XElement el)
    {
        var k = Meaningful(el.Elements());
        if (k.Count == 2 && LN(k[1]) == "mo")
        {
            string acc = Txt(k[1]);
            if (acc is "¯" or "̅" or "―" or "‾")   // macron / overline variants
                return $"conj({ConvertElement(k[0])})";
        }
        throw new ImportException("only an overline accent (conjugate) is supported on <mover>.");
    }

    // Piecewise cases table -> if COND then THEN else ELSE. Recognises the two
    // row shapes AstMathmlPrinter emits (value | "if"+cond ; value | "otherwise").
    private static string ConvertCases(XElement table)
    {
        var rows = table.Elements().Where(x => LN(x) == "mtr").ToList();
        if (rows.Count != 2) throw new ImportException("only two-branch piecewise (if/else) is supported.");
        var r0 = rows[0].Elements().Where(x => LN(x) == "mtd").ToList();
        var r1 = rows[1].Elements().Where(x => LN(x) == "mtd").ToList();
        if (r0.Count < 2 || r1.Count < 1) throw new ImportException("unrecognised piecewise layout.");

        string thenExpr = ConvertRow(Meaningful(r0[0].Elements()));
        string elseExpr = ConvertRow(Meaningful(r1[0].Elements()));
        string cond = ConvertCondCell(r0[1]);
        return $"if {cond} then {thenExpr} else {elseExpr}";
    }

    // A condition cell is [<mtext>if</mtext>] termA <mo>cmp</mo> termB.
    private static string ConvertCondCell(XElement mtd)
    {
        var els = mtd.Elements().Where(x => LN(x) != "mtext" && LN(x) != "mspace").ToList();
        // The comparison may be inside a single <mrow>.
        if (els.Count == 1 && LN(els[0]) == "mrow") els = Meaningful(els[0].Elements());

        int cmp = els.FindIndex(x => LN(x) == "mo" && IsCmp(Norm(Txt(x))));
        if (cmp <= 0 || cmp >= els.Count - 1)
            throw new ImportException("condition must be 'term <comparison> term'.");
        string op = Norm(Txt(els[cmp]));
        string left = ConvertCondTerm(els.GetRange(0, cmp));
        string right = ConvertCondTerm(els.GetRange(cmp + 1, els.Count - cmp - 1));
        return $"{left} {op} {right}";
    }

    private static bool IsCmp(string t) => t is ">" or "<" or ">=" or "<=" or "==" or "!=" or "=";

    // A condition term is re(x)/im(x)/arg(x), a constant, or |x|² (→ abs, which
    // in a DSL condition means squared magnitude), or a plain sub-expression.
    private static string ConvertCondTerm(List<XElement> els)
    {
        if (els.Count == 1)
        {
            var e = els[0];
            // |x|^2  →  abs(x)   (DSL condition-abs is squared magnitude)
            if (LN(e) == "msup")
            {
                var k = Meaningful(e.Elements());
                if (k.Count == 2 && LN(k[1]) == "mn" && Txt(k[1]) == "2"
                    && LN(k[0]) == "mrow")
                {
                    var kk = Meaningful(k[0].Elements());
                    if (kk.Count >= 2 && LN(kk[0]) == "mo" && Txt(kk[0]) == "|")
                        return $"abs({ConvertRow(kk.GetRange(1, kk.Count - 2))})";
                }
            }
        }
        return ConvertRow(els);
    }

    // ── leaves ───────────────────────────────────────────────────────────────
    private static bool IsEulerE(XElement el)
    {
        if (LN(el) == "mi" && Txt(el) == "e") return true;
        if (LN(el) == "mrow")
        {
            var k = Meaningful(el.Elements());
            return k.Count == 1 && LN(k[0]) == "mi" && Txt(k[0]) == "e";
        }
        return false;
    }

    private static string AtomDsl(string name) => name switch
    {
        "z" => "z", "c" => "c", "n" => "n", "i" => "i",
        "e" => "e", "pi" or "π" => "pi", "prev" => "prev",
        _ => throw new ImportException($"unknown identifier '{name}' — the DSL only knows z, c, n, i, e, pi, prev."),
    };

    private static string Number(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return s;
        throw new ImportException($"'{s}' is not a number.");
    }
}
