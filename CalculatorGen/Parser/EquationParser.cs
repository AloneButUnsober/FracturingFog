// EquationParser.cs
//
// Recursive-descent parser for polynomial-in-(z,c) expressions. Output
// is an AstNode tree consumed by the emitters in ../Emitters.
//
// Grammar (left-associative, conventional precedence):
//   expr   := term  (('+'|'-') term)*
//   term   := factor (('*'|'/') factor)*    // '/' is currently rejected
//                                            //   — kept in grammar for forward
//                                            //   compatibility with rational maps
//   factor := unary ('^' Int)?
//   unary  := '-' unary | atom
//   atom   := Number | 'z' | 'c' | '(' expr ')'
//
// Important deviation from a generic expression parser
//   • '^' binds to the immediately preceding factor, NOT a full sub-expression,
//     because Phase A only supports integer powers of z or c (the symbolic
//     differentiator needs that invariant to emit closed-form derivatives).

namespace FracturingFog.CalculatorGen.Parser;

public sealed class EquationParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public EquationParser(List<Token> tokens) => _tokens = tokens;

    public static AstNode Parse(string source)
    {
        var tokens = EquationLexer.Tokenize(source);
        var parser = new EquationParser(tokens);
        var node = parser.ParseExpr();
        parser.Expect(TokenKind.End);
        return node;
    }

    private Token Peek() => _tokens[_pos];
    private Token Advance() => _tokens[_pos++];

    private bool Match(params TokenKind[] kinds)
    {
        foreach (var k in kinds)
            if (_tokens[_pos].Kind == k) return true;
        return false;
    }

    private Token Expect(TokenKind kind)
    {
        var t = _tokens[_pos];
        if (t.Kind != kind)
            throw new FormatException(
                $"Expected {Describe(kind)} at {t.Where}, got {Describe(t.Kind)} ('{t.Lexeme}').");
        _pos++;
        return t;
    }

    // Friendly names for diagnostics — "'+' or '-'" reads better than
    // "Plus" to non-implementers.
    private static string Describe(TokenKind k) => k switch
    {
        TokenKind.Number => "number",
        TokenKind.ZVar   => "'z'",
        TokenKind.CVar   => "'c'",
        TokenKind.Plus   => "'+'",
        TokenKind.Minus  => "'-'",
        TokenKind.Star   => "'*'",
        TokenKind.Slash  => "'/'",
        TokenKind.Caret  => "'^'",
        TokenKind.LParen => "'('",
        TokenKind.RParen => "')'",
        TokenKind.Conj   => "conj(...)",
        TokenKind.Fold   => "fold(...)",
        TokenKind.Sqr    => "sqr(...)",
        TokenKind.Sin    => "sin(...)",
        TokenKind.Cos    => "cos(...)",
        TokenKind.Exp    => "exp(...)",
        TokenKind.Log    => "log(...)",
        TokenKind.End    => "end of input",
        _                => k.ToString(),
    };

    private AstNode ParseExpr()
    {
        var left = ParseTerm();
        while (Match(TokenKind.Plus, TokenKind.Minus))
        {
            var op = Advance();
            var right = ParseTerm();
            left = op.Kind == TokenKind.Plus
                ? new Add(left, right)
                : new Sub(left, right);
        }
        return left;
    }

    private AstNode ParseTerm()
    {
        var left = ParseFactor();
        while (Match(TokenKind.Star, TokenKind.Slash))
        {
            var op = Advance();
            var right = ParseFactor();
            left = op.Kind == TokenKind.Slash
                ? new Div(left, right)
                : new Mul(left, right);
        }
        return left;
    }

    private AstNode ParseFactor()
    {
        var node = ParseUnary();
        if (Match(TokenKind.Caret))
        {
            var caret = Advance();
            var exp = Expect(TokenKind.Number);
            if (!int.TryParse(exp.Lexeme, out int n) || n < 0 || n > 64)
                throw new FormatException(
                    $"Exponent at {exp.Where} must be a non-negative integer ≤ 64; got '{exp.Lexeme}'.");
            return new Pow(node, n);
        }
        return node;
    }

    private AstNode ParseUnary()
    {
        if (Match(TokenKind.Minus))
        {
            Advance();
            return new Neg(ParseUnary());
        }
        return ParseAtom();
    }

    private AstNode ParseAtom()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new RealConst(t.NumberValue);
            case TokenKind.ZVar:
                Advance();
                return new ZRef();
            case TokenKind.CVar:
                Advance();
                return new CRef();
            case TokenKind.LParen:
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.RParen);
                return inner;
            case TokenKind.Conj:
                Advance();
                Expect(TokenKind.LParen);
                var conjArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Conj(conjArg);
            case TokenKind.Fold:
                Advance();
                Expect(TokenKind.LParen);
                var foldArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Folded(foldArg);
            case TokenKind.Sqr:
                // sqr(x) ≡ x*x. Desugar at parse time — keeps every
                // downstream stage (differentiator, expander, emitters,
                // SA detector) unchanged. The squared expression
                // pattern-matches AstSaDetector's z*z chain so sqr(z)+c
                // still triggers SA at degree 2.
                Advance();
                Expect(TokenKind.LParen);
                var sqrArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Mul(sqrArg, sqrArg);
            case TokenKind.Sin:
                Advance();
                Expect(TokenKind.LParen);
                var sinArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Sin(sinArg);
            case TokenKind.Cos:
                Advance();
                Expect(TokenKind.LParen);
                var cosArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Cos(cosArg);
            case TokenKind.Exp:
                Advance();
                Expect(TokenKind.LParen);
                var expArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Exp(expArg);
            case TokenKind.Log:
                Advance();
                Expect(TokenKind.LParen);
                var logArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Log(logArg);
            default:
                throw new FormatException(
                    $"Unexpected {Describe(t.Kind)} ('{t.Lexeme}') at {t.Where}.");
        }
    }
}
