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
    Exp,
    Log,
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
                else if (name.Equals("exp", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Exp, name, start, startLine, startCol));
                else if (name.Equals("log", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Log, name, start, startLine, startCol));
                else
                {
                    // Suggest the closest valid keyword via Levenshtein-≤2.
                    string[] keywords = { "z", "c", "conj", "fold", "sqr", "sin", "cos", "exp", "log" };
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
                        "Allowed: z, c, conj, fold, sqr, sin, cos, exp, log.");
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
