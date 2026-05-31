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
    End,
}

public readonly record struct Token(TokenKind Kind, string Lexeme, int Position)
{
    public double NumberValue => double.Parse(Lexeme, CultureInfo.InvariantCulture);
}

public static class EquationLexer
{
    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char ch = source[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }

            if (char.IsDigit(ch) || ch == '.')
            {
                int start = i;
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.'))
                    sb.Append(source[i++]);
                if (i < source.Length && (source[i] == 'e' || source[i] == 'E'))
                {
                    sb.Append(source[i++]);
                    if (i < source.Length && (source[i] == '+' || source[i] == '-'))
                        sb.Append(source[i++]);
                    while (i < source.Length && char.IsDigit(source[i]))
                        sb.Append(source[i++]);
                }
                tokens.Add(new Token(TokenKind.Number, sb.ToString(), start));
                continue;
            }

            if (char.IsLetter(ch))
            {
                int start = i;
                var sb = new StringBuilder();
                while (i < source.Length && char.IsLetterOrDigit(source[i]))
                    sb.Append(source[i++]);
                string name = sb.ToString();
                if (name.Equals("z", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.ZVar, name, start));
                else if (name.Equals("c", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.CVar, name, start));
                else
                    throw new FormatException($"Unknown identifier '{name}' at {start}. Only 'z' and 'c' are allowed in Phase A.");
                continue;
            }

            switch (ch)
            {
                case '+': tokens.Add(new Token(TokenKind.Plus,   "+", i)); i++; continue;
                case '-': tokens.Add(new Token(TokenKind.Minus,  "-", i)); i++; continue;
                case '*': tokens.Add(new Token(TokenKind.Star,   "*", i)); i++; continue;
                case '/': tokens.Add(new Token(TokenKind.Slash,  "/", i)); i++; continue;
                case '^': tokens.Add(new Token(TokenKind.Caret,  "^", i)); i++; continue;
                case '(': tokens.Add(new Token(TokenKind.LParen, "(", i)); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")", i)); i++; continue;
                default:
                    throw new FormatException($"Unexpected character '{ch}' at {i}.");
            }
        }
        tokens.Add(new Token(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }
}
