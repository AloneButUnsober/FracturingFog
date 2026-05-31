// AstPrinter.cs
//
// Pretty-prints an AstNode back to source form. Used for the top-of-file
// banner in generated calculators ("derivative: 2*z") and for diagnostics.
// Adds parens only when precedence requires them.

using System.Globalization;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class AstPrinter
{
    public static string Print(AstNode node)
    {
        var sb = new StringBuilder();
        WriteExpr(sb, node, parentPrec: 0);
        return sb.ToString();
    }

    // Precedence: 0 = top (Add/Sub), 1 = Mul, 2 = Pow/Neg/atom
    private static void WriteExpr(StringBuilder sb, AstNode node, int parentPrec)
    {
        switch (node)
        {
            case ZRef:        sb.Append('z'); break;
            case CRef:        sb.Append('c'); break;
            case DRef:        sb.Append('D'); break;     // shows up in derivative trees only
            case DeltaRef:    sb.Append('δ'); break;     // shows up in perturbation trees only
            case EpsRef:      sb.Append('ε'); break;     // shows up in perturbation trees only
            case RealConst k: sb.Append(k.Value.ToString("R", CultureInfo.InvariantCulture)); break;
            case Neg n:
                Wrap(sb, parentPrec, 2, () => { sb.Append('-'); WriteExpr(sb, n.Operand, 2); });
                break;
            case Add a:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, a.Left, 0); sb.Append(" + "); WriteExpr(sb, a.Right, 0); });
                break;
            case Sub s:
                Wrap(sb, parentPrec, 0, () => { WriteExpr(sb, s.Left, 0); sb.Append(" - "); WriteExpr(sb, s.Right, 1); });
                break;
            case Mul m:
                Wrap(sb, parentPrec, 1, () => { WriteExpr(sb, m.Left, 1); sb.Append('*'); WriteExpr(sb, m.Right, 1); });
                break;
            case Pow p:
                Wrap(sb, parentPrec, 2, () => { WriteExpr(sb, p.Base, 2); sb.Append('^').Append(p.Exponent); });
                break;
            default:
                throw new InvalidOperationException($"AstPrinter: unhandled {node.GetType().Name}");
        }
    }

    private static void Wrap(StringBuilder sb, int parentPrec, int myPrec, Action emit)
    {
        bool needParens = parentPrec > myPrec;
        if (needParens) sb.Append('(');
        emit();
        if (needParens) sb.Append(')');
    }
}
