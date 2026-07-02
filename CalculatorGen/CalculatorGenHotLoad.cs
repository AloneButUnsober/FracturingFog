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
using System.Runtime.InteropServices;
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
    // Wave 2.9 — morph runner needs to compile a fresh calculator per frame
    // for an extended sweep (~60 calls). Default behaviour unloads the
    // previous context on every TryCompileAndLoad which races with the
    // render host: between the unload call and the caller's
    // SetDynamicAltCalculator(new) install, the host still references the
    // calc instance from the about-to-die context. A background
    // AnimationTick on the render thread that fires during that window
    // dereferences metadata mid-unload and crashes the process. While
    // KeepContexts is true, all compiled contexts are retained until it
    // flips back to false; FlushKeptContexts then unloads them in one
    // batch when the caller can guarantee no live references remain.
    public static bool KeepContexts { get; set; }
    private static readonly List<AssemblyLoadContext> _keptContexts = new();
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
        // Skipped while KeepContexts is true (Wave 2.9 morph) — the
        // caller will flush via FlushKeptContexts when safe.
        if (KeepContexts)
        {
            if (_lastContext != null) _keptContexts.Add(_lastContext);
        }
        else if (_lastContext != null)
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

    /// <summary>Unload all contexts retained while <see cref="KeepContexts"/>
    /// was true. Caller must have dropped every live reference to types from
    /// those contexts before calling — for the morph runner that means the
    /// render host's dynamic alt slot has been cleared. Best-effort: an
    /// individual unload that throws is logged and skipped so the rest of
    /// the queue still flushes.</summary>
    public static void FlushKeptContexts()
    {
        foreach (var stale in _keptContexts)
        {
            try
            {
                var staleKeys = new System.Collections.Generic.List<string>();
                foreach (var kv in _cache)
                    if (kv.Value.Assembly.GetName().Name?.StartsWith(stale.Name ?? "") ?? false)
                        staleKeys.Add(kv.Key);
                foreach (var k in staleKeys) _cache.Remove(k);
                stale.Unload();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"FlushKeptContexts: unload failed: {ex.Message}");
            }
        }
        _keptContexts.Clear();
    }

    // ── Phase D-6 / item 26 — permanent persist + auto-reload (Wave 2.3) ────
    //
    // "Save hot-loaded calc to permanent .cs" — generates the calculator
    // source, writes it under %LOCALAPPDATA%/FracturingFog/UserCalculators/,
    // and (optionally) hot-loads the result. On next launch the host scans
    // the dir and re-loads every .cs it finds — no rebuild required.
    //
    // Layout:
    //   <PersistDir>/<ClassName>.cs        — generated calculator source
    //   <PersistDir>/<ClassName>.meta.txt  — the source equation string for
    //                                        round-trip into the editor
    public static string PersistDir { get; set; } = DefaultPersistDir();

    public static string DefaultPersistDir()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "FracturingFog", "UserCalculators");
    }

    public readonly record struct PersistResult(
        Type? CalculatorType,
        string? SourcePath,
        string? Equation,
        string? ClassName,
        string? Error)
    {
        public bool Ok => CalculatorType != null && SourcePath != null;
    }

    /// <summary>
    /// Generate source for <paramref name="equation"/>, write it to
    /// <see cref="PersistDir"/>, then Roslyn-compile + load. Caller gets the
    /// compiled type and the on-disk path so the editor can show "saved to …"
    /// in the status bar. Idempotent on the source path — re-saving the same
    /// name overwrites the previous .cs.
    /// </summary>
    public static PersistResult PersistAndLoad(string equation, string name)
    {
        var gen = CalculatorGenApi.Generate(equation, name, includeSelfTest: false);
        if (!gen.Ok)
            return new PersistResult(null, null, equation, null, gen.Error);

        string dir = PersistDir;
        string srcPath;
        string metaPath;
        try
        {
            Directory.CreateDirectory(dir);
            srcPath = Path.Combine(dir, gen.ClassName + ".cs");
            metaPath = Path.Combine(dir, gen.ClassName + ".meta.txt");
            File.WriteAllText(srcPath, gen.Source, new UTF8Encoding(false));
            File.WriteAllText(metaPath, equation, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return new PersistResult(null, null, equation, gen.ClassName,
                $"Persist write failed: {ex.Message}");
        }

        var load = TryCompileAndLoad(equation, name);
        if (!load.Ok)
            return new PersistResult(null, srcPath, equation, gen.ClassName, load.Error);
        return new PersistResult(load.CalculatorType, srcPath, equation, gen.ClassName, null);
    }

    public readonly record struct PersistedEntry(
        string SourcePath,
        string ClassName,
        string Equation,
        Type? CalculatorType,
        string? Error);

    /// <summary>
    /// Enumerate every .cs file under <see cref="PersistDir"/>, attempt to
    /// hot-load each, and return one entry per file (with the error string
    /// populated for entries that failed to compile). Called once at host
    /// startup so persisted calculators are available without a rebuild.
    /// </summary>
    public static List<PersistedEntry> LoadAllPersisted()
    {
        var result = new List<PersistedEntry>();
        string dir = PersistDir;
        if (!Directory.Exists(dir)) return result;

        foreach (string srcPath in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string className = Path.GetFileNameWithoutExtension(srcPath);
            string equation = string.Empty;
            string metaPath = Path.Combine(dir, className + ".meta.txt");
            if (File.Exists(metaPath))
            {
                try { equation = File.ReadAllText(metaPath).Trim(); } catch { }
            }

            if (string.IsNullOrEmpty(equation))
            {
                result.Add(new PersistedEntry(srcPath, className, "", null,
                    "No matching .meta.txt — cannot reconstruct source equation."));
                continue;
            }

            string baseName = className.EndsWith("Calculator", StringComparison.Ordinal)
                ? className.Substring(0, className.Length - "Calculator".Length)
                : className;
            var load = TryCompileAndLoad(equation, baseName);
            result.Add(new PersistedEntry(srcPath, className, equation,
                load.CalculatorType, load.Error));
        }
        return result;
    }

    private static List<MetadataReference> GatherReferences()
    {
        // Force-load runtime dependencies the generated calculator imports
        // unconditionally. Without this, ILGPU is missing from AppDomain
        // until the host actually exercises the GPU path — which means
        // Roslyn compile fails on the `using ILGPU;` lines with CS0246.
        // The assemblies live next to FracturingFogCLD.exe (host project
        // references the ILGPU NuGet); load by simple name and let the
        // default probing path find them.
        TryLoadByName("ILGPU");
        TryLoadByName("ILGPU.Runtime");          // some versions split the runtime
        TryLoadByName("ILGPU.Algorithms");       // optional but cheap to attempt

        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // S-X7.3 (2026-06-23) — TPA fallback for single-file publish. In a
        // single-file self-contained build, Assembly.Location returns ""
        // because the assembly was loaded from the embedded bundle, not from
        // disk. GatherReferences then handed Roslyn an empty reference list
        // and every compile failed with CS0518 (Predefined types not defined)
        // because System.Private.CoreLib never made the cut. The TRUSTED_PLATFORM_ASSEMBLIES
        // AppContext data contains the extracted on-disk paths of every BCL
        // + dependency assembly (single-file runtimes write them under
        // %TEMP%/.net/<app>/<hash>/ then load by file). Use that as the
        // primary source; the AppDomain pass below picks up dynamically-
        // loaded extras (ILGPU, source-generated calculators).
        int tpaTotal = 0, tpaAdded = 0, tpaFailed = 0;
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var path in tpa.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                tpaTotal++;
                if (string.IsNullOrEmpty(path)) continue;
                if (!seen.Add(path)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(path)); tpaAdded++; }
                catch (Exception ex)
                {
                    tpaFailed++;
                    if (s_diag) Console.Error.WriteLine($"[CalcGenHotLoad] TPA ref skip {path}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        int adAdded = 0, adSkipped = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm != null)
            {
                if (asm.IsDynamic) { adSkipped++; continue; }
                string loc = asm.Location;
                //try { loc = } catch { adSkipped++; continue; }
                if (string.IsNullOrEmpty(loc)) { adSkipped++; continue; }
                if (!seen.Add(loc)) { adSkipped++; continue; }
                try
                {
                    refs.Add(MetadataReference.CreateFromFile(loc)); adAdded++;
                }
                catch (Exception ex)
                {
                    adSkipped++;
                    if (s_diag) Console.Error.WriteLine($"[CalcGenHotLoad] location bundle skip {loc}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // S-X7.11 (2026-06-23) — single-file bundle fallback. TPA is empty
        // and Assembly.Location is "" for every loaded assembly when the
        // app is published as single-file (.NET 10 default for the cross-
        // plat App). AssemblyExtensions.TryGetRawMetadata pulls metadata
        // straight out of the in-memory bundle so Roslyn can compile
        // without a disk-resident PE. See RoslynRefs.cs for the matching
        // path in the Engine-side hot-loaders.
        int bundleAdded = 0, bundleFailed = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            string? simpleName = asm.GetName().Name;
            if (string.IsNullOrEmpty(simpleName)) continue;
            if (!seen.Add("bundle:" + simpleName)) continue;
            try
            {
                unsafe
                {
                    if (System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(asm, out byte* blob, out int length)
                        && blob != null && length > 0)
                    {
                        var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                        var assembly = AssemblyMetadata.Create(module);
                        refs.Add(assembly.GetReference());
                        bundleAdded++;
                    }
                }
            }
            catch (Exception ex)
            {
                bundleFailed++;
                if (s_diag) Console.Error.WriteLine($"[CalcGenHotLoad] bundle skip {simpleName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (s_diag)
        {
            Console.Error.WriteLine($"[CalcGenHotLoad] refs total={refs.Count} TPA(total={tpaTotal},added={tpaAdded},failed={tpaFailed}) AppDomain(added={adAdded},skipped={adSkipped}) bundle(added={bundleAdded},failed={bundleFailed})");
            Console.Error.Flush();
        }
        return refs;
    }

    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("FF_ROSLYN_DEBUG"), "1", StringComparison.Ordinal);

    private static void TryLoadByName(string simpleName)
    {
        // Already loaded? Skip — Assembly.Load throws on duplicate in
        // some runtimes, and we want this to be cheap on repeat compiles.
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return;
        try { Assembly.Load(simpleName); }
        catch { /* optional dependency — generated calc may not actually use it */ }
    }
}
