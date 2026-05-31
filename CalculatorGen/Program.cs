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

using System.Reflection;
using System.Text;
using FracturingFog.CalculatorGen.Emitters;
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

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--equation":  case "-e": equation = args[++i]; break;
                case "--name":      case "-n": name     = args[++i]; break;
                case "--out":       case "-o": outDir   = args[++i]; break;
                case "--selftest":              selfTest = true; break;
                case "--dry-run":               dryRun   = true; break;
                case "--help": case "-h":      PrintHelp(); return 0;
                default:
                    Console.Error.WriteLine($"Unknown arg: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        if (equation is null || name is null)
        {
            Console.Error.WriteLine("Both --equation and --name are required.");
            PrintHelp();
            return 2;
        }

        if (!name.EndsWith("Calculator", StringComparison.Ordinal))
            name += "Calculator";

        AstNode root;
        try
        {
            root = EquationParser.Parse(equation);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Parse error: {ex.Message}");
            return 1;
        }

        // Derive symbolic ∂p/∂z, ∂p/∂c and the dz/dc update rule.
        var dpdz   = AstDifferentiator.DpDz(root);
        var dpdc   = AstDifferentiator.DpDc(root);
        var derivUpdate = AstDifferentiator.BuildDerivativeUpdate(root);

        // Emit bodies. Two emitters per target (Avx2Emitter is stateful —
        // accumulates SSA temps per body — so we need separate instances).
        string scalarZBody = new ScalarEmitter().EmitNewValueBody(root,        "z", indent: "            ");
        string scalarDBody = new ScalarEmitter().EmitNewValueBody(derivUpdate, "d", indent: "            ");
        string avxZBody    = new Avx2Emitter("                ", tempPrefix: "z_").EmitNewValueBody(root,        "z");
        string avxDBody    = new Avx2Emitter("                ", tempPrefix: "d_").EmitNewValueBody(derivUpdate, "d");

        // Tier 4 perturbation. Symbolic Taylor expansion of p(Z+δ, C+ε) − p(Z, C).
        var perturbDelta   = AstPerturbation.BuildDeltaUpdate(root);
        string perturbBody = new PerturbationEmitter()
            .EmitDeltaBody(perturbDelta, indent: "                    ");

        // Deep-zoom DD perturbation. Same Taylor body, emitted with DD-typed
        // bindings so per-pixel δ keeps ~31 digits at zoom levels where a
        // double-precision δ would have its ε contribution absorbed by Z.
        string perturbDdBody = new DdEmitter()
            .EmitDdDeltaBody(perturbDelta, indent: "                    ");

        // dz/dc derivative update inside the perturbation loop. The
        // derivative carries the per-pixel signal that the smooth-count
        // formula loses at deep zoom (where log2(log2(|z|)) round-trips
        // through float and collapses to a single value per iteration
        // band) — distance estimate and surface normals depend on it.
        string perturbDerivBody = new PerturbDerivEmitter()
            .EmitDerivBody(derivUpdate, indent: "                    ");

        // Tier 5 BLA coefficient bodies. A = ∂p/∂z(Z, C), B = ∂p/∂c(Z, C),
        // both emitted as scalar real+imag pairs operating on the reference
        // orbit's zr/zi (the template binds Z to those names).
        string blaABody = new ScalarEmitter().EmitNewValueBody(dpdz, "A", indent: "                    ");
        string blaBBody = new ScalarEmitter().EmitNewValueBody(dpdc, "B", indent: "                    ");

        // Tier 3 / Big+ — QD reference orbit. Same polynomial step but in
        // QuadDouble arithmetic (~62 decimal digits) so the reference orbit
        // remains accurate at zoom levels past 1e25.
        string qdZBody = new QdEmitter().EmitQdBody(root, indent: "                ");

        // HP-direct iteration bodies. Used by the per-pixel fallback when
        // the perturbation reference orbit escapes — every pixel iterates
        // z = p(z, c) directly in DD or QD precision. Mirrors what the
        // legacy MandelbrotCalculator does in ComputePixelHP / ComputePixelQD.
        string ddDirectBody = new DdDirectEmitter()
            .EmitDdDirectBody(root, indent: "                    ");
        string qdDirectBody = new QdDirectEmitter()
            .EmitQdDirectBody(root, indent: "                    ");

        string template = LoadTemplate("Calculator.template.cs");
        string rendered = template
            .Replace("{{CLASS_NAME}}",      name)
            .Replace("{{EQUATION_SOURCE}}", equation)
            .Replace("{{DPDZ_TEXT}}",       AstPrinter.Print(dpdz))
            .Replace("{{DPDC_TEXT}}",       AstPrinter.Print(dpdc))
            .Replace("{{DERIV_TEXT}}",      AstPrinter.Print(derivUpdate))
            .Replace("{{TIMESTAMP}}",       DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"))
            .Replace("{{SCALAR_Z_BODY}}",   scalarZBody)
            .Replace("{{SCALAR_D_BODY}}",   scalarDBody)
            .Replace("{{AVX2_Z_BODY}}",     avxZBody)
            .Replace("{{AVX2_D_BODY}}",     avxDBody)
            .Replace("{{PERTURB_DELTA_BODY}}", perturbBody)
            .Replace("{{PERTURB_DELTA_DD_BODY}}", perturbDdBody)
            .Replace("{{PERTURB_DERIV_BODY}}", perturbDerivBody)
            .Replace("{{PERTURB_DELTA_TEXT}}", AstPrinter.Print(perturbDelta))
            .Replace("{{BLA_A_BODY}}", blaABody)
            .Replace("{{BLA_B_BODY}}", blaBBody)
            .Replace("{{QD_Z_BODY}}", qdZBody)
            .Replace("{{DD_DIRECT_BODY}}", ddDirectBody)
            .Replace("{{QD_DIRECT_BODY}}", qdDirectBody);

        string selfTestRendered = string.Empty;
        if (selfTest)
        {
            string stTmpl = LoadTemplate("SelfTest.template.cs");
            selfTestRendered = stTmpl.Replace("{{CLASS_NAME}}", name);
        }

        if (dryRun)
        {
            Console.Out.Write(rendered);
            if (selfTest)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("// ───── self-test ─────");
                Console.Out.Write(selfTestRendered);
            }
            return 0;
        }

        Directory.CreateDirectory(outDir);
        string calcPath = Path.Combine(outDir, $"{name}.cs");
        File.WriteAllText(calcPath, rendered, new UTF8Encoding(false));
        Console.Out.WriteLine($"Wrote {calcPath}");

        if (selfTest)
        {
            string stPath = Path.Combine(outDir, $"{name}SelfTest.cs");
            File.WriteAllText(stPath, selfTestRendered, new UTF8Encoding(false));
            Console.Out.WriteLine($"Wrote {stPath}");
        }

        return 0;
    }

    private static string LoadTemplate(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resName = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Embedded template missing: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine(
@"CalculatorGen v0.2
Usage:
  CalculatorGen --equation ""z*z + c"" --name MandelbrotZ2 [--out DIR] [--selftest] [--dry-run]

Equation grammar
  Tokens:    z   c   real-literal   + - *   ^Int   ( )
  Notes:     '^' takes an integer exponent (0..16). Division and
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
