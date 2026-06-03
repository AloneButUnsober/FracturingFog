// ColorGenLexer.cs
//
// Tokeniser for the ColorGen DSL. Hand-rolled because the grammar is small
// and we want crisp error messages with line/column. One-shot: feed a
// string, get back a List<Token> ending in End.
//
// Lexical rules
//   • Whitespace separates tokens.
//   • // line comments and /* block comments */ are skipped.
//   • Numbers: optional int part, optional fractional part, optional
//     [eE][+-]?digits exponent. Leading minus parses as unary later.
//   • Identifiers: [A-Za-z_][A-Za-z0-9_]*. Reserved words ('let', 'return',
//     'true', 'false') get their own token kinds; everything else is Ident.
//   • Operators: + - * / % ^ ( ) , ; . = == != < <= > >= && || ! ? :

using System.Globalization;
using System.Text;

namespace FracturingFog.ColorGen.Parser;

public enum CgTokenKind
{
    Number,
    Ident,
    Let,
    Return,
    True,
    False,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Caret,
    LParen,
    RParen,
    Comma,
    Semi,
    Dot,
    Assign,
    EqEq,
    NotEq,
    Lt,
    Le,
    Gt,
    Ge,
    AndAnd,
    OrOr,
    Bang,
    Question,
    Colon,
    End,
}

public readonly record struct CgToken(CgTokenKind Kind, string Lexeme, int Position, int Line, int Column)
{
    public double NumberValue => double.Parse(Lexeme, CultureInfo.InvariantCulture);
    public string Where => Line > 1 ? $"line {Line}, col {Column}" : $"col {Column}";
}

public static class ColorGenLexer
{
    public static List<CgToken> Tokenize(string source)
    {
        var tokens = new List<CgToken>();
        int i = 0, line = 1, col = 1;

        void Bump(char c)
        {
            if (c == '\n') { line++; col = 1; }
            else col++;
        }

        while (i < source.Length)
        {
            char ch = source[i];

            // Whitespace.
            if (char.IsWhiteSpace(ch)) { Bump(ch); i++; continue; }

            // // line comment.
            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') { Bump(source[i]); i++; }
                continue;
            }
            // /* block comment */
            if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                Bump(ch); i++; Bump(source[i]); i++;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                { Bump(source[i]); i++; }
                if (i < source.Length) { Bump(source[i]); i++; Bump(source[i]); i++; }
                continue;
            }

            // Number.
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                int start = i, startLine = line, startCol = col;
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
                tokens.Add(new CgToken(CgTokenKind.Number, sb.ToString(), start, startLine, startCol));
                continue;
            }

            // Identifier or keyword.
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i, startLine = line, startCol = col;
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                { sb.Append(source[i]); Bump(source[i]); i++; }
                string name = sb.ToString();
                CgTokenKind kind = name switch
                {
                    "let" => CgTokenKind.Let,
                    "return" => CgTokenKind.Return,
                    "true" => CgTokenKind.True,
                    "false" => CgTokenKind.False,
                    _ => CgTokenKind.Ident,
                };
                tokens.Add(new CgToken(kind, name, start, startLine, startCol));
                continue;
            }

            int chLine = line, chCol = col;
            switch (ch)
            {
                case '+': tokens.Add(new CgToken(CgTokenKind.Plus,   "+", i, chLine, chCol)); Bump(ch); i++; continue;
                case '-': tokens.Add(new CgToken(CgTokenKind.Minus,  "-", i, chLine, chCol)); Bump(ch); i++; continue;
                case '*': tokens.Add(new CgToken(CgTokenKind.Star,   "*", i, chLine, chCol)); Bump(ch); i++; continue;
                case '/': tokens.Add(new CgToken(CgTokenKind.Slash,  "/", i, chLine, chCol)); Bump(ch); i++; continue;
                case '%': tokens.Add(new CgToken(CgTokenKind.Percent,"%", i, chLine, chCol)); Bump(ch); i++; continue;
                case '^': tokens.Add(new CgToken(CgTokenKind.Caret,  "^", i, chLine, chCol)); Bump(ch); i++; continue;
                case '(': tokens.Add(new CgToken(CgTokenKind.LParen, "(", i, chLine, chCol)); Bump(ch); i++; continue;
                case ')': tokens.Add(new CgToken(CgTokenKind.RParen, ")", i, chLine, chCol)); Bump(ch); i++; continue;
                case ',': tokens.Add(new CgToken(CgTokenKind.Comma,  ",", i, chLine, chCol)); Bump(ch); i++; continue;
                case ';': tokens.Add(new CgToken(CgTokenKind.Semi,   ";", i, chLine, chCol)); Bump(ch); i++; continue;
                case '.': tokens.Add(new CgToken(CgTokenKind.Dot,    ".", i, chLine, chCol)); Bump(ch); i++; continue;
                case '?': tokens.Add(new CgToken(CgTokenKind.Question,"?",i, chLine, chCol)); Bump(ch); i++; continue;
                case ':': tokens.Add(new CgToken(CgTokenKind.Colon,  ":", i, chLine, chCol)); Bump(ch); i++; continue;
                case '=':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new CgToken(CgTokenKind.EqEq, "==", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new CgToken(CgTokenKind.Assign, "=", i, chLine, chCol)); Bump(ch); i++; continue;
                case '!':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new CgToken(CgTokenKind.NotEq, "!=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new CgToken(CgTokenKind.Bang, "!", i, chLine, chCol)); Bump(ch); i++; continue;
                case '<':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new CgToken(CgTokenKind.Le, "<=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new CgToken(CgTokenKind.Lt, "<", i, chLine, chCol)); Bump(ch); i++; continue;
                case '>':
                    if (i + 1 < source.Length && source[i + 1] == '=')
                    { tokens.Add(new CgToken(CgTokenKind.Ge, ">=", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    tokens.Add(new CgToken(CgTokenKind.Gt, ">", i, chLine, chCol)); Bump(ch); i++; continue;
                case '&':
                    if (i + 1 < source.Length && source[i + 1] == '&')
                    { tokens.Add(new CgToken(CgTokenKind.AndAnd, "&&", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    goto default;
                case '|':
                    if (i + 1 < source.Length && source[i + 1] == '|')
                    { tokens.Add(new CgToken(CgTokenKind.OrOr, "||", i, chLine, chCol)); Bump(ch); Bump(source[i+1]); i += 2; continue; }
                    goto default;
                default:
                    string where = chLine > 1 ? $"line {chLine}, col {chCol}" : $"col {chCol}";
                    throw new FormatException($"Unexpected character '{ch}' at {where}.");
            }
        }

        tokens.Add(new CgToken(CgTokenKind.End, string.Empty, source.Length, line, col));
        return tokens;
    }
}
