// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 3b — 3D render parity harness (was the old Phase 2d).
//
// With the raw-C# Roslyn compile path deleted from UserBulbCalculator, the
// Sandbox DSL interpreter is the ONLY way a bulb runs. These render-level tests
// are the regression guard that the DSL path both compiles every shipped preset
// and drives a real raymarched surface end-to-end (no bulb regresses):
//
//   1. every seeded built-in bulb compiles on the interpreter — nothing can be
//      rescued by a fallback any more, so a broken DSL migration surfaces here;
//   2. a representative DSL bulb renders deterministically and non-blank
//      through the full raymarch + DE + shading pipeline;
//   3. a raw-C# body no longer compiles (the deleted path is really gone).
//
// Per-step interpreter-vs-native math parity is locked separately by
// SandboxBulbDslAuditTests (Phase 2a); this harness covers the render path.
//
// AppDataPaths is redirected to a throwaway temp dir for the whole test process
// (TestDataRootIsolation), so the UserBulbStore singleton never touches real
// user data. Shares the region-library collection to serialise singleton use.

using System.Collections.Generic;
using System.IO;
using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserBulbDslRenderParityTests
{
    // ── 1. every shipped built-in compiles on the DSL interpreter ────────────

    [Fact]
    public void AllSeededBuiltins_CompileOnDslInterpreter()
    {
        var store = UserBulbStore.Instance;
        // Force a clean reseed so we inspect the shipped defaults.
        string file = AppDataPaths.Combine("userbulbs.json");
        if (File.Exists(file)) File.Delete(file);
        store.Load();

        Assert.NotEmpty(store.Equations);
        foreach (var e in store.Equations)
        {
            var fp = new FractalParameters
            {
                UserBulbSource = e.Source,
                // The persisted selector is ignored post-Phase-3, but set it to
                // the preset's own value to mirror a real load.
                UserBulbCompiler = e.Settings?.Compiler ?? UserBulbCompilerKind.Sandbox,
            };
            if (e.Chain is { Count: > 0 }) fp.UserBulbChain = e.Chain;
            if (e.Settings?.KifsScale is double ks and > 0.0) fp.UserBulbKifsScale = ks;

            var calc = new UserBulbCalculator(8, 8) { FractalParameters = fp };
            calc.Compile(e.Source);

            Assert.True(calc.IsCompiled, $"'{e.Name}' did not compile on the DSL: {calc.LastError}");
            Assert.True(string.IsNullOrEmpty(calc.LastError), $"'{e.Name}' compiled with error text: {calc.LastError}");
        }
    }

    // ── 2. a representative DSL bulb renders deterministically + non-blank ────

    [Fact]
    public void DslMandelbulb_Renders_Deterministic_AndNonBlank()
    {
        FractalParameters Fp() => new()
        {
            UserBulbSource = "z^8 + c",
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbIterations = 6,
            UserBulbMaxSteps = 48,
        };

        var calc = new UserBulbCalculator(40, 40) { FractalParameters = Fp() };
        calc.Calculate();
        uint[] first = (uint[])calc.ColorBuffer.Clone();

        // Determinism: a second render of the same params is byte-identical.
        calc.Calculate();
        Assert.Equal(first, calc.ColorBuffer);

        // Non-degenerate: the raymarch produced both surface and background
        // pixels, so the DSL kernel actually drives a 3D surface.
        var distinct = new HashSet<uint>(first);
        Assert.True(distinct.Count > 1,
            $"expected a non-blank render, got {distinct.Count} distinct colour(s)");
    }

    // ── 3. the deleted raw-C# path is really gone ────────────────────────────

    [Fact]
    public void RawCsharpBulbBody_NoLongerCompiles()
    {
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbSource = "return Vec3.Pow(z, 8) + c;",
                UserCodeOrigin = UserCodeOrigin.Interactive, // even trusted
            }
        };
        calc.Compile("return Vec3.Pow(z, 8) + c;");
        Assert.False(calc.IsCompiled);
        Assert.False(string.IsNullOrWhiteSpace(calc.LastError));
    }
}
