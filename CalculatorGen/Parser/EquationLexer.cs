// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EquationLexer.cs
//
// Tokeniser for the polynomial-in-(z,c) grammar. One-shot: feed a string
// of source, get an immutable List<Token>. The parser is a hand-rolled
// recursive descent over the token list (see EquationParser.cs).
//
// Lexical rules
//   • Whitespace separates tokens but is otherwise insignificant.
//   • Numbers are decimal, optional fractional part, optional exponent:
//     /-? \d+ (\.\d+)? ([eE] [+-]? \d+)?/  — the leading minus is parsed
//     as unary Neg, NOT consumed here.
//   • Identifiers must be exactly 'z' or 'c' (case-insensitive). Anything
//     else throws — keeps the grammar small enough for symbolic emitters
//     to reason about safely.

using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public enum TokenKind
{
    Number,
    ZVar,
    CVar,
    Plus,
    Minus,
    Star,
    Slash,
    Caret,
    LParen,
    RParen,
    Conj,
    Fold,
    Sqr,
    Sin,
    Cos,
    Tan,
    Sinh,
    Cosh,
    Tanh,
    Sqrt,
    Exp,
    Log,
    Arg,
    Atan2,
    Asin,
    Acos,
    Atan,
    Asinh,
    Acosh,
    Atanh,
    Min,
    Max,
    Mod,
    PowF,
    Floor,
    Round,
    Ceil,
    Trunc,
    Fract,
    Sign,
    Comma,
    PiConst,
    EConst,
    If,
    Then,
    Else,
    Re,
    Im,
    Abs,
    Clamp,
    Gt,
    Lt,
    Ge,
    Le,
    EqEq,
    NotEq,
    Prev,
    Iter,
    ImagUnit,
    End,
}

public readonly record struct Token(TokenKind Kind, string Lexeme, int Position, int Line, int Column)
{
    public double NumberValue => double.Parse(Lexeme, CultureInfo.InvariantCulture);

    /// <summary>Human-friendly "line L, col C" — 1-based. Falls back to
    /// "col C" when the source is a single line (typical for equation
    /// strings) to avoid noise.</summary>
    public string Where => Line > 1
        ? $"line {Line}, col {Column}"
        : $"col {Column}";
}

public static class EquationLexer
{
    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        // 1-based line/column. Bumped inline as we walk the source so
        // diagnostics can point at the offending character without a
        // second pass over the input.
        int line = 1;
        int col  = 1;

        void Bump(char c)
        {
            if (c == '\n') { line++; col = 1; }
            else            { col++; }
        }

        while (i < source.Length)
        {
            char ch = source[i];
            if (char.IsWhiteSpace(ch)) { Bump(ch); i++; continue; }

            if (char.IsDigit(ch) || ch == '.')
            {
                int start = i;
                int startLine = line, startCol = col;
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.'))
                { sb.Append(source[i]); Bump(source[i]); i++; }
                if (i < source.Length && (source[i] == 'e' || source[i] == 'E'))
                {
                    sb.Append(source[i]); Bump(source[i]); i++;
                    if (i < source.Length && (source[i] == '+' || source[i] == '-'))
                    { sb.Append(source[i]); Bump(source[i]); i++; }
                    while (i < source.Length && char.IsDigit(source[i]))
                    { sb.Append(source[i]); Bump(source[i]); i++; }
                }
                tokens.Add(new Token(TokenKind.Number, sb.ToString(), start, startLine, startCol));
                continue;
            }

            if (char.IsLetter(ch))
            {
                int start = i;
                int startLine = line, startCol = col;
                var sb = new StringBuilder();
                while (i < source.Length && char.IsLetterOrDigit(source[i]))
                { sb.Append(source[i]); Bump(source[i]); i++; }
                string name = sb.ToString();
                if (name.Equals("z", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.ZVar, name, start, startLine, startCol));
                else if (name.Equals("c", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.CVar, name, start, startLine, startCol));
                else if (name.Equals("conj", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Conj, name, start, startLine, startCol));
                else if (name.Equals("fold", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Fold, name, start, startLine, startCol));
                else if (name.Equals("sqr", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Sqr, name, start, startLine, startCol));
                else if (name.Equals("sin", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Sin, name, start, startLine, startCol));
                else if (name.Equals("cos", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Cos, name, start, startLine, startCol));
                else if (name.Equals("tan", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Tan, name, start, startLine, startCol));
                else if (name.Equals("sinh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Sinh, name, start, startLine, startCol));
                else if (name.Equals("cosh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Cosh, name, start, startLine, startCol));
                else if (name.Equals("tanh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Tanh, name, start, startLine, startCol));
                else if (name.Equals("sqrt", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Sqrt, name, start, startLine, startCol));
                else if (name.Equals("exp", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Exp, name, start, startLine, startCol));
                else if (name.Equals("log", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Log, name, start, startLine, startCol));
                else if (name.Equals("arg", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Arg, name, start, startLine, startCol));
                else if (name.Equals("atan2", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Atan2, name, start, startLine, startCol));
                else if (name.Equals("asin", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Asin, name, start, startLine, startCol));
                else if (name.Equals("acos", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Acos, name, start, startLine, startCol));
                else if (name.Equals("atan", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Atan, name, start, startLine, startCol));
                else if (name.Equals("asinh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Asinh, name, start, startLine, startCol));
                else if (name.Equals("acosh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Acosh, name, start, startLine, startCol));
                else if (name.Equals("atanh", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Atanh, name, start, startLine, startCol));
                else if (name.Equals("min", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Min, name, start, startLine, startCol));
                else if (name.Equals("max", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Max, name, start, startLine, startCol));
                else if (name.Equals("mod", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Mod, name, start, startLine, startCol));
                else if (name.Equals("pow", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.PowF, name, start, startLine, startCol));
                else if (name.Equals("floor", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Floor, name, start, startLine, startCol));
                else if (name.Equals("round", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Round, name, start, startLine, startCol));
                else if (name.Equals("ceil", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Ceil, name, start, startLine, startCol));
                else if (name.Equals("trunc", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Trunc, name, start, startLine, startCol));
                else if (name.Equals("fract", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Fract, name, start, startLine, startCol));
                else if (name.Equals("sign", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Sign, name, start, startLine, startCol));
                else if (name.Equals("pi", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.PiConst, name, start, startLine, startCol));
                else if (name.Equals("e", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.EConst, name, start, startLine, startCol));
                else if (name.Equals("if", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.If, name, start, startLine, startCol));
                else if (name.Equals("then", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Then, name, start, startLine, startCol));
                else if (name.Equals("else", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Else, name, start, startLine, startCol));
                else if (name.Equals("re", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Re, name, start, startLine, startCol));
                else if (name.Equals("im", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Im, name, start, startLine, startCol));
                else if (name.Equals("abs", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Abs, name, start, startLine, startCol));
                else if (name.Equals("clamp", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Clamp, name, start, startLine, startCol));
                else if (name.Equals("prev", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Prev, name, start, startLine, startCol));
                else if (name.Equals("iter", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("n",    StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Iter, name, start, startLine, startCol));
                else if (name.Equals("i", StringComparison.OrdinalIgnoreCase))
                    // Single-char identifier 'i' — imaginary unit literal.
                    // 'if' and 'iter' match by full name above so they win
                    // before this branch ever runs. Bare 'i' yields (0, 1).
                    tokens.Add(new Token(TokenKind.ImagUnit, name, start, startLine, startCol));
                else
                {
                    // Suggest the closest valid keyword via Levenshtein-≤2.
                    string[] keywords = { "z", "c", "conj", "fold", "sqr", "sin", "cos", "tan",
                                          "sinh", "cosh", "tanh", "sqrt", "exp", "log", "arg", "atan2",
                                          "asin", "acos", "atan", "asinh", "acosh", "atanh",
                                          "min", "max", "mod", "pow", "clamp", "pi", "e", "i",
                                          "floor", "round", "ceil", "trunc", "fract", "sign",
                                          "if", "then", "else", "re", "im", "abs", "prev", "iter", "n" };
                    string? best = null;
                    int bestD = int.MaxValue;
                    foreach (var kw in keywords)
                    {
                        int d = Levenshtein(name.ToLowerInvariant(), kw);
                        if (d < bestD) { bestD = d; best = kw; }
                    }
                    string suggestion = bestD <= 2 && best != null
                        ? $" Did you mean '{best}'?" : "";
                    string where = startLine > 1
                        ? $"line {startLine}, col {startCol}"
                        : $"col {startCol}";
                    throw new FormatException(
                        $"Unknown identifier '{name}' at {where}.{suggestion} " +
                        "Allowed: z, c, conj, fold, sqr, sin, cos, tan, sinh, cosh, tanh, sqrt, " +
                        "exp, log, arg, atan2, asin, acos, atan, asinh, acosh, atanh, " +
                        "min, max, mod, pow, clamp, floor, round, ceil, trunc, fract, sign, pi, e, i, " +
                        "if, then, else, re, im, abs, prev, iter (or n).");
                }
                continue;
            }

            int chLine = line, chCol = col;
            switch (ch)
            {
                case '+': tokens.Add(new Token(TokenKind.Plus,   "+", i, chLine, chCol)); Bump(ch); i++; continue;
                case '-': tokens.Add(new Token(TokenKind.Minus,  "-", i, chLine, chCol)); Bump(ch); i++; continue;
                case '*': tokens.Add(new Token(TokenKind.Star,   "*", i, chLine, chCol)); Bump(ch); i++; continue;
                case '/': tokens.Add(new Token(TokenKind.Slash,  "/", i, chLine, chCol)); Bump(ch); i++; continue;
                case '^': tokens.Add(new Token(TokenKind.Caret,  "^", i, chLine, chCol)); Bump(ch); i++; continue;
                case '(': tokens.Add(new Token(TokenKind.LParen, "(", i, chLine, chCol)); Bump(ch); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")", i, chLine, chCol)); Bump(ch); i++; continue;
                case ',': tokens.Add(new Token(TokenKind.Comma,  ",", i, chLine, chCol)); Bump(ch); i++; continue;
                case '>':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new Token(TokenKind.Ge, ">=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new Token(TokenKind.Gt, ">", i, chLine, chCol)); Bump(ch); i++; continue;
                case '<':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new Token(TokenKind.Le, "<=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new Token(TokenKind.Lt, "<", i, chLine, chCol)); Bump(ch); i++; continue;
                case '=':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new Token(TokenKind.EqEq, "==", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    {
                        string whereEq = chLine > 1 ? $"line {chLine}, col {chCol}" : $"col {chCol}";
                        throw new FormatException($"Unexpected '=' at {whereEq}. Did you mean '=='?");
                    }
                case '!':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new Token(TokenKind.NotEq, "!=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    {
                        string whereBang = chLine > 1 ? $"line {chLine}, col {chCol}" : $"col {chCol}";
                        throw new FormatException($"Unexpected '!' at {whereBang}. Did you mean '!='?");
                    }
                default:
                    string whereCh = chLine > 1
                        ? $"line {chLine}, col {chCol}"
                        : $"col {chCol}";
                    throw new FormatException($"Unexpected character '{ch}' at {whereCh}.");
            }
        }
        tokens.Add(new Token(TokenKind.End, string.Empty, source.Length, line, col));
        return tokens;
    }

    // Tiny iterative Levenshtein for keyword suggestions. Bounded
    // length, so no allocation pressure worth caring about.
    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
