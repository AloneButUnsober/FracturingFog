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
        TokenKind.Tan    => "tan(...)",
        TokenKind.Sinh   => "sinh(...)",
        TokenKind.Cosh   => "cosh(...)",
        TokenKind.Tanh   => "tanh(...)",
        TokenKind.Sqrt   => "sqrt(...)",
        TokenKind.Exp    => "exp(...)",
        TokenKind.Log    => "log(...)",
        TokenKind.Arg    => "arg(...)",
        TokenKind.Atan2  => "atan2(...)",
        TokenKind.Min    => "min(...)",
        TokenKind.Max    => "max(...)",
        TokenKind.Mod    => "mod(...)",
        TokenKind.Comma  => "','",
        TokenKind.PiConst => "'pi'",
        TokenKind.EConst => "'e'",
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

    // sinh / cosh expand into a binary op-pair that uses the operand twice
    // (once positive, once negated). Helpers keep ParseAtom readable and
    // ensure the same expansion is reused from Tanh.
    private static AstNode BuildSinh(AstNode x) =>
        new Div(new Sub(new Exp(x), new Exp(new Neg(x))), new RealConst(2.0));

    private static AstNode BuildCosh(AstNode x) =>
        new Div(new Add(new Exp(x), new Exp(new Neg(x))), new RealConst(2.0));

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
            case TokenKind.Tan:
                // tan(x) ≡ sin(x) / cos(x). Desugaring keeps every downstream
                // stage unchanged. Pole behaviour at cos(x)==0 is handled by
                // the emitted Div like any other division.
                Advance();
                Expect(TokenKind.LParen);
                var tanArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Div(new Sin(tanArg), new Cos(tanArg));
            case TokenKind.Sinh:
                // sinh(x) ≡ (exp(x) - exp(-x)) / 2.
                Advance();
                Expect(TokenKind.LParen);
                var sinhArg = ParseExpr();
                Expect(TokenKind.RParen);
                return BuildSinh(sinhArg);
            case TokenKind.Cosh:
                // cosh(x) ≡ (exp(x) + exp(-x)) / 2.
                Advance();
                Expect(TokenKind.LParen);
                var coshArg = ParseExpr();
                Expect(TokenKind.RParen);
                return BuildCosh(coshArg);
            case TokenKind.Tanh:
                // tanh(x) ≡ sinh(x) / cosh(x).
                Advance();
                Expect(TokenKind.LParen);
                var tanhArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Div(BuildSinh(tanhArg), BuildCosh(tanhArg));
            case TokenKind.Sqrt:
                // sqrt(x) ≡ exp(0.5 * log(x)). The principal branch matches
                // System.Numerics.Complex.Sqrt (Im(log(x)) ∈ (-π, π]).
                Advance();
                Expect(TokenKind.LParen);
                var sqrtArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Exp(new Mul(new RealConst(0.5), new Log(sqrtArg)));
            case TokenKind.Arg:
                // arg(x) — non-holomorphic angle, lifted to complex as
                // (atan2(im(x), re(x)), 0). Gating handled like Conj/Folded
                // by Contains<Arg> checks in downstream visitors.
                Advance();
                Expect(TokenKind.LParen);
                var argArg = ParseExpr();
                Expect(TokenKind.RParen);
                return new Arg(argArg);
            case TokenKind.Atan2:
                // atan2(y, x) — binary form, requires the new Comma token.
                Advance();
                Expect(TokenKind.LParen);
                var atan2Y = ParseExpr();
                Expect(TokenKind.Comma);
                var atan2X = ParseExpr();
                Expect(TokenKind.RParen);
                return new Atan2(atan2Y, atan2X);
            case TokenKind.Min:
                Advance();
                Expect(TokenKind.LParen);
                var minL = ParseExpr();
                Expect(TokenKind.Comma);
                var minR = ParseExpr();
                Expect(TokenKind.RParen);
                return new Min(minL, minR);
            case TokenKind.Max:
                Advance();
                Expect(TokenKind.LParen);
                var maxL = ParseExpr();
                Expect(TokenKind.Comma);
                var maxR = ParseExpr();
                Expect(TokenKind.RParen);
                return new Max(maxL, maxR);
            case TokenKind.Mod:
                Advance();
                Expect(TokenKind.LParen);
                var modL = ParseExpr();
                Expect(TokenKind.Comma);
                var modR = ParseExpr();
                Expect(TokenKind.RParen);
                return new Mod(modL, modR);
            case TokenKind.PiConst:
                Advance();
                return new RealConst(Math.PI);
            case TokenKind.EConst:
                Advance();
                return new RealConst(Math.E);
            default:
                throw new FormatException(
                    $"Unexpected {Describe(t.Kind)} ('{t.Lexeme}') at {t.Where}.");
        }
    }
}
