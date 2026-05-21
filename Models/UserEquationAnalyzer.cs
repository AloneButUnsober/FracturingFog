// Models/UserEquationAnalyzer.cs
//
// Roslyn-backed AST analyzer for the UserEquationCalculator's C# source body.
// Mirrors the role of EquationAnalyzer (which walks the Sandbox SbxNode tree)
// but operates on a SyntaxTree parsed with Microsoft.CodeAnalysis.CSharp.
//
// Detection is syntactic only — no semantic analysis, no symbol binding. This
// keeps the analyzer fast (a few hundred microseconds for typical equations)
// and avoids paying for a Roslyn compilation that already happens inside
// UserEquationCalculator. Trade-off: identifiers aliased through `using` or
// `var` chains may evade detection. Acceptable for theme-recommendation use.

using System;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FracturingFog.Models
{
    public static class UserEquationAnalyzer
    {
        /// <summary>
        /// Parses the user equation body and extracts an <see cref="EquationProfile"/>.
        /// Returns null when the body is empty or cannot be parsed.
        /// </summary>
        public static EquationProfile? TryAnalyze(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            string wrapped = body.Contains("return") ? body : $"return {body};";
            string code = "{\n" + wrapped + "\n}";
            var stmt = SyntaxFactory.ParseStatement(code);
            if (stmt == null) return null;
            if (stmt.ContainsDiagnostics)
            {
                foreach (var d in stmt.GetDiagnostics())
                    if (d.Severity == DiagnosticSeverity.Error) return null;
            }

            var acc = new Accumulator();
            Walk(stmt, acc);

            return new EquationProfile
            {
                Antiholomorphic    = acc.Antiholomorphic,
                HasAbs             = acc.HasAbs,
                Transcendental     = acc.Transcendental,
                MaxPolyDegree      = acc.MaxPolyDegree,
                IterationDependent = acc.IterationDependent,
                HasImaginaryConst  = acc.HasImaginaryConst,
                HasCRef            = acc.HasCRef,
                HasZRef            = acc.HasZRef,
                HasBranching       = acc.HasBranching,
            };
        }

        private sealed class Accumulator
        {
            public bool Antiholomorphic;
            public bool HasAbs;
            public bool Transcendental;
            public int  MaxPolyDegree = -1;
            public bool IterationDependent;
            public bool HasImaginaryConst;
            public bool HasCRef;
            public bool HasZRef;
            public bool HasBranching;
        }

        // Identifiers reserved by the UserEquationCalculator wrapper.
        private const string ZName = "z";
        private const string CName = "c";
        private const string NName = "n";

        private static void Walk(SyntaxNode root, Accumulator acc)
        {
            foreach (var node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case IdentifierNameSyntax id:
                        HandleIdent(id, acc);
                        break;

                    case InvocationExpressionSyntax inv:
                        HandleInvocation(inv, acc);
                        break;

                    case BinaryExpressionSyntax bin:
                        HandleBinary(bin, acc);
                        break;

                    case ObjectCreationExpressionSyntax oce:
                        HandleObjectCreation(oce, acc);
                        break;

                    case MemberAccessExpressionSyntax ma:
                        HandleMemberAccess(ma, acc);
                        break;

                    case ConditionalExpressionSyntax:
                    case IfStatementSyntax:
                    case SwitchStatementSyntax:
                    case SwitchExpressionSyntax:
                        acc.HasBranching = true;
                        break;
                }
            }
        }

        private static void HandleIdent(IdentifierNameSyntax id, Accumulator acc)
        {
            switch (id.Identifier.Text)
            {
                case ZName: acc.HasZRef = true; break;
                case CName: acc.HasCRef = true; break;
                case NName: acc.IterationDependent = true; break;
            }
        }

        private static void HandleMemberAccess(MemberAccessExpressionSyntax ma, Accumulator acc)
        {
            // Complex.ImaginaryOne — explicit i constant.
            if (ma.Name.Identifier.Text == "ImaginaryOne" &&
                ma.Expression is IdentifierNameSyntax left && left.Identifier.Text == "Complex")
            {
                acc.HasImaginaryConst = true;
            }
        }

        private static void HandleObjectCreation(ObjectCreationExpressionSyntax oce, Accumulator acc)
        {
            // new Complex(0, 1) — literal i constant.
            if (oce.Type is IdentifierNameSyntax tname && tname.Identifier.Text == "Complex" &&
                oce.ArgumentList?.Arguments.Count == 2 &&
                IsZeroLiteral(oce.ArgumentList.Arguments[0].Expression) &&
                IsOneLiteral(oce.ArgumentList.Arguments[1].Expression))
            {
                acc.HasImaginaryConst = true;
            }
        }

        private static void HandleInvocation(InvocationExpressionSyntax inv, Accumulator acc)
        {
            string? methodName = GetSimpleMethodName(inv.Expression);
            if (methodName == null) return;
            var args = inv.ArgumentList.Arguments;

            switch (methodName)
            {
                case "Conjugate":
                    if (args.Count >= 1 && ContainsZ(args[0].Expression))
                        acc.Antiholomorphic = true;
                    break;

                case "Abs":
                    if (args.Count >= 1 && ContainsZ(args[0].Expression))
                        acc.HasAbs = true;
                    break;

                case "Sin": case "Cos": case "Tan":
                case "Exp": case "Log":
                case "Sinh": case "Cosh": case "Tanh":
                    if (args.Count >= 1 && ContainsZ(args[0].Expression))
                        acc.Transcendental = true;
                    break;

                case "Pow":
                    // Pow(z, k) with literal k ⇒ polynomial degree contribution.
                    if (args.Count == 2 && IsZRef(args[0].Expression) &&
                        TryConstReal(args[1].Expression, out double k) && k > 0)
                    {
                        int deg = (int)Math.Round(k);
                        if (deg > acc.MaxPolyDegree) acc.MaxPolyDegree = deg;
                    }
                    break;
            }
        }

        private static void HandleBinary(BinaryExpressionSyntax bin, Accumulator acc)
        {
            switch (bin.Kind())
            {
                case SyntaxKind.MultiplyExpression:
                    // z*z, z*z*z, etc. — count consecutive z multiplications.
                    int deg = CountZMultiplications(bin);
                    if (deg >= 2 && deg > acc.MaxPolyDegree) acc.MaxPolyDegree = deg;
                    break;

                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                case SyntaxKind.EqualsExpression:
                case SyntaxKind.NotEqualsExpression:
                case SyntaxKind.LessThanExpression:
                case SyntaxKind.GreaterThanExpression:
                case SyntaxKind.LessThanOrEqualExpression:
                case SyntaxKind.GreaterThanOrEqualExpression:
                    acc.HasBranching = true;
                    break;
            }
        }

        /// <summary>
        /// Returns the number of z factors in a chained multiplication. Returns
        /// 0 when the expression is not a pure product of z's.
        /// </summary>
        private static int CountZMultiplications(ExpressionSyntax expr)
        {
            if (IsZRef(expr)) return 1;
            if (expr is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.MultiplyExpression))
            {
                int l = CountZMultiplications(b.Left);
                int r = CountZMultiplications(b.Right);
                if (l > 0 && r > 0) return l + r;
            }
            return 0;
        }

        private static string? GetSimpleMethodName(ExpressionSyntax expr) => expr switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null,
        };

        private static bool IsZRef(ExpressionSyntax expr) =>
            expr is IdentifierNameSyntax id && id.Identifier.Text == ZName;

        private static bool ContainsZ(SyntaxNode node)
        {
            foreach (var d in node.DescendantNodesAndSelf())
                if (d is IdentifierNameSyntax id && id.Identifier.Text == ZName)
                    return true;
            return false;
        }

        private static bool TryConstReal(ExpressionSyntax expr, out double value)
        {
            if (expr is LiteralExpressionSyntax lit && lit.Token.Value is IConvertible c)
            {
                try { value = Convert.ToDouble(c); return true; }
                catch { value = 0; return false; }
            }
            value = 0;
            return false;
        }

        private static bool IsZeroLiteral(ExpressionSyntax expr) =>
            expr is LiteralExpressionSyntax lit && lit.Token.Value is IConvertible c
            && SafeToDouble(c) == 0.0;

        private static bool IsOneLiteral(ExpressionSyntax expr) =>
            expr is LiteralExpressionSyntax lit && lit.Token.Value is IConvertible c
            && SafeToDouble(c) == 1.0;

        private static double SafeToDouble(IConvertible c)
        {
            try { return Convert.ToDouble(c); }
            catch { return double.NaN; }
        }
    }
}
