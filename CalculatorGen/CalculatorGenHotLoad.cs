// CalculatorGenHotLoad.cs
//
// Roslyn-compile a freshly-generated calculator into an in-memory
// assembly and return the IFractalCalculator type. Lets the UserEquation
// dialog produce a working calculator without writing a file + restarting
// the app — the generated type becomes available immediately.
//
// References are pulled from the running AppDomain (every assembly the
// host has already loaded), so the generated code sees the same
// FracturingFog.* / ILGPU / System.Runtime.Intrinsics types the
// disk-compiled calculators see. Compile errors are surfaced as a
// formatted message; the caller is expected to display them in the UI.
//
// Each successful call drops its assembly into a collectible
// AssemblyLoadContext. The context isn't unloaded automatically (the
// returned type still has live references); a future call to
// UnloadPreviousContext clears the LAST loaded one once the host has
// stopped referencing types from it. For interactive editing where the
// user iterates rapidly this matters — without unload the process accrues
// a tiny assembly per compile. Acceptable for now.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FracturingFog.CalculatorGen;

public readonly record struct HotLoadResult(
    Type? CalculatorType,
    string? Error)
{
    public bool Ok => CalculatorType != null;
}

public static class CalculatorGenHotLoad
{
    private static AssemblyLoadContext? _lastContext;
    // Cache: (equation|name) → previously-loaded Type. Re-typing the
    // same equation in the UserEquation editor and clicking "Compile &
    // Load" repeatedly should be free — skip Roslyn entirely when the
    // tuple matches a still-loaded assembly.
    private static readonly System.Collections.Generic.Dictionary<string, Type> _cache = new();

    private static string CacheKey(string equation, string name) => $"{equation}{name}";

    /// <summary>
    /// Generate, Roslyn-compile, and load a calculator. Returns the
    /// resulting <see cref="Type"/> on success — the caller activates it
    /// via <see cref="Activator.CreateInstance"/> with the
    /// <c>(int width, int height)</c> constructor signature shared by all
    /// generated calculators.
    /// </summary>
    public static HotLoadResult TryCompileAndLoad(string equation, string name)
    {
        var gen = CalculatorGenApi.Generate(equation, name, includeSelfTest: false);
        if (!gen.Ok)
            return new HotLoadResult(null, gen.Error);

        string key = CacheKey(equation, gen.ClassName);
        if (_cache.TryGetValue(key, out var cached))
            return new HotLoadResult(cached, null);

        // Roslyn compile.
        var syntaxTree = CSharpSyntaxTree.ParseText(gen.Source);
        var references = GatherReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName: $"GeneratedCalc_{gen.ClassName}_{Environment.TickCount}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Compile failed:");
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine($"  {d.Id} {d.GetMessage()} @ {d.Location.GetLineSpan().StartLinePosition}");
            return new HotLoadResult(null, sb.ToString());
        }

        ms.Seek(0, SeekOrigin.Begin);
        var ctx = new AssemblyLoadContext($"GenCalc_{gen.ClassName}_{Environment.TickCount}", isCollectible: true);
        var asm = ctx.LoadFromStream(ms);
        // Unload the previous context to free the prior assembly's
        // memory. The cache holds Type references for fast re-load —
        // entries pointing at unloaded contexts get invalidated here.
        if (_lastContext != null)
        {
            var stale = _lastContext;
            // Drop cache entries from the dying context so a future
            // miss recompiles cleanly.
            var staleKeys = new System.Collections.Generic.List<string>();
            foreach (var kv in _cache)
                if (kv.Value.Assembly.GetName().Name?.StartsWith(stale.Name ?? "") ?? false)
                    staleKeys.Add(kv.Key);
            foreach (var k in staleKeys) _cache.Remove(k);
            stale.Unload();
        }
        _lastContext = ctx;

        var fullName = $"FracturingFog.Calculators.Generated.{gen.ClassName}";
        var type = asm.GetType(fullName);
        if (type == null)
            return new HotLoadResult(null,
                $"Compile succeeded but type '{fullName}' not found in generated assembly.");

        _cache[key] = type;
        return new HotLoadResult(type, null);
    }

    /// <summary>Unload the most-recently-loaded compile context. Safe only
    /// after the host has dropped every reference to types from it (the
    /// active calculator, any cached buffers it produced). Idempotent.</summary>
    public static void UnloadPreviousContext()
    {
        _lastContext?.Unload();
        _lastContext = null;
    }

    private static List<MetadataReference> GatherReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            string loc;
            try { loc = asm.Location; } catch { continue; }
            if (string.IsNullOrEmpty(loc)) continue;
            if (!seen.Add(loc)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(loc)); }
            catch { /* best-effort — skip refs we can't materialise */ }
        }
        return refs;
    }
}
