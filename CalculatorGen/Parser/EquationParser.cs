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
        TokenKind.If     => "'if'",
        TokenKind.Then   => "'then'",
        TokenKind.Else   => "'else'",
        TokenKind.Re     => "re(...)",
        TokenKind.Im     => "im(...)",
        TokenKind.Abs    => "abs(...)",
        TokenKind.Gt     => "'>'",
        TokenKind.Lt     => "'<'",
        TokenKind.Ge     => "'>='",
        TokenKind.Le     => "'<='",
        TokenKind.EqEq   => "'=='",
        TokenKind.NotEq  => "'!='",
        TokenKind.Prev   => "'prev'",
        TokenKind.Iter   => "'iter' (or 'n')",
        TokenKind.End    => "end of input",
        _                => k.ToString(),
    };

    private AstNode ParseExpr()
    {
        if (Match(TokenKind.If))
            return ParseIf();
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

    // If-expression. Grammar:
    //   if_expr ::= 'if' cond 'then' expr 'else' expr
    //   cond    ::= cond_term cmp_op cond_term
    //   cmp_op  ::= '>' | '<' | '>=' | '<=' | '==' | '!='
    //   cond_term ::= 're' '(' expr ')' | 'im' '(' expr ')'
    //              | 'abs' '(' expr ')' | number | '-' number
    // 'abs' is shorthand for |x|² (squared magnitude); the underlying
    // CondTerm is CondAbs2. Saves a sqrt and matches the natural
    // bailout-style threshold form users think in.
    private AstNode ParseIf()
    {
        Expect(TokenKind.If);
        var cond = ParseCond();
        Expect(TokenKind.Then);
        var thenBranch = ParseExpr();
        Expect(TokenKind.Else);
        var elseBranch = ParseExpr();
        return new If(cond, thenBranch, elseBranch);
    }

    private CondNode ParseCond()
    {
        var l = ParseCondTerm();
        var opTok = Peek();
        CmpOp op = opTok.Kind switch
        {
            TokenKind.Gt    => CmpOp.Gt,
            TokenKind.Lt    => CmpOp.Lt,
            TokenKind.Ge    => CmpOp.Ge,
            TokenKind.Le    => CmpOp.Le,
            TokenKind.EqEq  => CmpOp.Eq,
            TokenKind.NotEq => CmpOp.Ne,
            _ => throw new FormatException(
                $"Expected comparison ('>', '<', '>=', '<=', '==', '!=') at {opTok.Where}, " +
                $"got {Describe(opTok.Kind)} ('{opTok.Lexeme}')."),
        };
        Advance();
        var r = ParseCondTerm();
        return new Cmp(op, l, r);
    }

    private CondTerm ParseCondTerm()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Re:
                Advance();
                Expect(TokenKind.LParen);
                var reArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new CondRe(reArg);
            case TokenKind.Im:
                Advance();
                Expect(TokenKind.LParen);
                var imArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new CondIm(imArg);
            case TokenKind.Abs:
                Advance();
                Expect(TokenKind.LParen);
                var absArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new CondAbs2(absArg);
            case TokenKind.Number:
                Advance();
                return new CondConst(t.NumberValue);
            case TokenKind.Minus:
                // Unary minus on a literal — keep cond terms flat.
                Advance();
                var negTok = Expect(TokenKind.Number);
                return new CondConst(-negTok.NumberValue);
            default:
                throw new FormatException(
                    $"Expected condition term (re(...), im(...), abs(...), or number) at " +
                    $"{t.Where}, got {Describe(t.Kind)} ('{t.Lexeme}').");
        }
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
            case TokenKind.Prev:
                Advance();
                return new PrevRef();
            case TokenKind.Iter:
                Advance();
                return new IterRef();
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
