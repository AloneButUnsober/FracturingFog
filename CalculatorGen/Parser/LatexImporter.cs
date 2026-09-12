// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LatexImporter.cs
//
// #765 — import a *constrained subset* of LaTeX math into the User Equation
// dialog by converting it to the CalcGen DSL. The reverse of AstLatexPrinter
// (#754), and the best-effort sibling of MathmlImporter (#764).
//
// Strategy (per the #757 spike, Docs/Technical/UE-Math-Import-Feasibility.md):
// tokenize the LaTeX, recursive-descent parse into a generously parenthesised
// DSL string, then round-trip through EquationParser.Parse so the output is
// valid or a clear error is returned. LaTeX is irregular and unbounded, so this
// covers the AstLatexPrinter output plus common hand-written variants and
// rejects the rest (unknown commands/identifiers, sums, integrals, matrices
// other than a cases block) with a message naming the construct.
//
// Handled: \frac \sqrt \overline \left \right \cdot \times \div \operatorname
// \sin \cos \tan \ln \log \exp \arg \arcsin.. \sinh.. \min \max \pi \lfloor
// \lceil, ^{} _{} (z_{n-1}->prev), | | (abs), e^{}->exp, implicit multiplication
// (2z, z(z+c)), \begin{cases}..\end{cases} -> if/then/else. Spacing macros and
// \text/\displaystyle are stripped.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class LatexImporter
{
    private sealed class ImportException(string message) : Exception(message);

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
        ["abs"] = ("abs", 1), ["re"] = ("re", 1), ["im"] = ("im", 1), ["Re"] = ("re", 1), ["Im"] = ("im", 1),
        ["conj"] = ("conj", 1), ["fold"] = ("fold", 1),
        ["floor"] = ("floor", 1), ["round"] = ("round", 1), ["ceil"] = ("ceil", 1),
        ["trunc"] = ("trunc", 1), ["fract"] = ("fract", 1), ["sign"] = ("sign", 1),
        ["min"] = ("min", 2), ["max"] = ("max", 2), ["mod"] = ("mod", 2),
        ["atan2"] = ("atan2", 2), ["pow"] = ("pow", 2), ["clamp"] = ("clamp", 3),
    };

    public static MathmlImportResult Import(string? latex)
    {
        string source = latex ?? string.Empty;   // non-null for the parser below
        if (string.IsNullOrWhiteSpace(source))
            return new MathmlImportResult(false, string.Empty, "Clipboard has no text to import.");

        string dsl;
        try
        {
            var toks = Tokenize(source);
            int pos = 0;
            dsl = ParseExpr(toks, ref pos);
            if (pos != toks.Count)
                throw new ImportException($"unexpected '{toks[pos].Text}' after a complete expression.");
            if (string.IsNullOrWhiteSpace(dsl))
                throw new ImportException("no convertible math content found.");
        }
        catch (ImportException ex)
        {
            return new MathmlImportResult(false, string.Empty, ex.Message);
        }

        try { EquationParser.Parse(dsl); }
        catch (Exception ex)
        {
            return new MathmlImportResult(false, dsl, $"produced DSL the parser rejected: {ex.Message}");
        }
        return new MathmlImportResult(true, dsl, null);
    }

    // ── tokenizer ─────────────────────────────────────────────────────────────
    private enum TT { Cmd, LBrace, RBrace, LParen, RParen, Caret, Underscore, Bar,
                      FloorL, FloorR, CeilL, CeilR, Comma, Op, Cmp, Amp, RowSep, Ident, Number }
    private readonly record struct Tok(TT Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char ch = s[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }

            if (ch == '\\')
            {
                i++;
                if (i >= n) throw new ImportException("dangling '\\'.");
                if (s[i] == '\\') { i++; toks.Add(new(TT.RowSep, "\\\\")); continue; }
                if (!char.IsLetter(s[i]))
                {
                    // \, \; \! \  (spacing) -> skip; \{ \} \| -> literal
                    char c = s[i++];
                    if (c is ',' or ';' or '!' or ' ') continue;
                    if (c == '{') { toks.Add(new(TT.LBrace, "{")); continue; }
                    if (c == '}') { toks.Add(new(TT.RBrace, "}")); continue; }
                    if (c == '|') { toks.Add(new(TT.Bar, "|")); continue; }
                    continue; // unknown escaped punctuation -> ignore
                }
                int st = i;
                while (i < n && char.IsLetter(s[i])) i++;
                EmitCommand(toks, s.Substring(st, i - st));
                continue;
            }
            switch (ch)
            {
                case '{': toks.Add(new(TT.LBrace, "{")); i++; break;
                case '}': toks.Add(new(TT.RBrace, "}")); i++; break;
                case '(': toks.Add(new(TT.LParen, "(")); i++; break;
                case ')': toks.Add(new(TT.RParen, ")")); i++; break;
                case '[': toks.Add(new(TT.LParen, "(")); i++; break;
                case ']': toks.Add(new(TT.RParen, ")")); i++; break;
                case '^': toks.Add(new(TT.Caret, "^")); i++; break;
                case '_': toks.Add(new(TT.Underscore, "_")); i++; break;
                case '|': toks.Add(new(TT.Bar, "|")); i++; break;
                case ',': toks.Add(new(TT.Comma, ",")); i++; break;
                case '&': toks.Add(new(TT.Amp, "&")); i++; break;
                case '+': case '-': toks.Add(new(TT.Op, ch.ToString())); i++; break;
                case '*': toks.Add(new(TT.Op, "*")); i++; break;
                case '/': toks.Add(new(TT.Op, "/")); i++; break;
                case '=': toks.Add(new(TT.Cmp, "==")); i++; break;
                case '<': toks.Add(new(TT.Cmp, "<")); i++; break;
                case '>': toks.Add(new(TT.Cmp, ">")); i++; break;
                default:
                    if (char.IsDigit(ch) || ch == '.')
                    {
                        int st = i;
                        while (i < n && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                        toks.Add(new(TT.Number, s.Substring(st, i - st)));
                    }
                    else if (char.IsLetter(ch))
                    {
                        toks.Add(new(TT.Ident, ch.ToString())); i++;  // single-letter identifiers
                    }
                    else throw new ImportException($"unexpected character '{ch}'.");
                    break;
            }
        }
        return toks;
    }

    private static void EmitCommand(List<Tok> toks, string name)
    {
        switch (name)
        {
            case "left": case "right": return;                 // delimiters follow as their own chars
            case "displaystyle": case "textstyle": case "limits": return;
            case "cdot": case "times": case "ast": toks.Add(new(TT.Op, "*")); return;
            case "div": toks.Add(new(TT.Op, "/")); return;
            case "ge": case "geq": toks.Add(new(TT.Cmp, ">=")); return;
            case "le": case "leq": toks.Add(new(TT.Cmp, "<=")); return;
            case "ne": case "neq": toks.Add(new(TT.Cmp, "!=")); return;
            case "gt": toks.Add(new(TT.Cmp, ">")); return;
            case "lt": toks.Add(new(TT.Cmp, "<")); return;
            case "lfloor": toks.Add(new(TT.FloorL, "lfloor")); return;
            case "rfloor": toks.Add(new(TT.FloorR, "rfloor")); return;
            case "lceil": toks.Add(new(TT.CeilL, "lceil")); return;
            case "rceil": toks.Add(new(TT.CeilR, "rceil")); return;
            case "pi": toks.Add(new(TT.Ident, "pi")); return;
            default: toks.Add(new(TT.Cmd, name)); return;      // \frac \sqrt \sin \operatorname \begin ...
        }
    }

    // ── parser (emits parenthesised DSL) ─────────────────────────────────────
    private static string ParseExpr(List<Tok> toks, ref int pos)
    {
        string left = ParseTerm(toks, ref pos);
        while (pos < toks.Count && toks[pos].Kind == TT.Op && (toks[pos].Text is "+" or "-"))
        {
            string op = toks[pos].Text; pos++;
            string right = ParseTerm(toks, ref pos);
            left = $"({left} {op} {right})";
        }
        return left;
    }

    private static string ParseTerm(List<Tok> toks, ref int pos)
    {
        string left = ParseUnary(toks, ref pos);
        while (pos < toks.Count)
        {
            if (toks[pos].Kind == TT.Op && (toks[pos].Text is "*" or "/"))
            {
                string op = toks[pos].Text; pos++;
                string right = ParseUnary(toks, ref pos);
                left = $"({left}{op}{right})";
            }
            else if (StartsFactor(toks[pos]))          // implicit multiplication
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
        if (pos < toks.Count && toks[pos].Kind == TT.Op && toks[pos].Text == "-")
        {
            pos++;
            return $"(-{ParseUnary(toks, ref pos)})";
        }
        return ParsePostfix(toks, ref pos);
    }

    // factor with optional ^exponent (and e^ -> exp)
    private static string ParsePostfix(List<Tok> toks, ref int pos)
    {
        bool baseIsE = pos < toks.Count && toks[pos].Kind == TT.Ident && toks[pos].Text == "e";
        string baseDsl = ParseFactor(toks, ref pos);
        if (pos < toks.Count && toks[pos].Kind == TT.Caret)
        {
            pos++;
            var (expIsInt, expText, expDsl) = ParseExponent(toks, ref pos);
            if (baseIsE) return $"exp({expDsl})";
            if (expIsInt) return $"({baseDsl})^{expText}";
            return $"pow({baseDsl}, {expDsl})";
        }
        return baseDsl;
    }

    private static (bool isInt, string text, string dsl) ParseExponent(List<Tok> toks, ref int pos)
    {
        // ^{...} or ^<single token>
        if (pos < toks.Count && toks[pos].Kind == TT.LBrace)
        {
            pos++;
            // integer-literal fast path: a lone number
            if (pos + 1 < toks.Count && toks[pos].Kind == TT.Number && toks[pos + 1].Kind == TT.RBrace
                && IsInt(toks[pos].Text))
            {
                string t = toks[pos].Text; pos += 2; return (true, t, t);
            }
            string e = ParseExpr(toks, ref pos);
            Expect(toks, ref pos, TT.RBrace, "'}'");
            return (false, e, e);
        }
        var tok = toks[pos];
        if (tok.Kind == TT.Number && IsInt(tok.Text)) { pos++; return (true, tok.Text, tok.Text); }
        string single = ParseFactor(toks, ref pos);
        return (false, single, single);
    }

    private static string ParseFactor(List<Tok> toks, ref int pos)
    {
        if (pos >= toks.Count) throw new ImportException("expression ended unexpectedly.");
        Tok t = toks[pos];
        switch (t.Kind)
        {
            case TT.Number: pos++; return Number(t.Text);
            case TT.Ident:  pos++; return Atom(t, toks, ref pos);
            case TT.LBrace: { pos++; string e = ParseExpr(toks, ref pos); Expect(toks, ref pos, TT.RBrace, "'}'"); return $"({e})"; }
            case TT.LParen: { pos++; string e = ParseExpr(toks, ref pos); Expect(toks, ref pos, TT.RParen, "')'"); return $"({e})"; }
            case TT.Bar:
            {
                // Bars aren't self-describing (open vs close), so slice to the
                // matching top-level '|' and parse the inner tokens separately —
                // otherwise the closing bar reads as an implicit-mult factor.
                pos++;
                int j = pos, d = 0;
                for (; j < toks.Count; j++)
                {
                    var k = toks[j].Kind;
                    if (k is TT.LParen or TT.LBrace) d++;
                    else if (k is TT.RParen or TT.RBrace) d--;
                    else if (k == TT.Bar && d == 0) break;
                }
                if (j >= toks.Count) throw new ImportException("unclosed '|'.");
                var inner = toks.GetRange(pos, j - pos);
                pos = j + 1;
                int ip = 0;
                string e = ParseExpr(inner, ref ip);
                return $"abs({e})";
            }
            case TT.FloorL: { pos++; string e = ParseExpr(toks, ref pos); Expect(toks, ref pos, TT.FloorR, "\\rfloor"); return $"floor({e})"; }
            case TT.CeilL:  { pos++; string e = ParseExpr(toks, ref pos); Expect(toks, ref pos, TT.CeilR, "\\rceil"); return $"ceil({e})"; }
            case TT.Cmd:    return ParseCommand(toks, ref pos);
            default:
                throw new ImportException($"unexpected '{t.Text}'.");
        }
    }

    // Ident that may carry a subscript (z_{n-1} -> prev).
    private static string Atom(Tok t, List<Tok> toks, ref int pos)
    {
        if (t.Text == "z" && pos < toks.Count && toks[pos].Kind == TT.Underscore)
        {
            pos++;
            string sub = pos < toks.Count && toks[pos].Kind == TT.LBrace
                ? ReadBraceRaw(toks, ref pos)
                : (pos < toks.Count ? Consume(toks, ref pos) : string.Empty);
            if (sub.Replace(" ", string.Empty) is "n-1" or "(n-1)") return "prev";
            throw new ImportException("only z_{n-1} (prev) subscripts are supported.");
        }
        return AtomDsl(t.Text);
    }

    private static string ParseCommand(List<Tok> toks, ref int pos)
    {
        string name = toks[pos].Text; pos++;
        switch (name)
        {
            case "frac":
            {
                string a = ReadGroup(toks, ref pos), b = ReadGroup(toks, ref pos);
                return $"(({a})/({b}))";
            }
            case "sqrt":
            {
                // optional [index]
                if (pos < toks.Count && toks[pos].Kind == TT.LParen) { /* [n] tokenised as ( ) */ }
                string a = ReadGroup(toks, ref pos);
                return $"sqrt({a})";
            }
            case "overline": return $"conj({ReadGroup(toks, ref pos)})";
            case "operatorname":
            {
                string fn = ReadGroupRawName(toks, ref pos);
                return ParseFuncCall(fn, toks, ref pos);
            }
            case "text": _ = ReadGroupRawName(toks, ref pos); return string.Empty;
            case "begin":
            {
                string env = ReadGroupRawName(toks, ref pos);
                if (env != "cases") throw new ImportException($"unsupported environment '{env}'.");
                return ParseCases(toks, ref pos);
            }
            default:
                if (Funcs.ContainsKey(name)) return ParseFuncCall(name, toks, ref pos);
                throw new ImportException($"unsupported command '\\{name}'.");
        }
    }

    // A function call: name followed by ( args ) — or a single factor for the
    // 1-arg trig/log forms written without parens (\sin x).
    private static string ParseFuncCall(string name, List<Tok> toks, ref int pos)
    {
        if (!Funcs.TryGetValue(name, out var f))
            throw new ImportException($"unknown function '{name}'.");
        if (pos < toks.Count && toks[pos].Kind == TT.LParen)
        {
            pos++;
            var args = new List<string> { ParseExpr(toks, ref pos) };
            while (pos < toks.Count && toks[pos].Kind == TT.Comma) { pos++; args.Add(ParseExpr(toks, ref pos)); }
            Expect(toks, ref pos, TT.RParen, $"')' closing {name}(...)");
            if (args.Count != f.Arity) throw new ImportException($"{name} expects {f.Arity} argument(s), got {args.Count}.");
            return $"{f.Dsl}({string.Join(", ", args)})";
        }
        if (f.Arity == 1) return $"{f.Dsl}({ParseFactor(toks, ref pos)})";
        throw new ImportException($"{name} needs parenthesised arguments.");
    }

    private static string ParseCases(List<Tok> toks, ref int pos)
    {
        // then & \text{if } cond \\ else & \text{otherwise} \end{cases}
        var cells = new List<List<Tok>>();
        var cur = new List<Tok>();
        int depth = 0;
        while (pos < toks.Count)
        {
            Tok t = toks[pos];
            if (t.Kind == TT.Cmd && t.Text == "end") { pos++; ReadGroupRawName(toks, ref pos); break; }
            if (depth == 0 && (t.Kind == TT.Amp || t.Kind == TT.RowSep)) { cells.Add(cur); cur = new(); pos++; continue; }
            if (t.Kind == TT.LBrace) depth++;
            if (t.Kind == TT.RBrace) depth--;
            cur.Add(t); pos++;
        }
        cells.Add(cur);
        if (cells.Count != 4) throw new ImportException("only two-branch \\begin{cases} (then/else) is supported.");

        string thenE = ParseCellExpr(cells[0]);
        string cond = ParseCondCell(cells[1]);
        string elseE = ParseCellExpr(cells[2]);
        return $"if {cond} then {thenE} else {elseE}";
    }

    private static string ParseCellExpr(List<Tok> cell)
    {
        int p = 0;
        string e = ParseExpr(cell, ref p);
        return e;
    }

    // condition cell: [\text{if }] term <cmp> term
    private static string ParseCondCell(List<Tok> cell)
    {
        // strip a leading \text{...}
        var toks = StripLeadingText(cell);
        int cmp = toks.FindIndex(x => x.Kind == TT.Cmp);
        if (cmp <= 0 || cmp >= toks.Count - 1) throw new ImportException("condition must be 'term <comparison> term'.");
        string op = toks[cmp].Text;
        string left = CondTerm(toks.GetRange(0, cmp));
        string right = CondTerm(toks.GetRange(cmp + 1, toks.Count - cmp - 1));
        return $"{left} {op} {right}";
    }

    private static List<Tok> StripLeadingText(List<Tok> cell)
    {
        // \text was tokenised as Cmd(text) then {..}; ParseCommand strips it, but
        // here we operate on raw tokens — drop a leading Cmd(text) + brace group.
        int i = 0;
        if (i < cell.Count && cell[i].Kind == TT.Cmd && cell[i].Text == "text")
        {
            i++;
            if (i < cell.Count && cell[i].Kind == TT.LBrace)
            {
                int depth = 0;
                do { if (cell[i].Kind == TT.LBrace) depth++; else if (cell[i].Kind == TT.RBrace) depth--; i++; }
                while (i < cell.Count && depth > 0);
            }
        }
        return cell.GetRange(i, cell.Count - i);
    }

    // |x|^2 in a condition -> abs(x) (DSL condition-abs is squared magnitude),
    // matching AstLatexPrinter's CondAbs2 = {\left|x\right|}^{2}. Anything else
    // (re/im/arg/const/expr) parses normally.
    private static string CondTerm(List<Tok> c)
    {
        // Match the exporter's CondAbs2:  {\left|X\right|}^{2}  or  |X|^2  ->  abs(X).
        bool brace = c.Count > 0 && c[0].Kind == TT.LBrace;
        int barOpen = brace ? 1 : 0;
        if (barOpen < c.Count && c[barOpen].Kind == TT.Bar)
        {
            int j = barOpen + 1, d = 0;
            for (; j < c.Count; j++)
            {
                var k = c[j].Kind;
                if (k is TT.LParen or TT.LBrace) d++;
                else if (k is TT.RParen or TT.RBrace) d--;
                else if (k == TT.Bar && d == 0) break;
            }
            if (j < c.Count)                       // found the closing bar
            {
                int r = j + 1;
                bool ok = true;
                if (brace) { if (r < c.Count && c[r].Kind == TT.RBrace) r++; else ok = false; }
                bool squared = false;
                if (ok && r < c.Count && c[r].Kind == TT.Caret)
                {
                    r++;
                    if (r < c.Count && c[r].Kind == TT.Number && c[r].Text == "2") { squared = true; r++; }
                    else if (r + 2 < c.Count && c[r].Kind == TT.LBrace && c[r + 1].Kind == TT.Number
                             && c[r + 1].Text == "2" && c[r + 2].Kind == TT.RBrace) { squared = true; r += 3; }
                }
                if (squared && r == c.Count)
                {
                    var inner = c.GetRange(barOpen + 1, j - barOpen - 1);
                    int ip = 0;
                    return $"abs({ParseExpr(inner, ref ip)})";
                }
            }
        }
        int p = 0;
        return ParseExpr(c, ref p);
    }

    // ── small helpers ─────────────────────────────────────────────────────────
    private static bool StartsFactor(Tok t) =>
        t.Kind is TT.Number or TT.Ident or TT.LParen or TT.LBrace or TT.Bar or TT.FloorL or TT.CeilL or TT.Cmd;

    private static string ReadGroup(List<Tok> toks, ref int pos)
    {
        if (pos >= toks.Count) throw new ImportException("expected '{'.");
        if (toks[pos].Kind == TT.LBrace)
        {
            pos++;
            string e = ParseExpr(toks, ref pos);
            Expect(toks, ref pos, TT.RBrace, "'}'");
            return e;
        }
        return ParseFactor(toks, ref pos);   // tolerate \sqrt x
    }

    // Read a {name} group as a raw identifier string (for \operatorname{}, \begin{}).
    private static string ReadGroupRawName(List<Tok> toks, ref int pos)
    {
        Expect(toks, ref pos, TT.LBrace, "'{'");
        var sb = new StringBuilder();
        while (pos < toks.Count && toks[pos].Kind != TT.RBrace) { sb.Append(toks[pos].Text); pos++; }
        Expect(toks, ref pos, TT.RBrace, "'}'");
        return sb.ToString();
    }

    private static string ReadBraceRaw(List<Tok> toks, ref int pos)
    {
        Expect(toks, ref pos, TT.LBrace, "'{'");
        var sb = new StringBuilder();
        while (pos < toks.Count && toks[pos].Kind != TT.RBrace) { sb.Append(toks[pos].Text); pos++; }
        Expect(toks, ref pos, TT.RBrace, "'}'");
        return sb.ToString();
    }

    private static string Consume(List<Tok> toks, ref int pos) { var t = toks[pos]; pos++; return t.Text; }

    private static void Expect(List<Tok> toks, ref int pos, TT kind, string what)
    {
        if (pos >= toks.Count || toks[pos].Kind != kind) throw new ImportException($"expected {what}.");
        pos++;
    }

    private static bool IsInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static string AtomDsl(string name) => name switch
    {
        "z" => "z", "c" => "c", "n" => "n", "i" => "i",
        "e" => "e", "pi" => "pi", "prev" => "prev",
        _ => throw new ImportException($"unknown identifier '{name}' — the DSL only knows z, c, n, i, e, pi, prev."),
    };

    private static string Number(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return s;
        throw new ImportException($"'{s}' is not a number.");
    }
}
