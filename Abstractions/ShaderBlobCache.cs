// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ShaderBlobCache.cs — #456.
//
// Persist compiled GPU shader blobs to disk so the one-time first-launch
// FXC/DXC compile is paid only once per machine, not once per process. The
// D3D backend caches FXC bytecode (ID3DBlob bytes); the Vulkan backend caches
// DXC-produced SPIR-V bytes. Both are just byte[] keyed by a hash of the exact
// compile inputs, so this class is backend-agnostic — it never sees D3D or
// Vulkan types.
//
// Correctness contract (issue #456):
//   • The key is a SHA-256 of the EXACT HLSL source string + entry point +
//     profile + compiler flags + a format-version tag. Any change to any of
//     those ⇒ a different key ⇒ a fresh compile. A stale blob can never be
//     loaded for changed source. We NEVER key on file mtime.
//   • The cache is a pure accelerator. On any miss / corruption / read error
//     the caller compiles from source (today's path). Correctness never
//     depends on a cache hit.
//   • Poisoned-blob guard: if the driver rejects a cached blob
//     (CreateComputeShader / vkCreateShaderModule fails), the caller calls
//     Invalidate(kind, key) to delete it, then recompiles.
//
// Opt-out: set FF_NO_SHADER_CACHE=1 to disable both load and store (falls back
// to always-compile). Useful for A/B timing and for driver-bug bisection.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FracturingFog.Abstractions
{
    /// <summary>Backend-agnostic on-disk cache of compiled shader blobs
    /// (FXC bytecode or SPIR-V). See the file header for the correctness
    /// contract. All operations are best-effort: any I/O failure degrades to a
    /// cache miss so the caller compiles from source.</summary>
    public static class ShaderBlobCache
    {
        // Bump when the on-disk blob format or the key composition changes in a
        // way that must invalidate every existing entry. Folded into every key.
        private const int FormatVersion = 1;

        private const string CacheDirName = "ShaderCache";

        /// <summary>When true, both <see cref="TryLoad"/> and <see cref="Store"/>
        /// are no-ops, so every compile goes to the source path. Defaults to the
        /// FF_NO_SHADER_CACHE env var; settable for tests.</summary>
        public static bool Disabled { get; set; } =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FF_NO_SHADER_CACHE"));

        /// <summary>Cache directory under the app data root
        /// (<see cref="AppDataPaths.Root"/>/ShaderCache), NOT the install dir.</summary>
        public static string CacheDir => Path.Combine(AppDataPaths.Root, CacheDirName);

        /// <summary>Compute a stable cache key (uppercase hex SHA-256) from the
        /// exact compile inputs. <paramref name="formatTag"/> identifies the
        /// output format/compiler (e.g. "fxc-cs_5_0" / "dxc-spirv-cs_6_0");
        /// <paramref name="flags"/> are the compiler flags that affect the blob
        /// (e.g. the DXC -fvk-*-shift set). A change to ANY argument yields a
        /// different key.</summary>
        public static string ComputeKey(
            string formatTag, string source, string entryPoint, string profile, params string[] flags)
        {
            var sb = new StringBuilder(source.Length + 128);
            sb.Append("v").Append(FormatVersion).Append('\n');
            sb.Append(formatTag).Append('\n');
            sb.Append(entryPoint).Append('\n');
            sb.Append(profile).Append('\n');
            if (flags != null)
                foreach (var f in flags) sb.Append(f ?? "").Append('\x1f');
            sb.Append('\n');
            sb.Append(source);

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        /// <summary>Load the cached blob for (<paramref name="kind"/>,
        /// <paramref name="key"/>), or null on any miss / read failure.
        /// <paramref name="kind"/> is a short filename prefix ("fxc" / "spv").</summary>
        public static byte[]? TryLoad(string kind, string key)
        {
            if (Disabled) return null;
            try
            {
                string path = PathFor(kind, key);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch { return null; }
        }

        /// <summary>Persist <paramref name="blob"/> for (<paramref name="kind"/>,
        /// <paramref name="key"/>). Best-effort; write failures are swallowed.
        /// The write is staged to a temp file then moved into place so a
        /// concurrent or interrupted write can never leave a torn blob.</summary>
        public static void Store(string kind, string key, byte[] blob)
        {
            if (Disabled || blob == null || blob.Length == 0) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                string path = PathFor(kind, key);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, blob);
                try { File.Move(tmp, path, overwrite: true); }
                catch { TryDelete(tmp); }
            }
            catch { /* best effort — a failed store just means a future recompile */ }
        }

        /// <summary>Delete a poisoned/rejected blob so the next launch recompiles
        /// it. Called when the driver rejects a loaded blob.</summary>
        public static void Invalidate(string kind, string key)
        {
            try
            {
                string path = PathFor(kind, key);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }

        /// <summary>Delete every cached blob. For a "clear shader cache" action;
        /// safe to call anytime (blobs are pure derived data).</summary>
        public static void ClearAll()
        {
            try
            {
                if (Directory.Exists(CacheDir))
                    Directory.Delete(CacheDir, recursive: true);
            }
            catch { /* best effort */ }
        }

        private static string PathFor(string kind, string key)
            => Path.Combine(CacheDir, $"{kind}-{key}.bin");

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
