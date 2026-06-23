// Engine/Calculators/RoslynRefs.cs
//
// S-X7.9 (2026-06-23) — shared MetadataReference gathering for every Roslyn
// call site that compiles user-supplied C# inside the host process.
// Consolidates the TPA-fallback pattern needed for single-file self-contained
// publish, where typeof(T).Assembly.Location returns "" because assemblies
// are loaded from the embedded bundle, not disk. Without this,
// MetadataReference.CreateFromFile("") throws ArgumentException ("value
// cannot be an empty string (Parameter 'path')") and Roslyn never sees the
// BCL types, surfacing as CS0518 + CS0246 errors in the UserEquation / DSL /
// UserBulb / Sandbox compile flows.
//
// Sources resolved by GatherRefs(params Assembly[]):
//   1. Each marker assembly's Location, falling back to the matching
//      TRUSTED_PLATFORM_ASSEMBLIES entry by simple name when Location == "".
//   2. System.Runtime.dll + netstandard.dll from TPA (always pulled — many
//      BCL primitive types forward through these).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Microsoft.CodeAnalysis;

namespace FracturingFog.Calculators;

internal static class RoslynRefs
{
    public static MetadataReference[] GatherRefs(params Assembly[] markers)
    {
        var tpaByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var path in tpa.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!tpaByName.ContainsKey(name)) tpaByName[name] = path;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        void AddPath(string? p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!seen.Add(p)) return;
            try { refs.Add(MetadataReference.CreateFromFile(p)); }
            catch { /* skip unreadable */ }
        }

        foreach (var asm in markers)
        {
            string? loc = null;
            try { loc = asm.Location; } catch { }
            if (string.IsNullOrEmpty(loc))
                tpaByName.TryGetValue(asm.GetName().Name ?? string.Empty, out loc);
            AddPath(loc);
        }

        // BCL forward-shims: always include.
        if (tpaByName.TryGetValue("System.Runtime", out string? sysRt)) AddPath(sysRt);
        if (tpaByName.TryGetValue("netstandard", out string? netStd)) AddPath(netStd);
        if (tpaByName.TryGetValue("System.Private.CoreLib", out string? coreLib)) AddPath(coreLib);

        return refs.ToArray();
    }
}
