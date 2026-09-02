// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ColorGenApi.cs
//
// Library-facing entry into ColorGen. Mirrors CalculatorGenApi: take a
// DSL source string, render the IColorMap subclass body via the embedded
// template, return the generated C# source so callers can write, compile,
// hash, or preview at their discretion. Parse failures surface as
// GenerateResult.Error with empty Source.

using System;
using System.Globalization;
using System.Reflection;
using FracturingFog.ColorGen.Emitters;
using FracturingFog.ColorGen.Parser;

namespace FracturingFog.ColorGen;

public readonly record struct GenerateResult(
    string ClassName,
    string Source,
    string? Error)
{
    public bool Ok => Error is null;
}

public sealed class GenerateOptions
{
    public string ThemeName { get; init; } = "My ColorGen Theme";
    public string Category { get; init; } = "User";
    public string Description { get; init; } = "";
}

public static class ColorGenApi
{
    /// <summary>
    /// Render a colour-theme C# class from a DSL source. Class name gets a
    /// "Theme" suffix when absent. Parse error → result.Error non-null.
    /// </summary>
    public static GenerateResult Generate(string source, string className, GenerateOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new GenerateResult(className ?? "", "", "Source is empty.");
        if (string.IsNullOrWhiteSpace(className))
            return new GenerateResult("", "", "Class name is empty.");

        string sanitized = SanitizeClassName(className);
        if (!sanitized.EndsWith("Theme", StringComparison.Ordinal))
            sanitized += "Theme";

        CgProgram prog;
        try
        {
            prog = ColorGenParser.Parse(source);
        }
        catch (Exception ex)
        {
            return new GenerateResult(sanitized, "", $"Parse error: {ex.Message}");
        }

        // F15 (#591) — a program referencing orbit-accumulator inputs (trapMin /
        // stripeAvg / curvature / …) needs per-iteration orbit sampling. It gets
        // its own template that implements IOrbitAwareColorMap (InitOrbit / Sample
        // / MapWithOrbit) and advertises no GPU palette (the escape-only HLSL path
        // can't compute these — CPU render). Only the referenced accumulators are
        // sampled (const-bool gates baked from the program).
        var orbitInputs = CollectOrbitInputs(prog);
        if (orbitInputs.Count > 0)
            return GenerateOrbit(prog, sanitized, source, orbitInputs, options);

        string body = new ColorGenEmitter(indent: "        ").EmitBody(prog);

        // T3.1 phase 2: also emit HLSL body + prelude so the generated theme
        // implements IGpuHlslPalette. Verbatim string literal escape — only
        // double-quote chars need doubling; the emitter doesn't produce any
        // (no string-typed DSL constructs), so a plain @"..." wrap is safe.
        var hlslEmit = new ColorGenHlslEmitter(indent: "    ");
        string hlslBody = hlslEmit.EmitBody(prog);
        string hlslPrelude = ColorGenHlslPrelude.Build(hlslEmit.PaletteArities);
        string hlslHash = ShortHash(hlslBody + "\0" + hlslPrelude);

        var opts = options ?? new GenerateOptions();
        string template = LoadTemplate("ColorMap.template.cs");
        string rendered = template
            .Replace("{{CLASS_NAME}}",  sanitized)
            .Replace("{{THEME_NAME}}",  EscapeQuotes(opts.ThemeName))
            .Replace("{{CATEGORY}}",    EscapeQuotes(opts.Category))
            .Replace("{{DESCRIPTION}}", EscapeQuotes(opts.Description))
            .Replace("{{SOURCE_COMMENT}}", CommentBlock(source))
            .Replace("{{BODY}}", body)
            .Replace("{{HLSL_BODY}}",    EscapeVerbatim(hlslBody))
            .Replace("{{HLSL_PRELUDE}}", EscapeVerbatim(hlslPrelude))
            .Replace("{{HLSL_HASH}}",    hlslHash)
            .Replace("{{TIMESTAMP}}",   DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture));

        return new GenerateResult(sanitized, rendered, null);
    }

    // F15 — render the orbit-aware template. Bakes const-bool gates for the
    // referenced accumulators and emits the DSL body into both MapWithOrbit
    // (orbit inputs bound from the accumulator) and the escape-final Map
    // overload (orbit inputs 0 — recolour/standalone paths lack an orbit).
    private static GenerateResult GenerateOrbit(
        CgProgram prog, string sanitized, string source,
        System.Collections.Generic.ISet<string> orbitInputs, GenerateOptions? options)
    {
        // Both MapWithOrbit and the escape-final Map are method bodies at the
        // same nesting (8-space indent), so one emit serves both insertion points.
        string body = new ColorGenEmitter(indent: "        ").EmitBody(prog);

        static string B(bool v) => v ? "true" : "false";

        var opts = options ?? new GenerateOptions();
        string template = LoadTemplate("ColorMapOrbit.template.cs");
        string rendered = template
            .Replace("{{CLASS_NAME}}",  sanitized)
            .Replace("{{THEME_NAME}}",  EscapeQuotes(opts.ThemeName))
            .Replace("{{CATEGORY}}",    EscapeQuotes(opts.Category))
            .Replace("{{DESCRIPTION}}", EscapeQuotes(opts.Description))
            .Replace("{{SOURCE_COMMENT}}", CommentBlock(source))
            .Replace("{{F_TRAPMIN}}",       B(orbitInputs.Contains("trapMin")))
            .Replace("{{F_TRAPCROSS}}",     B(orbitInputs.Contains("trapCross")))
            .Replace("{{F_TRAPRING}}",      B(orbitInputs.Contains("trapRing")))
            .Replace("{{F_TRAPHYPERBOLA}}", B(orbitInputs.Contains("trapHyperbola")))
            .Replace("{{F_TRAPHEXAGON}}",   B(orbitInputs.Contains("trapHexagon")))
            .Replace("{{F_STRIPE}}",        B(orbitInputs.Contains("stripeAvg")))
            .Replace("{{F_TIA}}",           B(orbitInputs.Contains("tiaAvg")))
            .Replace("{{F_CURVATURE}}",     B(orbitInputs.Contains("curvature")))
            .Replace("{{F_LYAPUNOV}}",      B(orbitInputs.Contains("lyapunov")))
            .Replace("{{F_GAUSSIAN}}",      B(orbitInputs.Contains("gaussian")))
            .Replace("{{F_EXP}}",           B(orbitInputs.Contains("expSmooth")))
            .Replace("{{BODY}}", body)
            .Replace("{{TIMESTAMP}}",   DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture));

        return new GenerateResult(sanitized, rendered, null);
    }

    // F15 — the set of orbit-accumulator inputs the program references (empty ⇒
    // not orbit-aware). Kept in lockstep with InterpretedColorMap.CollectOrbitInputs.
    private static System.Collections.Generic.HashSet<string> CollectOrbitInputs(CgProgram prog)
    {
        var found = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var s in prog.Statements)
        {
            CgNode? node = s switch { CgLet l => l.Value, CgReturn r => r.Value, _ => null };
            if (node != null) CollectOrbit(node, found);
        }
        return found;
    }

    private static void CollectOrbit(CgNode n, System.Collections.Generic.HashSet<string> found)
    {
        switch (n)
        {
            case CgVar v: if (v.IsBuiltIn && CgInputs.OrbitScalars.Contains(v.Name)) found.Add(v.Name); break;
            case CgChannel ch: CollectOrbit(ch.Target, found); break;
            case CgUnary u: CollectOrbit(u.Operand, found); break;
            case CgBinary b: CollectOrbit(b.Lhs, found); CollectOrbit(b.Rhs, found); break;
            case CgTernary t: CollectOrbit(t.Cond, found); CollectOrbit(t.IfTrue, found); CollectOrbit(t.IfFalse, found); break;
            case CgCall c: foreach (var a in c.Args) CollectOrbit(a, found); break;
        }
    }

    private static string LoadTemplate(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resName = System.Linq.Enumerable.First(asm.GetManifestResourceNames(),
            n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Embedded template missing: {fileName}");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string SanitizeClassName(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool upper = true;
        foreach (char c in s ?? "")
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(upper ? char.ToUpperInvariant(c) : c); upper = false; }
            else upper = true;
        }
        if (sb.Length == 0) sb.Append("MyColorGen");
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string EscapeQuotes(string s)
        => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");

    /// <summary>Verbatim-string-literal escape: inside @"…", only `"`
    /// needs doubling. The HLSL emitter doesn't produce `"` chars (no DSL
    /// construct emits string literals) so this is usually a no-op, but
    /// stay defensive — future intrinsic could embed one.</summary>
    private static string EscapeVerbatim(string s)
        => (s ?? "").Replace("\"", "\"\"");

    /// <summary>10-char base16 hash — enough to disambiguate ~10^12 themes
    /// for the kernel cache key.</summary>
    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
        var sb = new System.Text.StringBuilder(10);
        for (int i = 0; i < 5; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    private static string CommentBlock(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in (s ?? "").Replace("\r", "").Split('\n'))
        {
            sb.Append("//   ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
