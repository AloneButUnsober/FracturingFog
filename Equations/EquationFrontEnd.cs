// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EquationFrontEnd.cs
//
// #1101 — the ONE parser of the equation language, shared by the 2D language
// (SandboxExpression) and the User Bulb 3D language (SandboxBulbExpression).
// Before #1101 each had its own hand-written recursive-descent parser with the
// same grammar; every surface rule (#1086, #1088, #1100) had to be written twice.
//
// The front end owns the SYNTAX: tokens, comments, precedence (current and
// language version 1), statements, `let`, `if … then … else`, the ternary,
// member access (when the language allows it), and the syntax errors with their
// "at line L, col C" positions. It builds each engine's own AST directly
// through an IEquationBuilder<T>, so there is no intermediate tree: errors are
// raised in exactly the order the old parsers raised them, and the trees are
// identical (ParserBehaviourSnapshotTests pins both).
//
// The builder owns the LANGUAGE: identifiers and scope (slots), constants,
// the function table and arity, value-specific checks, the exception type, and
// the 2D legacy condition rule. See Docs/Technical/Equation-Language.md.
//
// Grammar (both languages):
//   program  := block
//   block    := "return" expr ";"?
//             | "if" "(" expr ")" "return" expr ";"? block       ; guard
//             | "if" "(" expr ")" IDENT "=" expr ";"? block      ; conditional reassign
//             | TYPE? IDENT "=" expr ";"? block                  ; declare / reassign
//             | expr ";"?
//   expr     := "let" IDENT "=" expr "in" expr
//             | "if" or_expr "then" expr "else" expr | ternary
//   ternary  := or_expr ("?" expr ":" expr)?
//   or_expr  := and_expr ("||" and_expr)*
//   and_expr := not_expr ("&&" not_expr)*
//   not_expr := "!" not_expr | cmp_expr
//   cmp_expr := add_expr (("<"|">"|"<="|">="|"=="|"!=") add_expr)?
//   add_expr := mul_expr (("+"|"-") mul_expr)*
//   mul_expr := unary (("*"|"/") unary)*
//   unary    := ("-"|"+") unary | pow_expr
//   pow_expr := primary ("^" unary)?                     ; right-assoc
//   primary  := (NUMBER | IDENT | IDENT "(" args ")" | "(" expr ")") member*
//   member   := "." ("x"|"y"|"z"|"w")                    ; only when AllowMembers
// Language version 1 (Legacy): pow_expr := unary ("^" pow_expr)?, so -x^y was
// (-x)^y; the edit recorder wraps such a signed base in parentheses.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FracturingFog.Models
{
    /// <summary>#1101 — what a language supplies to <see cref="EquationFrontEnd{T}"/>.</summary>
    public interface IEquationBuilder<T>
    {
        /// <summary>The language's parse exception for a message at a span.</summary>
        Exception Error(string message, int position, int length = 1);

        T Number(double value);
        /// <summary>Resolve a bare identifier (scope, constants, lenient slots).</summary>
        T Identifier(string name, int position);
        /// <summary>A call, after its arguments were parsed (name as written).</summary>
        T Call(string name, int nameStart, IReadOnlyList<T> args);
        /// <summary>'-' (negate) or '!' (logical not).</summary>
        T Unary(char op, T operand);
        T Binary(string op, T a, T b);
        T Ternary(T cond, T then, T otherwise);
        T Member(T target, char axis);
        T Let(int slot, T value, T body);

        /// <summary>The condition of an <c>if … then … else</c>; a language may
        /// reinterpret it (2D version 1) and report migration edits to
        /// <paramref name="edits"/> when it is not null.</summary>
        T IfThenCondition(T cond, List<(int Pos, int Len, string Text)>? edits);

        /// <summary>Throw if <paramref name="name"/> can't be a <c>let</c> name.</summary>
        void CheckLetName(string name, int position);
        /// <summary>Throw if <paramref name="name"/> can't be assigned by a statement.</summary>
        void CheckAssignName(string name, int position);

        /// <summary>Bind <paramref name="name"/> to a fresh slot; returns the slot
        /// and what <see cref="Unbind"/> needs to restore the outer binding.</summary>
        (int Slot, int Prior, bool HadPrior) Bind(string name);
        void Unbind(string name, int prior, bool hadPrior);
        /// <summary>The node reading <paramref name="name"/>'s current binding, if bound.</summary>
        bool TryGetBound(string name, out T node);

        /// <summary>Slot-allocation state, saved and restored around lookahead.</summary>
        int Mark();
        void Reset(int mark);
    }

    /// <summary>#1101 — syntax options that differ between the languages.</summary>
    public sealed record EquationSyntax(
        bool AllowMembers,
        IReadOnlySet<string> TypeKeywords,
        IReadOnlySet<string> NonAssignableWords)
    {
        public static string At(string src, int pos)
        {
            int line = 1, col = 1;
            for (int k = 0; k < pos && k < src.Length; k++)
            {
                if (src[k] == '\n') { line++; col = 1; } else col++;
            }
            return $"line {line}, col {col}";
        }

        /// <summary>" Did you mean 'x'?" for the closest candidate within edit
        /// distance 2, else empty.</summary>
        public static string DidYouMean(string name, IEnumerable<string> candidates)
        {
            string? best = null;
            int bestD = int.MaxValue;
            string lower = name.ToLowerInvariant();
            foreach (var cand in candidates)
            {
                int d = Levenshtein(lower, cand.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = cand; }
            }
            return best != null && bestD > 0 && bestD <= 2 ? $" Did you mean '{best}'?" : string.Empty;
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }

    /// <summary>#1101 — the shared recursive-descent parser. One instance per parse.</summary>
    public sealed class EquationFrontEnd<T>
    {
        private readonly string _src;
        private int _pos;
        private readonly IEquationBuilder<T> _b;
        private readonly EquationSyntax _syntax;
        private readonly bool _legacy;
        private List<(int Pos, int Len, string Text)>? _edits;

        public EquationFrontEnd(string source, IEquationBuilder<T> builder, EquationSyntax syntax,
            bool legacy = false, List<(int Pos, int Len, string Text)>? edits = null)
        {
            _src = source ?? string.Empty;
            _b = builder;
            _syntax = syntax;
            _legacy = legacy;
            _edits = edits;
        }

        private Exception Error(string what, int pos, int len = 1)
            => _b.Error($"{what} at {EquationSyntax.At(_src, pos)}.", pos, len);

        public T ParseProgram()
        {
            SkipWs();
            var node = ParseBlock();
            SkipWs();
            // Tolerate a single trailing `;` (possibly before a comment).
            if (Peek() == ';') { _pos++; SkipWs(); }
            if (_pos < _src.Length)
                throw Error($"Unexpected '{_src[_pos]}'", _pos);
            return node;
        }

        // Statement block: desugars to let / ternary.
        private T ParseBlock()
        {
            SkipWs();

            // return <expr> ;   — the block's value; anything after it is dead
            // code and rejected by ParseProgram's trailing check.
            if (MatchKeyword("return"))
            {
                var e = ParseExpr();
                SkipWs();
                if (Peek() == ';') _pos++;
                return e;
            }

            int ifAt = _pos;
            if (MatchKeyword("if"))
            {
                SkipWs();
                // Expression form `if <cond> then a else b` unless a parenthesised
                // condition is followed by `return` or an assignment.
                if (!IsStatementIfAhead())
                {
                    var ite = ParseIfThenElse(ParseOr());
                    SkipWs();
                    if (Peek() == ';') _pos++;
                    return ite;
                }
                Expect('(');
                var cond = ParseExpr();
                Expect(')');
                SkipWs();

                if (MatchKeyword("return"))
                {
                    var thenE = ParseExpr();
                    SkipWs();
                    if (Peek() == ';') _pos++;
                    var elseE = ParseBlock();          // rest of the block
                    return _b.Ternary(cond, thenE, elseE);
                }

                int nameAt = _pos;
                string ifName = ReadIdent();
                if (string.IsNullOrEmpty(ifName))
                    throw Error("Expected an assignment or 'return' after 'if (...)'", _pos);
                SkipWs();
                Expect('=');
                var ifRhs = ParseExpr();
                SkipWs();
                if (Peek() == ';') _pos++;
                // The else branch keeps the variable's prior value, so it must be bound.
                if (!_b.TryGetBound(ifName, out var prior))
                    throw Error($"'if' assigns to unbound '{ifName}'", nameAt, ifName.Length);
                return BindBlock(ifName, _b.Ternary(cond, ifRhs, prior));
            }
            _pos = ifAt;

            var assign = TryParseAssignment();
            if (assign.Matched) return assign.Node;

            var expr = ParseExpr();
            SkipWs();
            if (Peek() == ';') _pos++;
            return expr;
        }

        // `let name = value in <rest of block>`; reassignment shadows (restored on exit).
        private T BindBlock(string name, T valueExpr)
        {
            var (slot, prior, hadPrior) = _b.Bind(name);
            try
            {
                var body = ParseBlock();
                return _b.Let(slot, valueExpr, body);
            }
            finally { _b.Unbind(name, prior, hadPrior); }
        }

        // `[type] ident = expr ;` — on no match the position is fully restored.
        private (bool Matched, T Node) TryParseAssignment()
        {
            int save = _pos;
            SkipWs();
            string first = ReadIdent();
            if (first.Length == 0 || _syntax.NonAssignableWords.Contains(first)) { _pos = save; return (false, default!); }
            string name = first;
            if (_syntax.TypeKeywords.Contains(first))
            {
                SkipWs();
                name = ReadIdent();
                if (name.Length == 0) { _pos = save; return (false, default!); }
            }
            SkipWs();
            // A single '=' (not '==') marks an assignment statement.
            if (!(Peek() == '=' && Peek(1) != '=')) { _pos = save; return (false, default!); }
            _pos++;
            _b.CheckAssignName(name, save);
            var rhs = ParseExpr();
            SkipWs();
            if (Peek() == ';') _pos++;
            return (true, BindBlock(name, rhs));
        }

        // Lookahead (no net consumption, no edits): `( expr )` followed by
        // `return` or `ident =` (not `==`) marks a statement-form `if`.
        private bool IsStatementIfAhead()
        {
            int save = _pos, mark = _b.Mark();
            var edits = _edits; _edits = null;
            try
            {
                SkipWs();
                if (Peek() != '(') return false;
                _pos++;
                ParseExpr();
                SkipWs();
                if (Peek() != ')') return false;
                _pos++;
                if (MatchKeyword("return")) return true;
                SkipWs();
                if (ReadIdent().Length == 0) return false;
                SkipWs();
                return Peek() == '=' && Peek(1) != '=';
            }
            catch (FormatException) { return false; }
            finally { _pos = save; _b.Reset(mark); _edits = edits; }
        }

        private T ParseExpr() => ParseLet();

        private T ParseLet()
        {
            SkipWs();
            if (MatchKeyword("let"))
            {
                SkipWs();
                int nameAt = _pos;
                string name = ReadIdent();
                if (string.IsNullOrEmpty(name)) throw Error("Expected an identifier after 'let'", _pos);
                _b.CheckLetName(name, nameAt);
                SkipWs();
                Expect('=');
                var valueExpr = ParseExpr();
                SkipWs();
                if (!MatchKeyword("in")) throw Error("Expected 'in' in let-expression", _pos);

                var (slot, prior, hadPrior) = _b.Bind(name);
                try
                {
                    var body = ParseExpr();
                    return _b.Let(slot, valueExpr, body);
                }
                finally { _b.Unbind(name, prior, hadPrior); }
            }
            if (MatchKeyword("if"))
                return ParseIfThenElse(ParseOr());
            return ParseTernary();
        }

        // `if <cond> then <a> else <b>` (the `if` consumed): a ternary.
        private T ParseIfThenElse(T cond)
        {
            SkipWs();
            if (!MatchKeyword("then")) throw Error("Expected 'then' after the 'if' condition", _pos);
            var thenN = ParseExpr();
            SkipWs();
            if (!MatchKeyword("else")) throw Error("Expected 'else' after the 'then' branch", _pos);
            var elseN = ParseExpr();
            return _b.Ternary(_b.IfThenCondition(cond, _legacy ? _edits : null), thenN, elseN);
        }

        private T ParseTernary()
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
                return _b.Ternary(cond, thenN, elseN);
            }
            return cond;
        }

        private T ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                SkipWs();
                if (Peek() == '|' && Peek(1) == '|') { _pos += 2; left = _b.Binary("||", left, ParseAnd()); }
                else break;
            }
            return left;
        }

        private T ParseAnd()
        {
            var left = ParseNot();
            while (true)
            {
                SkipWs();
                if (Peek() == '&' && Peek(1) == '&') { _pos += 2; left = _b.Binary("&&", left, ParseNot()); }
                else break;
            }
            return left;
        }

        private T ParseNot()
        {
            SkipWs();
            if (Peek() == '!' && Peek(1) != '=')
            {
                _pos++;
                return _b.Unary('!', ParseNot());
            }
            return ParseCmp();
        }

        private T ParseCmp()
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
            return _b.Binary(op, left, right);
        }

        private T ParseAdd()
        {
            var left = ParseMul();
            while (true)
            {
                SkipWs();
                char p = Peek();
                if (p == '+' || p == '-') { _pos++; left = _b.Binary(p.ToString(), left, ParseMul()); }
                else break;
            }
            return left;
        }

        private T ParseMul()
        {
            var left = ParseFactor();
            while (true)
            {
                SkipWs();
                char p = Peek();
                if (p == '*' || p == '/') { _pos++; left = _b.Binary(p.ToString(), left, ParseFactor()); }
                else break;
            }
            return left;
        }

        private T ParseFactor() => _legacy ? ParsePowLegacy() : ParseUnary();

        // Current rules: unary minus is looser than ^, so -z^2 = -(z^2); ^ is
        // right-associative and its exponent may carry a sign (z^-2).
        private T ParseUnary()
        {
            SkipWs();
            if (Peek() == '-') { _pos++; return _b.Unary('-', ParseUnary()); }
            if (Peek() == '+') { _pos++; return ParseUnary(); }
            return ParsePow();
        }

        private T ParsePow()
        {
            var left = ParsePrimary();
            SkipWs();
            if (Peek() == '^') { _pos++; return _b.Binary("^", left, ParseUnary()); }
            return left;
        }

        // Language version 1: pow := unary ("^" pow)?, so -x^y = (-x)^y. The
        // migration wraps such a signed base in parentheses, "(-x)^y", which
        // reads the same under both rule sets.
        private T ParsePowLegacy()
        {
            SkipWs();
            int start = _pos;
            char first = Peek();
            var left = ParseUnaryLegacy();
            int end = _pos;
            SkipWs();
            if (Peek() == '^')
            {
                if (_edits != null && (first == '-' || first == '+'))
                {
                    _edits.Add((start, 0, "("));
                    _edits.Add((end, 0, ")"));
                }
                _pos++;
                return _b.Binary("^", left, ParsePowLegacy());
            }
            return left;
        }

        private T ParseUnaryLegacy()
        {
            SkipWs();
            if (Peek() == '-') { _pos++; return _b.Unary('-', ParseUnaryLegacy()); }
            if (Peek() == '+') { _pos++; return ParseUnaryLegacy(); }
            return ParsePrimary();
        }

        private T ParsePrimary()
        {
            SkipWs();
            if (_pos >= _src.Length) throw Error("Unexpected end of expression", _pos);
            char p = Peek();
            T node;
            if (p == '(')
            {
                _pos++;
                node = ParseExpr();
                SkipWs();
                Expect(')');
            }
            else if (IsDigit(p) || (p == '.' && _pos + 1 < _src.Length && IsDigit(_src[_pos + 1])))
            {
                node = ParseNumber();
            }
            else if (IsIdentStart(p))
            {
                int identStart = _pos;
                string name = ReadIdent();
                SkipWs();
                node = Peek() == '(' ? ParseCall(name, identStart) : _b.Identifier(name, identStart);
            }
            else
            {
                throw Error($"Unexpected character '{p}'", _pos);
            }

            if (!_syntax.AllowMembers) return node;
            // Member access chain: foo.x.y — .x .y .z (vec/quat) or .w (quat).
            while (true)
            {
                SkipWs();
                if (Peek() != '.') break;
                if (_pos + 1 < _src.Length && IsDigit(_src[_pos + 1])) break;
                _pos++;
                SkipWs();
                char ax = Peek();
                if (ax != 'x' && ax != 'y' && ax != 'z' && ax != 'w')
                    throw Error("Expected .x/.y/.z/.w", _pos);
                _pos++;
                node = _b.Member(node, ax);
            }
            return node;
        }

        private T ParseCall(string name, int nameStart)
        {
            _pos++; // consume '('
            var args = new List<T>();
            SkipWs();
            if (Peek() != ')')
            {
                args.Add(ParseExpr());
                SkipWs();
                while (Peek() == ',') { _pos++; args.Add(ParseExpr()); SkipWs(); }
            }
            Expect(')');
            return _b.Call(name, nameStart, args);
        }

        private T ParseNumber()
        {
            int start = _pos;
            while (_pos < _src.Length && (IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
            if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                while (_pos < _src.Length && IsDigit(_src[_pos])) _pos++;
            }
            string tok = _src.Substring(start, _pos - start);
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw Error($"Invalid number '{tok}'", start, tok.Length);
            return _b.Number(d);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private char Peek(int offset = 0)
            => (_pos + offset < _src.Length) ? _src[_pos + offset] : '\0';

        // Whitespace and comments: `//` to end of line, `/* */` blocks. A lone
        // `/` is left for the division operator.
        private void SkipWs()
        {
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (char.IsWhiteSpace(c)) { _pos++; continue; }
                if (c == '/' && _pos + 1 < _src.Length)
                {
                    char d = _src[_pos + 1];
                    if (d == '/')
                    {
                        _pos += 2;
                        while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                        continue;
                    }
                    if (d == '*')
                    {
                        _pos += 2;
                        while (_pos + 1 < _src.Length && !(_src[_pos] == '*' && _src[_pos + 1] == '/')) _pos++;
                        _pos = Math.Min(_src.Length, _pos + 2);
                        continue;
                    }
                }
                break;
            }
        }

        private void Expect(char c)
        {
            SkipWs();
            if (_pos >= _src.Length || _src[_pos] != c)
                throw Error($"Expected '{c}'", _pos);
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

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        private static bool IsIdentCont(char c) => IsIdentStart(c) || IsDigit(c);
    }
}
