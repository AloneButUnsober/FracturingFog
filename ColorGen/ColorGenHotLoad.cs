// ColorGenHotLoad.cs
//
// Roslyn-compile a freshly-generated colour theme into an in-memory
// assembly and return the IColorMap type. Lets the ColorGenEditor produce
// a working theme without writing a file + rebuilding the app — the
// generated type becomes available immediately.
//
// References are gathered from the running AppDomain (same approach as
// CalculatorGenHotLoad) so the generated code sees the host's IColorMap
// + ColorPaletteType + INamedColorMap types.
//
// Each successful compile drops into a collectible AssemblyLoadContext.
// The previous context is unloaded on the next compile so iterative edits
// don't accrete assemblies; the cache holds the most-recent Type per
// (source|name) tuple for instant re-load on repeat hits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FracturingFog.ColorGen;

public readonly record struct ColorGenHotLoadResult(
    Type? ColorMapType,
    string? GeneratedSource,
    string? Error)
{
    public bool Ok => ColorMapType != null;
}

public static class ColorGenHotLoad
{
    private static AssemblyLoadContext? _lastContext;
    private static readonly Dictionary<string, Type> _cache = new();
    private static string CacheKey(string src, string name) => $"{src}::{name}";

    /// <summary>
    /// Generate, Roslyn-compile, and load a colour theme. Returns the
    /// resulting <see cref="Type"/> on success; activate it via
    /// <c>Activator.CreateInstance</c> (parameterless ctor).
    /// </summary>
    public static ColorGenHotLoadResult TryCompileAndLoad(string source, string className, GenerateOptions? options = null)
    {
        var gen = ColorGenApi.Generate(source, className, options);
        if (!gen.Ok) return new ColorGenHotLoadResult(null, null, gen.Error);

        string key = CacheKey(source, gen.ClassName);
        if (_cache.TryGetValue(key, out var cached))
            return new ColorGenHotLoadResult(cached, gen.Source, null);

        var syntaxTree = CSharpSyntaxTree.ParseText(gen.Source);
        var references = GatherReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName: $"GeneratedColorTheme_{gen.ClassName}_{Environment.TickCount}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Compile failed:");
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine($"  {d.Id} {d.GetMessage()} @ {d.Location.GetLineSpan().StartLinePosition}");
            return new ColorGenHotLoadResult(null, gen.Source, sb.ToString());
        }

        ms.Seek(0, SeekOrigin.Begin);
        var ctx = new AssemblyLoadContext($"GenColorTheme_{gen.ClassName}_{Environment.TickCount}", isCollectible: true);
        var asm = ctx.LoadFromStream(ms);
        if (_lastContext != null)
        {
            var stale = _lastContext;
            var staleKeys = new List<string>();
            foreach (var kv in _cache)
                if (kv.Value.Assembly.GetName().Name?.StartsWith(stale.Name ?? "") ?? false)
                    staleKeys.Add(kv.Key);
            foreach (var k in staleKeys) _cache.Remove(k);
            stale.Unload();
        }
        _lastContext = ctx;

        var fullName = $"FracturingFog.Models.Generated.{gen.ClassName}";
        var type = asm.GetType(fullName);
        if (type == null)
            return new ColorGenHotLoadResult(null, gen.Source,
                $"Compile succeeded but type '{fullName}' not found in generated assembly.");

        _cache[key] = type;
        return new ColorGenHotLoadResult(type, gen.Source, null);
    }

    /// <summary>Unload the most-recently-loaded compile context. Safe only
    /// after the host has dropped every reference to types from it.</summary>
    public static void UnloadPreviousContext()
    {
        _lastContext?.Unload();
        _lastContext = null;
    }

    private static List<MetadataReference> GatherReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // S-X7.3 (2026-06-23) — TPA fallback for single-file publish. See the
        // matching comment in CalculatorGenHotLoad.GatherReferences for the
        // full rationale; in short, single-file builds leave Assembly.Location
        // empty, so the loop below produces no MetadataReferences and Roslyn
        // fails with CS0518 (Predefined types not defined). TRUSTED_PLATFORM_ASSEMBLIES
        // carries the extracted on-disk paths every Roslyn compile needs.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var path in tpa.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!seen.Add(path)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* skip unreadable */ }
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            string loc;
            try { loc = asm.Location; } catch { continue; }
            if (string.IsNullOrEmpty(loc)) continue;
            if (!seen.Add(loc)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(loc)); }
            catch { /* best-effort */ }
        }
        return refs;
    }
}
