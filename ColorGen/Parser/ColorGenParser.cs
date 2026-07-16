// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ColorGenParser.cs
//
// Recursive-descent parser for the ColorGen DSL. Produces a typed AST
// (CgProgram). Type inference happens during parsing so the emitter can
// walk the tree once and emit the right C# expression form per node.
//
// Type rules
//   • Number, built-in scalar input, let-bound scalar     → Scalar
//   • rgb/hsv/hsl/palette/brightness/contrast/gamma calls → Vec3
//   • Channel access (.r/.g/.b on a Vec3)                 → Scalar
//   • Binary + - * / % ^ : Scalar↔Scalar → Scalar
//                          Scalar↔Vec3   → Vec3 (broadcast)
//                          Vec3↔Vec3     → Vec3 (elementwise)
//   • Comparisons (< <= > >= == !=)      → Scalar (0/1); require Scalar
//   • Logical && || !                    → Scalar; require Scalar
//   • Ternary cond ? a : b               → type(a). a/b must match.
//   • mix is polymorphic: (S,S,S) → S, (V,V,S) → V.
//
// Final statement MUST be `return <vec3-expr>;`.

using System;
using System.Collections.Generic;

namespace FracturingFog.ColorGen.Parser;

public sealed class ColorGenParseException : Exception
{
    public ColorGenParseException(string msg) : base(msg) { }
}

public sealed class ColorGenParser
{
    private readonly List<CgToken> _toks;
    private int _pos;
    private readonly Dictionary<string, CgType> _locals = new(StringComparer.Ordinal);

    private ColorGenParser(List<CgToken> toks) { _toks = toks; }

    public static CgProgram Parse(string source)
    {
        var toks = ColorGenLexer.Tokenize(source);
        var p = new ColorGenParser(toks);
        return p.ParseProgram();
    }

    // ── Token helpers ─────────────────────────────────────────────────────

    private CgToken Peek(int offset = 0) => _toks[_pos + offset];
    private CgToken Next() { var t = _toks[_pos]; _pos++; return t; }

    private bool Accept(CgTokenKind k)
    {
        if (Peek().Kind == k) { _pos++; return true; }
        return false;
    }

    private CgToken Expect(CgTokenKind k, string what)
    {
        var t = Peek();
        if (t.Kind != k)
            throw new ColorGenParseException($"Expected {what} at {t.Where}, got '{t.Lexeme}'.");
        _pos++;
        return t;
    }

    // ── Program ───────────────────────────────────────────────────────────

    private CgProgram ParseProgram()
    {
        var prog = new CgProgram();
        while (Peek().Kind != CgTokenKind.End)
        {
            var s = ParseStmt();
            prog.Statements.Add(s);
            if (s is CgReturn ret)
            {
                prog.Return = ret;
                if (Peek().Kind != CgTokenKind.End)
                    throw new ColorGenParseException(
                        $"Stray tokens after 'return' at {Peek().Where}. " +
                        "The return statement must be last.");
                break;
            }
        }
        if (prog.Return == null)
            throw new ColorGenParseException("Program has no 'return' statement.");
        if (prog.Return.Value.Type != CgType.Vec3)
            throw new ColorGenParseException(
                $"'return' must yield a Vec3 (use rgb/hsv/hsl/palette); " +
                $"got Scalar at line {prog.Return.Line}, col {prog.Return.Column}.");
        return prog;
    }

    private CgStmt ParseStmt()
    {
        var t = Peek();
        if (t.Kind == CgTokenKind.Let)
        {
            Next();
            var name = Expect(CgTokenKind.Ident, "identifier after 'let'").Lexeme;
            if (CgInputs.Scalars.Contains(name) || CgInputs.Constants.ContainsKey(name))
                throw new ColorGenParseException(
                    $"'{name}' is a built-in input/constant — pick another name at {t.Where}.");
            if (_locals.ContainsKey(name))
                throw new ColorGenParseException(
                    $"'{name}' already bound at {t.Where}.");
            Expect(CgTokenKind.Assign, "'='");
            var v = ParseExpr();
            Expect(CgTokenKind.Semi, "';'");
            _locals[name] = v.Type;
            return new CgLet { Name = name, Value = v, Line = t.Line, Column = t.Column };
        }
        if (t.Kind == CgTokenKind.Return)
        {
            Next();
            var v = ParseExpr();
            Expect(CgTokenKind.Semi, "';'");
            return new CgReturn { Value = v, Line = t.Line, Column = t.Column };
        }
        throw new ColorGenParseException(
            $"Expected 'let' or 'return' at {t.Where}, got '{t.Lexeme}'.");
    }

    // ── Expressions ───────────────────────────────────────────────────────

    private CgNode ParseExpr() => ParseTernary();

    private CgNode ParseTernary()
    {
        var cond = ParseOr();
        if (Accept(CgTokenKind.Question))
        {
            if (cond.Type != CgType.Scalar)
                throw new ColorGenParseException(
                    $"Ternary condition must be scalar at line {cond.Line}, col {cond.Column}.");
            var a = ParseExpr();
            Expect(CgTokenKind.Colon, "':'");
            var b = ParseExpr();
            if (a.Type != b.Type)
                throw new ColorGenParseException(
                    $"Ternary branches must have matching types " +
                    $"(got {a.Type} and {b.Type}) at line {a.Line}, col {a.Column}.");
            return new CgTernary
            {
                Cond = cond,
                IfTrue = a,
                IfFalse = b,
                Type = a.Type,
                Line = cond.Line,
                Column = cond.Column,
            };
        }
        return cond;
    }

    private CgNode ParseOr()
    {
        var lhs = ParseAnd();
        while (Accept(CgTokenKind.OrOr))
        {
            var rhs = ParseAnd();
            RequireScalar(lhs, "left side of '||'");
            RequireScalar(rhs, "right side of '||'");
            lhs = new CgBinary { Op = CgBinOp.Or, Lhs = lhs, Rhs = rhs, Type = CgType.Scalar, Line = lhs.Line, Column = lhs.Column };
        }
        return lhs;
    }

    private CgNode ParseAnd()
    {
        var lhs = ParseCompare();
        while (Accept(CgTokenKind.AndAnd))
        {
            var rhs = ParseCompare();
            RequireScalar(lhs, "left side of '&&'");
            RequireScalar(rhs, "right side of '&&'");
            lhs = new CgBinary { Op = CgBinOp.And, Lhs = lhs, Rhs = rhs, Type = CgType.Scalar, Line = lhs.Line, Column = lhs.Column };
        }
        return lhs;
    }

    private CgNode ParseCompare()
    {
        var lhs = ParseSum();
        var k = Peek().Kind;
        CgBinOp? op = k switch
        {
            CgTokenKind.Lt   => CgBinOp.Lt,
            CgTokenKind.Le   => CgBinOp.Le,
            CgTokenKind.Gt   => CgBinOp.Gt,
            CgTokenKind.Ge   => CgBinOp.Ge,
            CgTokenKind.EqEq => CgBinOp.Eq,
            CgTokenKind.NotEq=> CgBinOp.Ne,
            _ => null,
        };
        if (op == null) return lhs;
        Next();
        var rhs = ParseSum();
        RequireScalar(lhs, "comparison left side");
        RequireScalar(rhs, "comparison right side");
        return new CgBinary { Op = op.Value, Lhs = lhs, Rhs = rhs, Type = CgType.Scalar, Line = lhs.Line, Column = lhs.Column };
    }

    private CgNode ParseSum()
    {
        var lhs = ParseTerm();
        while (true)
        {
            var k = Peek().Kind;
            CgBinOp op;
            if (k == CgTokenKind.Plus) op = CgBinOp.Add;
            else if (k == CgTokenKind.Minus) op = CgBinOp.Sub;
            else break;
            Next();
            var rhs = ParseTerm();
            lhs = MakeArithBinary(op, lhs, rhs);
        }
        return lhs;
    }

    private CgNode ParseTerm()
    {
        var lhs = ParsePower();
        while (true)
        {
            var k = Peek().Kind;
            CgBinOp op;
            if (k == CgTokenKind.Star) op = CgBinOp.Mul;
            else if (k == CgTokenKind.Slash) op = CgBinOp.Div;
            else if (k == CgTokenKind.Percent) op = CgBinOp.Mod;
            else break;
            Next();
            var rhs = ParsePower();
            lhs = MakeArithBinary(op, lhs, rhs);
        }
        return lhs;
    }

    private CgNode ParsePower()
    {
        var lhs = ParseUnary();
        if (Accept(CgTokenKind.Caret))
        {
            var rhs = ParsePower(); // right-assoc
            lhs = MakeArithBinary(CgBinOp.Pow, lhs, rhs);
        }
        return lhs;
    }

    private CgNode ParseUnary()
    {
        var t = Peek();
        if (t.Kind == CgTokenKind.Minus)
        {
            Next();
            var v = ParseUnary();
            return new CgUnary { Op = CgUnaryOp.Neg, Operand = v, Type = v.Type, Line = t.Line, Column = t.Column };
        }
        if (t.Kind == CgTokenKind.Plus)
        {
            Next();
            return ParseUnary();
        }
        if (t.Kind == CgTokenKind.Bang)
        {
            Next();
            var v = ParseUnary();
            RequireScalar(v, "operand of '!'");
            return new CgUnary { Op = CgUnaryOp.Not, Operand = v, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
        }
        return ParsePostfix();
    }

    private CgNode ParsePostfix()
    {
        var node = ParsePrimary();
        while (Accept(CgTokenKind.Dot))
        {
            var idTok = Expect(CgTokenKind.Ident, "channel name ('r', 'g', or 'b')");
            if (idTok.Lexeme.Length != 1 || (idTok.Lexeme[0] != 'r' && idTok.Lexeme[0] != 'g' && idTok.Lexeme[0] != 'b'))
                throw new ColorGenParseException(
                    $"Channel must be 'r', 'g', or 'b' at {idTok.Where}, got '{idTok.Lexeme}'.");
            if (node.Type != CgType.Vec3)
                throw new ColorGenParseException(
                    $"Channel access requires a Vec3 at line {node.Line}, col {node.Column}.");
            node = new CgChannel
            {
                Target = node,
                Channel = idTok.Lexeme[0],
                Type = CgType.Scalar,
                Line = node.Line,
                Column = node.Column,
            };
        }
        return node;
    }

    private CgNode ParsePrimary()
    {
        var t = Peek();
        if (t.Kind == CgTokenKind.Number)
        {
            Next();
            return new CgNumber { Value = t.NumberValue, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
        }
        if (t.Kind == CgTokenKind.True)
        {
            Next();
            return new CgNumber { Value = 1.0, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
        }
        if (t.Kind == CgTokenKind.False)
        {
            Next();
            return new CgNumber { Value = 0.0, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
        }
        if (t.Kind == CgTokenKind.LParen)
        {
            Next();
            var v = ParseExpr();
            Expect(CgTokenKind.RParen, "')'");
            return v;
        }
        if (t.Kind == CgTokenKind.Ident)
        {
            Next();
            // Function call?
            if (Peek().Kind == CgTokenKind.LParen)
            {
                Next();
                var args = new List<CgNode>();
                if (Peek().Kind != CgTokenKind.RParen)
                {
                    args.Add(ParseExpr());
                    while (Accept(CgTokenKind.Comma))
                        args.Add(ParseExpr());
                }
                Expect(CgTokenKind.RParen, "')'");
                return BuildCall(t, args);
            }
            // Variable / constant.
            if (CgInputs.Scalars.Contains(t.Lexeme))
                return new CgVar { Name = t.Lexeme, IsBuiltIn = true, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
            if (CgInputs.Constants.TryGetValue(t.Lexeme, out var k))
                return new CgNumber { Value = k, Type = CgType.Scalar, Line = t.Line, Column = t.Column };
            if (_locals.TryGetValue(t.Lexeme, out var localType))
                return new CgVar { Name = t.Lexeme, IsBuiltIn = false, Type = localType, Line = t.Line, Column = t.Column };
            throw new ColorGenParseException(
                $"Unknown identifier '{t.Lexeme}' at {t.Where}. " +
                "Inputs: smooth, dist, iter, maxIter, t, nx, ny, zr, zi, dzr, dzi, arg, mag, isInSet. " +
                "Constants: pi, tau, e, phi.");
        }
        throw new ColorGenParseException(
            $"Unexpected '{t.Lexeme}' at {t.Where}.");
    }

    private CgNode BuildCall(CgToken nameTok, List<CgNode> args)
    {
        string name = nameTok.Lexeme;
        if (!CgFunctions.Table.TryGetValue(name, out var sig))
            throw new ColorGenParseException(
                $"Unknown function '{name}' at {nameTok.Where}.");

        // mix special-case: scalar form (S,S,S) or vec3 form (V,V,S).
        if (name == "mix")
        {
            if (args.Count != 3)
                throw new ColorGenParseException(
                    $"mix() takes 3 args at {nameTok.Where}.");
            if (args[0].Type == CgType.Vec3 && args[1].Type == CgType.Vec3)
            {
                RequireScalar(args[2], "mix t");
                return new CgCall { Name = "mix_v", Args = args, Type = CgType.Vec3, Line = nameTok.Line, Column = nameTok.Column };
            }
            // Otherwise treat as scalar mix; broadcast Scalar Vec3 cases is not supported.
            RequireScalar(args[0], "mix a");
            RequireScalar(args[1], "mix b");
            RequireScalar(args[2], "mix t");
            return new CgCall { Name = "mix", Args = args, Type = CgType.Scalar, Line = nameTok.Line, Column = nameTok.Column };
        }

        if (!sig.IsVariadic && args.Count != sig.ArgArity)
            throw new ColorGenParseException(
                $"{name}() takes {sig.ArgArity} args at {nameTok.Where}, got {args.Count}.");
        if (sig.IsVariadic && args.Count < sig.ArgArity)
            throw new ColorGenParseException(
                $"{name}() takes at least {sig.ArgArity} args at {nameTok.Where}, got {args.Count}.");

        // palette: first arg scalar (t), trailing args all Vec3.
        // Handled before the generic check because palette is variadic with
        // a mixed-type signature (Scalar + Vec3*) that doesn't fit the
        // "every arg must be Scalar" default for scalar-typed signatures.
        if (name == "palette")
        {
            if (args[0].Type != CgType.Scalar)
                throw new ColorGenParseException(
                    $"palette() arg 1 must be scalar at line {args[0].Line}, col {args[0].Column}.");
            for (int i = 1; i < args.Count; i++)
                if (args[i].Type != CgType.Vec3)
                    throw new ColorGenParseException(
                        $"palette() stops must be Vec3 (use rgb/hsv/hsl) at line {args[i].Line}, col {args[i].Column}.");
        }
        else if (sig.RequiredArgTypes != null)
        {
            // Type-check positional required args.
            for (int i = 0; i < sig.RequiredArgTypes.Length; i++)
            {
                if (args[i].Type != sig.RequiredArgTypes[i])
                    throw new ColorGenParseException(
                        $"{name}() arg {i + 1} must be {sig.RequiredArgTypes[i]} at line {args[i].Line}, col {args[i].Column} (got {args[i].Type}).");
            }
        }
        else
        {
            // Scalar-default functions: every arg must be Scalar.
            foreach (var a in args)
                if (a.Type != CgType.Scalar)
                    throw new ColorGenParseException(
                        $"{name}() requires scalar args at line {a.Line}, col {a.Column} (got {a.Type}).");
        }

        return new CgCall { Name = name, Args = args, Type = sig.RetType, Line = nameTok.Line, Column = nameTok.Column };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void RequireScalar(CgNode n, string what)
    {
        if (n.Type != CgType.Scalar)
            throw new ColorGenParseException(
                $"{what} must be Scalar at line {n.Line}, col {n.Column}.");
    }

    /// <summary>Builds a + - * / % ^ node; broadcasts Scalar↔Vec3 to Vec3.
    /// Mod and Pow are valid for both types (elementwise on Vec3).</summary>
    private static CgBinary MakeArithBinary(CgBinOp op, CgNode lhs, CgNode rhs)
    {
        var t = (lhs.Type, rhs.Type) switch
        {
            (CgType.Scalar, CgType.Scalar) => CgType.Scalar,
            _                              => CgType.Vec3,
        };
        return new CgBinary { Op = op, Lhs = lhs, Rhs = rhs, Type = t, Line = lhs.Line, Column = lhs.Column };
    }
}
