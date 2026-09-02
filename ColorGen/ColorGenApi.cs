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

        // F15 (#591) — orbit-accumulator inputs (trapMin / stripeAvg / tiaAvg)
        // are interpreter-only for now: they need per-iteration orbit sampling
        // that the generated C# template + HLSL palette do not provide. Reject
        // the C# export up front rather than emit code that won't compile.
        if (UsesOrbitInputs(prog))
            return new GenerateResult(sanitized, "",
                "Orbit inputs (trapMin / stripeAvg / tiaAvg) are supported by " +
                "Compile & Load (interpreter) only — not yet by Generate via " +
                "ColorGen (C# export) or the GPU palette.");

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

    // F15 — detect references to orbit-accumulator inputs (interpreter-only).
    private static bool UsesOrbitInputs(CgProgram prog)
    {
        foreach (var s in prog.Statements)
        {
            CgNode? node = s switch { CgLet l => l.Value, CgReturn r => r.Value, _ => null };
            if (node != null && ReferencesOrbit(node)) return true;
        }
        return false;
    }

    private static bool ReferencesOrbit(CgNode n) => n switch
    {
        CgVar v      => v.IsBuiltIn && CgInputs.OrbitScalars.Contains(v.Name),
        CgChannel ch => ReferencesOrbit(ch.Target),
        CgUnary u    => ReferencesOrbit(u.Operand),
        CgBinary b   => ReferencesOrbit(b.Lhs) || ReferencesOrbit(b.Rhs),
        CgTernary t  => ReferencesOrbit(t.Cond) || ReferencesOrbit(t.IfTrue) || ReferencesOrbit(t.IfFalse),
        CgCall c     => System.Linq.Enumerable.Any(c.Args, ReferencesOrbit),
        _            => false,
    };

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
