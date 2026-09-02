// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DXC HLSL->SPIR-V compilation via the DXC command-line tool.
//
// O2 (DXC delivery) spike answer: this uses the *runtime CLI JIT* model —
// invoke `dxc -spirv` at runtime, mirroring how the D3D path runtime-compiles
// HLSL. Themes are dynamic, so a build-time SPIR-V bake cannot cover per-theme
// EvalPalette variants; runtime compile is the representative path. `dxc` is
// located via DXC_PATH -> VULKAN_SDK/Bin -> PATH so CI can point at whichever
// DXC the leg ships (Vulkan SDK on the LunarG image, or an apt/NuGet DXC).

using System;
using System.Diagnostics;
using System.IO;

namespace FracturingFog.Rendering.Vulkan;

public static class DxcCompiler
{
    // Compile an HLSL source string to a SPIR-V module (raw bytes).
    // extraArgs passes through DXC flags verbatim (e.g. -fvk-*-shift binding
    // maps for the real kernel port). Kept before the -Fo/input so DXC parses
    // them as options.
    // Short filename prefix for cached SPIR-V blobs on disk (see ShaderBlobCache).
    private const string CacheKind = "spv";

    public static byte[] CompileToSpirv(string hlsl, string entry, string profile, params string[] extraArgs)
    {
        // #456 — SPIR-V bytes are portable across runs on the same machine, so
        // reuse a machine-cached blob keyed by the exact source + entry + profile
        // + flags instead of shelling out to DXC on every launch. A pure
        // accelerator: any miss falls through to the runtime compile below.
        string cacheKey = FracturingFog.Abstractions.ShaderBlobCache.ComputeKey(
            $"dxc-spirv-{profile}", hlsl, entry, profile, extraArgs);
        byte[]? cached = FracturingFog.Abstractions.ShaderBlobCache.TryLoad(CacheKind, cacheKey);
        if (cached != null)
        {
            if (LooksLikeSpirv(cached)) return cached;
            // Corrupt/truncated blob — drop it so vkCreateShaderModule never sees
            // it, then recompile below.
            FracturingFog.Abstractions.ShaderBlobCache.Invalidate(CacheKind, cacheKey);
        }

        string dxc = LocateDxc();
        string stem = Path.Combine(Path.GetTempPath(), "ffvk-" + Guid.NewGuid().ToString("N"));
        string inFile = stem + ".hlsl";
        string outFile = stem + ".spv";
        File.WriteAllText(inFile, hlsl);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dxc,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-spirv");
            psi.ArgumentList.Add("-T"); psi.ArgumentList.Add(profile);   // e.g. cs_6_0
            psi.ArgumentList.Add("-E"); psi.ArgumentList.Add(entry);     // e.g. main
            psi.ArgumentList.Add("-fspv-target-env=vulkan1.1");
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-Fo"); psi.ArgumentList.Add(outFile);
            psi.ArgumentList.Add(inFile);

            Process p;
            try { p = Process.Start(psi)!; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"could not launch dxc ('{dxc}'). Set DXC_PATH or install the " +
                    $"Vulkan SDK / DXC on PATH. Inner: {ex.Message}", ex);
            }

            using (p)
            {
                string stderr = p.StandardError.ReadToEnd();
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(outFile))
                    throw new InvalidOperationException(
                        $"dxc failed (exit {p.ExitCode}) using '{dxc}':\n{stderr}{stdout}");
                byte[] spirv = File.ReadAllBytes(outFile);
                FracturingFog.Abstractions.ShaderBlobCache.Store(CacheKind, cacheKey, spirv);   // #456
                return spirv;
            }
        }
        finally
        {
            TryDelete(inFile);
            TryDelete(outFile);
        }
    }

    // Resolve the dxc executable: DXC_PATH override, then VULKAN_SDK/Bin, then
    // bare name for OS PATH resolution.
    private static string LocateDxc()
    {
        var env = Environment.GetEnvironmentVariable("DXC_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string exe = OperatingSystem.IsWindows() ? "dxc.exe" : "dxc";
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrEmpty(sdk))
        {
            foreach (var sub in new[] { "Bin", "bin" })
            {
                var candidate = Path.Combine(sdk, sub, exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return exe; // fall through to PATH
    }

    // Cheap validity gate for a cached SPIR-V blob: a whole number of 32-bit
    // words led by the SPIR-V magic (0x07230203, either endianness). Guards the
    // #456 disk cache against a truncated/garbage entry reaching vkCreateShaderModule.
    private static bool LooksLikeSpirv(byte[] blob)
    {
        if (blob.Length < 20 || (blob.Length & 3) != 0) return false;
        uint w0 = (uint)(blob[0] | (blob[1] << 8) | (blob[2] << 16) | (blob[3] << 24));
        return w0 == 0x07230203u || w0 == 0x03022307u;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
