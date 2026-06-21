// Tools/VisualRegression/Program.cs
//
// Visual-regression harness for Fracturing Fog. Drives the main app's
// `--batch --mode image` path against a fixed set of fractal/region/theme
// cases, SHA256s each output PNG, and either writes a baseline manifest
// (`record`) or asserts against it (`verify`).
//
// Roadmap reference: Performance-Roadmap "Visual regression harness" and
// Lighting-FX "Bit-identity smoke test" cross-cutting items. Gating
// future GPU/SIMD/perturbation rewrites against silent pixel drift.
//
// Tool is intentionally engine-free — it shells out via `dotnet run` so
// it works regardless of which calculators are wired in the current
// FracturingFog.App configuration.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Tools.VisualRegression;

internal static class Program
{
    private const string BaselineFile = "Tools/VisualRegression/baseline.json";
    private const int DefaultWidth   = 256;
    private const int DefaultHeight  = 256;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string verb = args[0].ToLowerInvariant();
        return verb switch
        {
            "record" => Run(record: true),
            "verify" => Run(record: false),
            "list"   => PrintCases(),
            _        => HelpExit(verb),
        };
    }

    private static int HelpExit(string verb)
    {
        Console.Error.WriteLine($"Unknown verb '{verb}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Tools/VisualRegression -- record");
        Console.WriteLine("  dotnet run --project Tools/VisualRegression -- verify");
        Console.WriteLine("  dotnet run --project Tools/VisualRegression -- list");
        Console.WriteLine();
        Console.WriteLine("Baseline file: " + BaselineFile);
    }

    private static int PrintCases()
    {
        foreach (var c in DefaultCases())
            Console.WriteLine($"{c.Name,-32} {string.Join(' ', c.Args)}");
        return 0;
    }

    private static int Run(bool record)
    {
        string repoRoot = ResolveRepoRoot();
        string baselinePath = Path.Combine(repoRoot, BaselineFile);
        string workDir = Path.Combine(repoRoot, "out", "visual-regression");
        Directory.CreateDirectory(workDir);

        var cases = DefaultCases();
        var baseline = record
            ? new Baseline { Cases = new() }
            : LoadBaseline(baselinePath);

        int failed = 0;
        var results = new List<CaseRecord>(cases.Count);

        foreach (var c in cases)
        {
            string pngPath = Path.Combine(workDir, c.Name + ".png");
            File.Delete(pngPath);

            int rc = RunBatch(repoRoot, c.Args, pngPath);
            if (rc != 0 || !File.Exists(pngPath))
            {
                Console.Error.WriteLine($"FAIL  {c.Name}  batch exit={rc}, png missing");
                failed++;
                continue;
            }

            string sha = Sha256OfFile(pngPath);
            results.Add(new CaseRecord { Name = c.Name, Sha256 = sha, Args = c.Args });

            if (record)
            {
                Console.WriteLine($"REC   {c.Name}  {sha}");
                continue;
            }

            string? expected = baseline.Cases.Find(r => r.Name == c.Name)?.Sha256;
            if (expected == null)
            {
                Console.Error.WriteLine($"MISS  {c.Name}  no baseline entry");
                failed++;
            }
            else if (!string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"DIFF  {c.Name}  expected={expected[..12]}..  got={sha[..12]}..");
                failed++;
            }
            else
            {
                Console.WriteLine($"OK    {c.Name}");
            }
        }

        if (record)
        {
            baseline.Cases = results;
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(baseline, JsonOpts));
            Console.WriteLine($"Wrote {results.Count} baseline entries to {baselinePath}");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{results.Count - failed}/{cases.Count} passed.");
        return failed == 0 ? 0 : 1;
    }

    // ── Cases ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Default case matrix. One entry per fractal family at default-knob
    /// values, 256×256, HSV theme. Designed to be cheap (sub-second per case)
    /// so the full pass runs in CI inside a minute.
    /// </summary>
    private static List<Case> DefaultCases()
    {
        string[] fractals =
        {
            "Mandelbrot",
            "Julia",
            "BurningShip",
            "Tricorn",
            "Multibrot",
            "Phoenix",
            "Newton",
            "Nova",
            "MagnetOne",
            "MagnetTwo",
            "Halley",
            "Secant",
            "Glynn",
            "Spider",
            "Buddhabrot",
            "IFS",
            "LSystem",
            "StrangeAttractor",
            "Plasma",
            "Apollonian",
            "DLA",
            "Flame",
        };

        var cases = new List<Case>();
        foreach (var f in fractals)
        {
            cases.Add(new Case
            {
                Name = $"{f.ToLowerInvariant()}-default",
                Args = new[]
                {
                    "--fractal", f,
                    "--theme", "HSV",
                    "--width", DefaultWidth.ToString(),
                    "--height", DefaultHeight.ToString(),
                    "--quality", "Standard",
                },
            });
        }
        return cases;
    }

    // ── Batch shell-out ──────────────────────────────────────────────────────

    private static int RunBatch(string repoRoot, IReadOnlyList<string> caseArgs, string outPng)
    {
        // dotnet run -c Release --project FracturingFog.App -- --batch --mode image
        //     --out <pngPath> <case args ...>
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add("FracturingFog.App");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--batch");
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("image");
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPng);
        foreach (var a in caseArgs) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.WaitForExit(120_000);
        return p.HasExited ? p.ExitCode : -1;
    }

    // ── Hash ─────────────────────────────────────────────────────────────────

    private static string Sha256OfFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Repo root + baseline I/O ─────────────────────────────────────────────

    /// <summary>
    /// Walk up from the running exe until a directory containing
    /// `FracturingFogCLD.sln` (the solution root) is found.
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Cannot locate repo root (FracturingFogCLD.sln not found above " + AppContext.BaseDirectory + ").");
        return dir.FullName;
    }

    private static Baseline LoadBaseline(string path)
    {
        if (!File.Exists(path))
            return new Baseline { Cases = new() };
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Baseline>(json, JsonOpts) ?? new Baseline { Cases = new() };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Models ───────────────────────────────────────────────────────────────

    private sealed class Case
    {
        public string Name { get; set; } = "";
        public string[] Args { get; set; } = Array.Empty<string>();
    }

    private sealed class Baseline
    {
        public List<CaseRecord> Cases { get; set; } = new();
    }

    private sealed class CaseRecord
    {
        public string Name { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string[] Args { get; set; } = Array.Empty<string>();
    }
}
