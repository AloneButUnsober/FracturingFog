// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Language/EquationLanguage.cs
//
// #1086 (#937 Phase 1) — the single entry point of the unified 2D equation
// language shared by the User Equation editor, the DSL tab, the Sandbox editor,
// the z0-seed / bailout-condition fields, and (from #1087) CalcGen's lowering.
//
// One parser, one AST: the language IS SandboxExpression's grammar (see the
// spec, Docs/Technical/Equation-Language.md). This facade names it, exposes the
// parse with errors in the shared "at line L, col C … Did you mean 'x'?" form,
// and prints the AST as an S-expression for tests, diagnostics and the coming
// lowering. It lives in CalculatorGen.Lib (below Engine) so CalcGen can consume
// the same tree without referencing Engine.

using System;
using System.Globalization;
using System.Text;

namespace FracturingFog.Models
{
    public static class EquationLanguage
    {
        /// <summary>Parse an equation in the unified language. Throws
        /// <see cref="FormatException"/> with a positioned message on error.</summary>
        public static SandboxExpression Parse(string source) => SandboxExpression.Parse(source);

        /// <summary>Parse without throwing. On failure <paramref name="error"/>
        /// carries the positioned message and <paramref name="expression"/> is null.</summary>
        public static bool TryParse(string source, out SandboxExpression? expression, out string? error)
        {
            try
            {
                expression = SandboxExpression.Parse(source);
                error = null;
                return true;
            }
            catch (FormatException ex)
            {
                expression = null;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>The parsed tree as an S-expression: <c>(+ (* z z) c)</c>,
        /// <c>(^ (neg z) 2)</c>, <c>(? cond then else)</c>,
        /// <c>(let $5 value body)</c>. Input slots print by name (z c n prev
        /// iter); let / statement locals print as <c>$slot</c>. Numbers use the
        /// round-trip invariant format; <c>i</c> prints as <c>(cx 0 1)</c>.</summary>
        public static string ToSExpression(SbxNode node)
        {
            var sb = new StringBuilder();
            Write(sb, node);
            return sb.ToString();
        }

        /// <inheritdoc cref="ToSExpression(SbxNode)"/>
        public static string ToSExpression(SandboxExpression expression) => ToSExpression(expression.Root);

        private static void Write(StringBuilder sb, SbxNode n)
        {
            switch (n)
            {
                case SbxConst k:
                    if (k.V.IsReal) sb.Append(Num(k.V.R));
                    else sb.Append("(cx ").Append(Num(k.V.R)).Append(' ').Append(Num(k.V.I)).Append(')');
                    break;
                case SbxSlot s:
                    sb.Append(SlotName(s.Slot));
                    break;
                case SbxLet l:
                    sb.Append("(let ").Append(SlotName(l.Slot)).Append(' ');
                    Write(sb, l.Value); sb.Append(' '); Write(sb, l.Body); sb.Append(')');
                    break;
                case SbxUnary u:
                    sb.Append(u.Op == '-' ? "(neg " : "(! ");
                    Write(sb, u.A); sb.Append(')');
                    break;
                case SbxBinary b:
                    sb.Append('(').Append(b.Op).Append(' ');
                    Write(sb, b.A); sb.Append(' '); Write(sb, b.B); sb.Append(')');
                    break;
                case SbxTernary t:
                    sb.Append("(? ");
                    Write(sb, t.Cond); sb.Append(' '); Write(sb, t.Then); sb.Append(' '); Write(sb, t.Else); sb.Append(')');
                    break;
                case SbxCall c:
                    sb.Append('(').Append(c.Name);
                    foreach (var a in c.Args) { sb.Append(' '); Write(sb, a); }
                    sb.Append(')');
                    break;
                default:
                    sb.Append('<').Append(n.GetType().Name).Append('>');
                    break;
            }
        }

        private static string SlotName(int slot) => slot switch
        {
            SandboxExpression.SlotZ => "z",
            SandboxExpression.SlotC => "c",
            SandboxExpression.SlotN => "n",
            SandboxExpression.SlotPrev => "prev",
            SandboxExpression.SlotIter => "iter",
            _ => "$" + slot.ToString(CultureInfo.InvariantCulture),
        };

        private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }
}
