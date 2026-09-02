// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ShaderBlobCacheTests.cs — #456.
//
// Covers the backend-agnostic on-disk shader-blob cache: key stability, the
// store/load round-trip, that ANY change to source/entry/profile/flags yields a
// different key (never a stale hit), invalidation, and the Disabled gate.
// The data root is already redirected to a throwaway temp dir for the whole
// test process (TestDataRootIsolation), so these hit real disk harmlessly.

using System.Text;

using FracturingFog.Abstractions;

using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ShaderBlobCacheTests
{
    private const string Kind = "test";
    private const string Src = "float4 CSMain(){ return 0; }";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ComputeKey_IsStable_ForIdenticalInputs()
    {
        string a = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src, "CSMain", "cs_5_0");
        string b = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src, "CSMain", "cs_5_0");
        Assert.Equal(a, b);
        Assert.NotEqual(0, a.Length);
    }

    [Theory]
    [InlineData("fxc-cs_5_0", "float4 CSMain(){ return 1; }", "CSMain", "cs_5_0")] // source differs
    [InlineData("fxc-cs_5_0", Src, "CSOther", "cs_5_0")]                            // entry differs
    [InlineData("fxc-cs_5_0", Src, "CSMain", "cs_6_0")]                             // profile differs
    [InlineData("dxc-spirv", Src, "CSMain", "cs_5_0")]                              // format tag differs
    public void ComputeKey_ChangesWhenAnyInputChanges(string tag, string src, string entry, string profile)
    {
        string baseline = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src, "CSMain", "cs_5_0");
        string changed = ShaderBlobCache.ComputeKey(tag, src, entry, profile);
        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void ComputeKey_ChangesWhenFlagsChange()
    {
        string noFlags = ShaderBlobCache.ComputeKey("dxc-spirv", Src, "main", "cs_6_0");
        string withFlags = ShaderBlobCache.ComputeKey("dxc-spirv", Src, "main", "cs_6_0", "-fvk-b-shift", "4", "0");
        Assert.NotEqual(noFlags, withFlags);
    }

    [Fact]
    public void StoreThenLoad_RoundTripsBytes()
    {
        string key = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src + "roundtrip", "CSMain", "cs_5_0");
        byte[] blob = Bytes("compiled-bytecode-here");

        ShaderBlobCache.Store(Kind, key, blob);
        byte[]? loaded = ShaderBlobCache.TryLoad(Kind, key);

        Assert.NotNull(loaded);
        Assert.Equal(blob, loaded);
    }

    [Fact]
    public void TryLoad_ReturnsNull_OnMiss()
    {
        string key = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src + "never-stored", "CSMain", "cs_5_0");
        Assert.Null(ShaderBlobCache.TryLoad(Kind, key));
    }

    [Fact]
    public void Invalidate_DropsEntry()
    {
        string key = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src + "invalidate", "CSMain", "cs_5_0");
        ShaderBlobCache.Store(Kind, key, Bytes("x"));
        Assert.NotNull(ShaderBlobCache.TryLoad(Kind, key));

        ShaderBlobCache.Invalidate(Kind, key);
        Assert.Null(ShaderBlobCache.TryLoad(Kind, key));
    }

    [Fact]
    public void Disabled_SuppressesStoreAndLoad()
    {
        string key = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src + "disabled", "CSMain", "cs_5_0");
        bool prev = ShaderBlobCache.Disabled;
        try
        {
            ShaderBlobCache.Disabled = true;
            ShaderBlobCache.Store(Kind, key, Bytes("nope"));      // no-op
            Assert.Null(ShaderBlobCache.TryLoad(Kind, key));      // no-op
        }
        finally { ShaderBlobCache.Disabled = prev; }

        // With the cache re-enabled the earlier store really was suppressed.
        Assert.Null(ShaderBlobCache.TryLoad(Kind, key));
    }

    [Fact]
    public void Store_EmptyBlob_IsNoOp()
    {
        string key = ShaderBlobCache.ComputeKey("fxc-cs_5_0", Src + "empty", "CSMain", "cs_5_0");
        ShaderBlobCache.Store(Kind, key, System.Array.Empty<byte>());
        Assert.Null(ShaderBlobCache.TryLoad(Kind, key));
    }
}
