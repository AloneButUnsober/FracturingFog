// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// D3DShaderCache.cs — #456.
//
// Thin FXC front-end that consults the on-disk ShaderBlobCache before paying a
// runtime Compiler.Compile. On a cache hit it CreateComputeShader's straight
// from the persisted cs_5_0 bytecode (portable across runs on the same
// machine); on a miss it compiles, persists the bytecode, then creates the
// shader. If the driver rejects a cached blob the entry is deleted and the
// source is compiled — the cache is a pure accelerator (see #456).
//
// Every D3D compute kernel (Mandelbrot / Relief / Froxel) routes its
// Compiler.Compile through here so first-launch compile is paid once per
// machine, not once per process.

using System;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

[SupportedOSPlatform("windows")]
internal static class D3DShaderCache
{
    private const string Kind = "fxc";

    /// <summary>Return a compute shader for <paramref name="hlsl"/>, loading its
    /// FXC bytecode from disk when a valid cache entry exists, otherwise
    /// compiling and persisting it. <paramref name="errorLabel"/> prefixes the
    /// exception message on a genuine source-compile failure so each kernel
    /// keeps its own diagnostic wording.</summary>
    public static ID3D11ComputeShader CompileOrLoad(
        ID3D11Device device, string hlsl, string entryPoint, string profile,
        string sourceName, string errorLabel)
    {
        string tag = $"{Kind}-{profile}";
        string key = FracturingFog.Abstractions.ShaderBlobCache.ComputeKey(
            tag, hlsl, entryPoint, profile);

        // Fast path: reuse the machine-cached bytecode blob.
        byte[]? cached = FracturingFog.Abstractions.ShaderBlobCache.TryLoad(Kind, key);
        if (cached != null)
        {
            try { return device.CreateComputeShader(cached); }
            catch
            {
                // Poisoned/incompatible blob (driver rejected it) — drop it and
                // fall through to a clean recompile.
                FracturingFog.Abstractions.ShaderBlobCache.Invalidate(Kind, key);
            }
        }

        var hr = Compiler.Compile(hlsl, entryPoint, sourceName, profile, out var blob, out var errBlob);
        if (hr.Failure || blob == null)
        {
            string msg = errBlob?.AsString() ?? hr.ToString();
            errBlob?.Dispose();
            throw new InvalidOperationException($"{errorLabel}: HLSL compile failed — {msg}");
        }
        try
        {
            byte[] bytes = blob.AsSpan().ToArray();
            FracturingFog.Abstractions.ShaderBlobCache.Store(Kind, key, bytes);
            return device.CreateComputeShader(bytes);
        }
        finally { blob.Dispose(); errBlob?.Dispose(); }
    }
}
