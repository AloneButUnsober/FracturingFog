// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Program.cs — CalculatorGen CLI entry
//
// Usage
//   CalculatorGen --equation "z*z + c" --name MandelbrotZ2 --out Calculators/Generated [--selftest] [--dry-run]
//
// Flags
//   --equation, -e   Required. The right-hand side of z_{n+1} = … using
//                    'z', 'c', integer powers (^2..^16), + - *, parens,
//                    real literals.
//   --name, -n       Required. Class name (suffix "Calculator" is appended
//                    automatically if missing).
//   --out, -o        Output directory. Defaults to ./Generated.
//   --selftest       Also emit <Name>SelfTest.cs validating scalar↔AVX2.
//   --dry-run        Print to stdout instead of writing files.

using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen;

public static class Program
{
    public static int Main(string[] args)
    {
        string? equation = null;
        string? name     = null;
        string outDir    = "./Generated";
        bool dryRun      = false;
        bool selfTest    = false;
        bool analyze     = false;
        double bailout   = 512.0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--equation":  case "-e": equation = args[++i]; break;
                case "--name":      case "-n": name     = args[++i]; break;
                case "--out":       case "-o": outDir   = args[++i]; break;
                case "--selftest":              selfTest = true; break;
                case "--dry-run":               dryRun   = true; break;
                case "--analyze":               analyze  = true; break;
                case "--bailout":              bailout  = double.Parse(args[++i],
                    System.Globalization.CultureInfo.InvariantCulture); break;
                case "--help": case "-h":      PrintHelp(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown arg: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        // --analyze: parse + diff + print, no file output, no template
        // expansion. Lets users sanity-check an equation before paying
        // the full generation pipeline.
        if (analyze)
        {
            if (string.IsNullOrWhiteSpace(equation))
            {
                Console.Error.WriteLine("--analyze requires --equation.");
                return 2;
            }
            try
            {
                var root = EquationParser.Parse(equation);
                var dpdz = AstDifferentiator.DpDz(root);
                var dpdc = AstDifferentiator.DpDc(root);
                var deriv = AstDifferentiator.BuildDerivativeUpdate(root);
                bool hasConj = AstHelpers.Contains<Conj>(root);
                bool hasFolded = AstHelpers.Contains<Folded>(root);
                bool hasDiv = AstHelpers.Contains<Div>(root);
                bool hasTrans = AstHelpers.Contains<Sin>(root)
                             || AstHelpers.Contains<Cos>(root)
                             || AstHelpers.Contains<Exp>(root)
                             || AstHelpers.Contains<Log>(root);
                bool hasCond = AstHelpers.Contains<If>(root);
                bool hasPrev = AstHelpers.Contains<PrevRef>(root);
                bool hasIter = AstHelpers.Contains<IterRef>(root);
                int saDegree = AstSaDetector.DetectZdPlusC(root);
                Console.Out.WriteLine($"Equation:    {equation}");
                Console.Out.WriteLine($"Parsed AST:  {AstPrinter.Print(root)}");
                Console.Out.WriteLine($"∂p/∂z:       {AstPrinter.Print(dpdz)}");
                Console.Out.WriteLine($"∂p/∂c:       {AstPrinter.Print(dpdc)}");
                Console.Out.WriteLine($"dz/dc step:  {AstPrinter.Print(deriv)}");
                Console.Out.WriteLine($"Has Conj:    {hasConj}");
                Console.Out.WriteLine($"Has Folded:  {hasFolded}");
                Console.Out.WriteLine($"Has Div:     {hasDiv}");
                Console.Out.WriteLine($"Has Trans:   {hasTrans}");
                Console.Out.WriteLine($"Has Cond:    {hasCond}");
                Console.Out.WriteLine($"Has Prev:    {hasPrev}");
                Console.Out.WriteLine($"Has Iter:    {hasIter}");
                Console.Out.WriteLine($"SupportsDe:  {!(hasConj || hasFolded || hasPrev)}");
                Console.Out.WriteLine($"SupportsPT:  {!(hasConj || hasFolded || hasDiv || hasTrans || hasCond || hasPrev || hasIter)}");
                Console.Out.WriteLine($"SA degree:   {(saDegree >= 2 ? saDegree.ToString() : "(not z^d+c)")}");
                var (genPoly, genDeg) = AstSaDetector.DetectPolyInZPlusC(root);
                if (genPoly != null && saDegree < 2)
                    Console.Out.WriteLine($"SA generic:  degree {genDeg}, F(z) = {AstPrinter.Print(genPoly)}");
                if (saDegree >= 2)
                {
                    Console.Out.WriteLine($"Perturb δ:   {AstPrinter.Print(AstPerturbation.BuildDeltaUpdate(root))}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Parse error: {ex.Message}");
                return 1;
            }
        }

        if (equation is null || name is null)
        {
            Console.Error.WriteLine("Both --equation and --name are required.");
            PrintHelp();
            return 2;
        }

        var result = CalculatorGenApi.Generate(equation, name, selfTest, bailout);
        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        if (dryRun)
        {
            Console.Out.Write(result.Source);
            if (result.SelfTest != null)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("// ───── self-test ─────");
                Console.Out.Write(result.SelfTest);
            }
            return 0;
        }

        Directory.CreateDirectory(outDir);
        string calcPath = Path.Combine(outDir, $"{result.ClassName}.cs");
        File.WriteAllText(calcPath, result.Source, new UTF8Encoding(false));
        Console.Out.WriteLine($"Wrote {calcPath}");

        if (result.SelfTest != null)
        {
            string stPath = Path.Combine(outDir, $"{result.ClassName}SelfTest.cs");
            File.WriteAllText(stPath, result.SelfTest, new UTF8Encoding(false));
            Console.Out.WriteLine($"Wrote {stPath}");
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine(
@"CalculatorGen v0.2
Usage:
  CalculatorGen --equation ""z*z + c"" --name MandelbrotZ2 [--out DIR] [--selftest] [--dry-run]

Equation grammar
  Tokens:    z   c   real-literal   + - *   ^Int   ( )
  Notes:     '^' takes an integer exponent (0..64). Division and
             transcendentals are reserved for later phases.

Phase B (default)
  Symbolic differentiation is always applied. The generated calculator
  tracks dz/dc per iteration and emits surface normals + an exterior
  distance estimate alongside the colour map's smooth-count input. 3D
  colour themes activate automatically; flat themes inherit IColorMap's
  default fallback.
");
    }
}
