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

        string body = new ColorGenEmitter(indent: "        ").EmitBody(prog);

        var opts = options ?? new GenerateOptions();
        string template = LoadTemplate("ColorMap.template.cs");
        string rendered = template
            .Replace("{{CLASS_NAME}}",  sanitized)
            .Replace("{{THEME_NAME}}",  EscapeQuotes(opts.ThemeName))
            .Replace("{{CATEGORY}}",    EscapeQuotes(opts.Category))
            .Replace("{{DESCRIPTION}}", EscapeQuotes(opts.Description))
            .Replace("{{SOURCE_COMMENT}}", CommentBlock(source))
            .Replace("{{BODY}}", body)
            .Replace("{{TIMESTAMP}}",   DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture));

        return new GenerateResult(sanitized, rendered, null);
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
