// Models/SandboxExpression.cs
//
// Safe expression DSL for the Sandbox fractal type. Parses a user-supplied
// string into an AST that an interpreter evaluates per pixel. Unlike the
// Roslyn-backed UserEquationCalculator, this has no access to the BCL — only
// the operators, functions, and constants enumerated below. No file IO, no
// reflection, no P/Invoke, no allocation beyond AST + per-thread env array.
//
// Grammar (right-recursive descent):
//   expr     := let_expr
//   let_expr := "let" IDENT "=" expr "in" expr | ternary
//   ternary  := or_expr ("?" expr ":" expr)?
//   or_expr  := and_expr ("||" and_expr)*
//   and_expr := not_expr ("&&" not_expr)*
//   not_expr := "!" not_expr | cmp_expr
//   cmp_expr := add_expr ((<|>|<=|>=|==|!=) add_expr)?
//   add_expr := mul_expr (("+"|"-") mul_expr)*
//   mul_expr := pow_expr (("*"|"/") pow_expr)*
//   pow_expr := unary ("^" pow_expr)?         ; right-assoc
//   unary    := "-" unary | primary
//   primary  := NUMBER | IDENT | IDENT "(" args ")" | "(" expr ")"
//
// Built-in identifiers:
//   z, c, n               (input slots, refreshed per iteration)
//   pi, e, i              (constants)
// Functions:
//   Sin Cos Tan Exp Log Sqrt Abs Conj Re Im Arg Pow(z,w)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace FracturingFog.Models
{
    public readonly struct SbxVal
    {
        public readonly bool IsReal;
        public readonly double R;
        public readonly double I;

        public SbxVal(double r) { IsReal = true; R = r; I = 0.0; }
        public SbxVal(double r, double i) { IsReal = false; R = r; I = i; }
        public SbxVal(Complex z) { IsReal = false; R = z.Real; I = z.Imaginary; }

        public Complex AsComplex() => new Complex(R, I);
        public double AsReal() => IsReal ? R : Math.Sqrt(R * R + I * I);
        public bool AsBool() => (IsReal ? R : Math.Sqrt(R * R + I * I)) != 0.0;

        public static SbxVal Real(double r) => new SbxVal(r);
        public static SbxVal Cx(double r, double i) => new SbxVal(r, i);
        public static SbxVal Cx(Complex z) => new SbxVal(z);

        public static SbxVal Add(SbxVal a, SbxVal b)
            => a.IsReal && b.IsReal ? Real(a.R + b.R) : new SbxVal(a.R + b.R, a.I + b.I);
        public static SbxVal Sub(SbxVal a, SbxVal b)
            => a.IsReal && b.IsReal ? Real(a.R - b.R) : new SbxVal(a.R - b.R, a.I - b.I);
        public static SbxVal Mul(SbxVal a, SbxVal b)
        {
            if (a.IsReal && b.IsReal) return Real(a.R * b.R);
            // (ar + ai i)(br + bi i) = (ar*br - ai*bi) + (ar*bi + ai*br) i
            return new SbxVal(a.R * b.R - a.I * b.I, a.R * b.I + a.I * b.R);
        }
        public static SbxVal Div(SbxVal a, SbxVal b)
        {
            if (a.IsReal && b.IsReal) return Real(a.R / b.R);
            double denom = b.R * b.R + b.I * b.I;
            if (denom == 0.0) return new SbxVal(double.PositiveInfinity, 0.0);
            return new SbxVal((a.R * b.R + a.I * b.I) / denom, (a.I * b.R - a.R * b.I) / denom);
        }
        public static SbxVal Neg(SbxVal a) => a.IsReal ? Real(-a.R) : new SbxVal(-a.R, -a.I);
        public static SbxVal Pow(SbxVal a, SbxVal b)
        {
            if (a.IsReal && b.IsReal) return Real(Math.Pow(a.R, b.R));
            return Cx(Complex.Pow(a.AsComplex(), b.AsComplex()));
        }
    }

    public abstract class SbxNode
    {
        public abstract SbxVal Eval(SbxVal[] env);
    }

    public sealed class SbxConst : SbxNode
    {
        public readonly SbxVal V;
        public SbxConst(SbxVal v) { V = v; }
        public override SbxVal Eval(SbxVal[] env) => V;
    }

    public sealed class SbxSlot : SbxNode
    {
        public readonly int Slot;
        public SbxSlot(int s) { Slot = s; }
        public override SbxVal Eval(SbxVal[] env) => env[Slot];
    }

    public sealed class SbxLet : SbxNode
    {
        public readonly int Slot;
        public readonly SbxNode Value;
        public readonly SbxNode Body;
        public SbxLet(int slot, SbxNode value, SbxNode body) { Slot = slot; Value = value; Body = body; }
        public override SbxVal Eval(SbxVal[] env)
        {
            env[Slot] = Value.Eval(env);
            return Body.Eval(env);
        }
    }

    public sealed class SbxUnary : SbxNode
    {
        public readonly char Op; // '-' or '!'
        public readonly SbxNode A;
        public SbxUnary(char op, SbxNode a) { Op = op; A = a; }
        public override SbxVal Eval(SbxVal[] env)
        {
            var v = A.Eval(env);
            return Op == '-' ? SbxVal.Neg(v) : SbxVal.Real(v.AsBool() ? 0.0 : 1.0);
        }
    }

    public sealed class SbxBinary : SbxNode
    {
        public readonly string Op;
        public readonly SbxNode A, B;
        public SbxBinary(string op, SbxNode a, SbxNode b) { Op = op; A = a; B = b; }
        public override SbxVal Eval(SbxVal[] env)
        {
            // Short-circuit logical ops.
            if (Op == "&&") return SbxVal.Real(A.Eval(env).AsBool() && B.Eval(env).AsBool() ? 1.0 : 0.0);
            if (Op == "||") return SbxVal.Real(A.Eval(env).AsBool() || B.Eval(env).AsBool() ? 1.0 : 0.0);

            var a = A.Eval(env);
            var b = B.Eval(env);
            return Op switch
            {
                "+"  => SbxVal.Add(a, b),
                "-"  => SbxVal.Sub(a, b),
                "*"  => SbxVal.Mul(a, b),
                "/"  => SbxVal.Div(a, b),
                "^"  => SbxVal.Pow(a, b),
                "<"  => SbxVal.Real(a.AsReal() <  b.AsReal() ? 1.0 : 0.0),
                ">"  => SbxVal.Real(a.AsReal() >  b.AsReal() ? 1.0 : 0.0),
                "<=" => SbxVal.Real(a.AsReal() <= b.AsReal() ? 1.0 : 0.0),
                ">=" => SbxVal.Real(a.AsReal() >= b.AsReal() ? 1.0 : 0.0),
                "==" => SbxVal.Real(a.AsReal() == b.AsReal() ? 1.0 : 0.0),
                "!=" => SbxVal.Real(a.AsReal() != b.AsReal() ? 1.0 : 0.0),
                _    => throw new InvalidOperationException("Unknown op " + Op)
            };
        }
    }

    public sealed class SbxTernary : SbxNode
    {
        public readonly SbxNode Cond, Then, Else;
        public SbxTernary(SbxNode c, SbxNode t, SbxNode e) { Cond = c; Then = t; Else = e; }
        public override SbxVal Eval(SbxVal[] env) => Cond.Eval(env).AsBool() ? Then.Eval(env) : Else.Eval(env);
    }

    public sealed class SbxCall : SbxNode
    {
        public readonly string Name;
        public readonly SbxNode[] Args;
        public SbxCall(string name, SbxNode[] args) { Name = name; Args = args; }

        public override SbxVal Eval(SbxVal[] env)
        {
            if (Name == "pow")
            {
                var a = Args[0].Eval(env);
                var b = Args[1].Eval(env);
                return SbxVal.Pow(a, b);
            }

            var x = Args[0].Eval(env);
            switch (Name)
            {
                case "sin":  return x.IsReal ? SbxVal.Real(Math.Sin(x.R))  : SbxVal.Cx(Complex.Sin(x.AsComplex()));
                case "cos":  return x.IsReal ? SbxVal.Real(Math.Cos(x.R))  : SbxVal.Cx(Complex.Cos(x.AsComplex()));
                case "tan":  return x.IsReal ? SbxVal.Real(Math.Tan(x.R))  : SbxVal.Cx(Complex.Tan(x.AsComplex()));
                case "exp":  return x.IsReal ? SbxVal.Real(Math.Exp(x.R))  : SbxVal.Cx(Complex.Exp(x.AsComplex()));
                case "log":  return x.IsReal && x.R > 0
                                ? SbxVal.Real(Math.Log(x.R))
                                : SbxVal.Cx(Complex.Log(x.AsComplex()));
                case "sqrt": return x.IsReal && x.R >= 0
                                ? SbxVal.Real(Math.Sqrt(x.R))
                                : SbxVal.Cx(Complex.Sqrt(x.AsComplex()));
                case "abs":  return SbxVal.Real(x.IsReal ? Math.Abs(x.R) : Math.Sqrt(x.R * x.R + x.I * x.I));
                case "conj": return x.IsReal ? x : new SbxVal(x.R, -x.I);
                case "re":   return SbxVal.Real(x.R);
                case "im":   return SbxVal.Real(x.IsReal ? 0.0 : x.I);
                case "arg":  return SbxVal.Real(x.IsReal ? (x.R < 0 ? Math.PI : 0.0) : Math.Atan2(x.I, x.R));
                default:     throw new InvalidOperationException("Unknown function " + Name);
            }
        }
    }

    /// <summary>Parsed Sandbox expression that can be evaluated per pixel.</summary>
    public sealed class SandboxExpression
    {
        public SbxNode Root { get; }
        public int EnvSize { get; }

        // Slots 0..2 reserved: z, c, n.
        public const int SlotZ = 0;
        public const int SlotC = 1;
        public const int SlotN = 2;
        public const int ReservedSlots = 3;

        private SandboxExpression(SbxNode root, int envSize) { Root = root; EnvSize = envSize; }

        public static SandboxExpression Parse(string source)
        {
            var parser = new Parser(source);
            var root = parser.ParseProgram();
            return new SandboxExpression(root, parser.EnvSize);
        }

        public SbxVal[] NewEnv() => new SbxVal[EnvSize];

        /// <summary>Evaluate with the given z, c, iteration; env must come from <see cref="NewEnv"/>.</summary>
        public Complex EvalStep(Complex z, Complex c, int n, SbxVal[] env)
        {
            env[SlotZ] = SbxVal.Cx(z);
            env[SlotC] = SbxVal.Cx(c);
            env[SlotN] = SbxVal.Real(n);
            return Root.Eval(env).AsComplex();
        }

        // ── Parser ────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly string _src;
            private int _pos;
            private readonly Dictionary<string, int> _scope = new(StringComparer.Ordinal);
            public int EnvSize;

            public Parser(string src)
            {
                _src = src ?? string.Empty;
                _pos = 0;
                _scope["z"] = SlotZ;
                _scope["c"] = SlotC;
                _scope["n"] = SlotN;
                EnvSize = ReservedSlots;
            }

            public SbxNode ParseProgram()
            {
                SkipWs();
                var node = ParseExpr();
                SkipWs();
                if (_pos < _src.Length)
                    throw new FormatException($"Unexpected '{_src[_pos]}' at position {_pos}");
                return node;
            }

            private SbxNode ParseExpr() => ParseLet();

            private SbxNode ParseLet()
            {
                SkipWs();
                if (MatchKeyword("let"))
                {
                    SkipWs();
                    string name = ReadIdent();
                    if (string.IsNullOrEmpty(name)) throw new FormatException("Expected identifier after 'let'");
                    if (IsReservedName(name)) throw new FormatException($"Cannot rebind reserved name '{name}'");
                    SkipWs();
                    Expect('=');
                    var valueExpr = ParseExpr();
                    SkipWs();
                    if (!MatchKeyword("in")) throw new FormatException("Expected 'in' in let-expression");

                    // Bind name to a fresh slot for the body; restore prior binding on exit.
                    bool hadPrior = _scope.TryGetValue(name, out int prior);
                    int slot = EnvSize++;
                    _scope[name] = slot;
                    try
                    {
                        var body = ParseExpr();
                        return new SbxLet(slot, valueExpr, body);
                    }
                    finally
                    {
                        if (hadPrior) _scope[name] = prior;
                        else _scope.Remove(name);
                    }
                }
                return ParseTernary();
            }

            private SbxNode ParseTernary()
            {
                var cond = ParseOr();
                SkipWs();
                if (Peek() == '?')
                {
                    _pos++;
                    var thenN = ParseExpr();
                    SkipWs();
                    Expect(':');
                    var elseN = ParseExpr();
                    return new SbxTernary(cond, thenN, elseN);
                }
                return cond;
            }

            private SbxNode ParseOr()
            {
                var left = ParseAnd();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '|' && Peek(1) == '|') { _pos += 2; left = new SbxBinary("||", left, ParseAnd()); }
                    else break;
                }
                return left;
            }

            private SbxNode ParseAnd()
            {
                var left = ParseNot();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '&' && Peek(1) == '&') { _pos += 2; left = new SbxBinary("&&", left, ParseNot()); }
                    else break;
                }
                return left;
            }

            private SbxNode ParseNot()
            {
                SkipWs();
                if (Peek() == '!' && Peek(1) != '=')
                {
                    _pos++;
                    return new SbxUnary('!', ParseNot());
                }
                return ParseCmp();
            }

            private SbxNode ParseCmp()
            {
                var left = ParseAdd();
                SkipWs();
                string? op = null;
                if (Peek() == '<')      op = Peek(1) == '=' ? "<=" : "<";
                else if (Peek() == '>') op = Peek(1) == '=' ? ">=" : ">";
                else if (Peek() == '=' && Peek(1) == '=') op = "==";
                else if (Peek() == '!' && Peek(1) == '=') op = "!=";
                if (op == null) return left;
                _pos += op.Length;
                var right = ParseAdd();
                return new SbxBinary(op, left, right);
            }

            private SbxNode ParseAdd()
            {
                var left = ParseMul();
                while (true)
                {
                    SkipWs();
                    char p = Peek();
                    if (p == '+' || p == '-') { _pos++; left = new SbxBinary(p.ToString(), left, ParseMul()); }
                    else break;
                }
                return left;
            }

            private SbxNode ParseMul()
            {
                var left = ParsePow();
                while (true)
                {
                    SkipWs();
                    char p = Peek();
                    if (p == '*' || p == '/') { _pos++; left = new SbxBinary(p.ToString(), left, ParsePow()); }
                    else break;
                }
                return left;
            }

            private SbxNode ParsePow()
            {
                var left = ParseUnary();
                SkipWs();
                if (Peek() == '^') { _pos++; return new SbxBinary("^", left, ParsePow()); }
                return left;
            }

            private SbxNode ParseUnary()
            {
                SkipWs();
                if (Peek() == '-') { _pos++; return new SbxUnary('-', ParseUnary()); }
                if (Peek() == '+') { _pos++; return ParseUnary(); }
                return ParsePrimary();
            }

            private SbxNode ParsePrimary()
            {
                SkipWs();
                if (_pos >= _src.Length) throw new FormatException("Unexpected end of expression");
                char p = Peek();
                if (p == '(')
                {
                    _pos++;
                    var inner = ParseExpr();
                    SkipWs();
                    Expect(')');
                    return inner;
                }
                if (IsDigit(p) || (p == '.' && _pos + 1 < _src.Length && IsDigit(_src[_pos + 1])))
                    return ParseNumber();
                if (IsIdentStart(p))
                {
                    string name = ReadIdent();
                    SkipWs();
                    if (Peek() == '(') return ParseCall(name);

                    // Reserved single-letter constants
                    if (name == "pi") return new SbxConst(SbxVal.Real(Math.PI));
                    if (name == "e")  return new SbxConst(SbxVal.Real(Math.E));
                    if (name == "i")  return new SbxConst(SbxVal.Cx(0.0, 1.0));

                    if (_scope.TryGetValue(name, out int slot)) return new SbxSlot(slot);
                    throw new FormatException($"Unknown identifier '{name}' at {_pos}");
                }
                throw new FormatException($"Unexpected character '{p}' at {_pos}");
            }

            private SbxNode ParseCall(string name)
            {
                _pos++; // consume '('
                var args = new List<SbxNode>();
                SkipWs();
                if (Peek() != ')')
                {
                    args.Add(ParseExpr());
                    SkipWs();
                    while (Peek() == ',') { _pos++; args.Add(ParseExpr()); SkipWs(); }
                }
                Expect(')');

                string lname = name.ToLowerInvariant();
                int expected = ArityOf(lname);
                if (expected < 0) throw new FormatException($"Unknown function '{name}'");
                if (args.Count != expected)
                    throw new FormatException($"Function '{name}' takes {expected} arg(s), got {args.Count}");
                return new SbxCall(lname, args.ToArray());
            }

            private static int ArityOf(string name) => name switch
            {
                "sin" or "cos" or "tan" or "exp" or "log" or "sqrt"
                    or "abs" or "conj" or "re" or "im" or "arg" => 1,
                "pow" => 2,
                _ => -1
            };

            private SbxNode ParseNumber()
            {
                int start = _pos;
                while (_pos < _src.Length && (IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                // Optional exponent.
                if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                    while (_pos < _src.Length && IsDigit(_src[_pos])) _pos++;
                }
                string tok = _src.Substring(start, _pos - start);
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw new FormatException($"Invalid number '{tok}' at {start}");
                return new SbxConst(SbxVal.Real(d));
            }

            // ── Helpers ───────────────────────────────────────────────────────

            private char Peek(int offset = 0)
                => (_pos + offset < _src.Length) ? _src[_pos + offset] : '\0';

            private void SkipWs()
            {
                while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
            }

            private void Expect(char c)
            {
                SkipWs();
                if (_pos >= _src.Length || _src[_pos] != c)
                    throw new FormatException($"Expected '{c}' at position {_pos}");
                _pos++;
            }

            private string ReadIdent()
            {
                int start = _pos;
                if (_pos >= _src.Length || !IsIdentStart(_src[_pos])) return string.Empty;
                _pos++;
                while (_pos < _src.Length && IsIdentCont(_src[_pos])) _pos++;
                return _src.Substring(start, _pos - start);
            }

            private bool MatchKeyword(string kw)
            {
                SkipWs();
                if (_pos + kw.Length > _src.Length) return false;
                if (string.CompareOrdinal(_src, _pos, kw, 0, kw.Length) != 0) return false;
                int next = _pos + kw.Length;
                if (next < _src.Length && IsIdentCont(_src[next])) return false;
                _pos = next;
                return true;
            }

            private static bool IsReservedName(string name) =>
                name is "z" or "c" or "n" or "pi" or "e" or "i" or "let" or "in";
            private static bool IsDigit(char c) => c >= '0' && c <= '9';
            private static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
            private static bool IsIdentCont(char c) => IsIdentStart(c) || IsDigit(c);
        }
    }
}
