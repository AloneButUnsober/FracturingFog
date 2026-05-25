// UserBulbIlgpuTranslator.cs
//
// Validates a user step source for GPU compatibility, then emits a string
// that the ILGPU runtime can compile alongside its kernel. Restricted
// grammar (no closures, no heap allocation other than Vec3, no loops with
// dynamic bounds, no exception flow).
//
// Used by UserBulbGpuCalculator. Validation failure → caller falls back to
// CPU path.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FracturingFog.Calculators;

public sealed record IlgpuTranslateResult(bool Ok, string? Error, string? Body);

public static class UserBulbIlgpuTranslator
{
    private static readonly HashSet<string> AllowedTypes = new()
    {
        "Vec3", "double", "float", "int", "bool",
    };

    private static readonly HashSet<string> AllowedCallPrefixes = new()
    {
        "Math.", "Vec3.",
    };

    public static IlgpuTranslateResult Translate(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new(false, "Empty source.", null);

        string s = StripComments(source);

        // Reject `new` of anything other than Vec3.
        foreach (Match m in Regex.Matches(s, @"\bnew\s+([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string type = m.Groups[1].Value;
            if (!AllowedTypes.Contains(type))
                return new(false, $"GPU: 'new {type}' not allowed. Only Vec3.", null);
        }

        // Reject loops + try/catch + throw + lambdas + delegates.
        if (Regex.IsMatch(s, @"\b(for|while|foreach|do|try|catch|throw|delegate)\b"))
            return new(false, "GPU: control flow loops and exceptions not supported.", null);
        if (Regex.IsMatch(s, @"=>"))
            return new(false, "GPU: lambdas not supported.", null);

        // Reject string + heap-y types.
        if (Regex.IsMatch(s, @"\b(string|List|Array|Dictionary|HashSet|new\s*\[)\b"))
            return new(false, "GPU: collections and arrays not supported.", null);

        return new(true, null, source);
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//.*?$", "", RegexOptions.Multiline);
        return src;
    }
}
