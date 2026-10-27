// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1490 — FF_GPU_PERTURB_BUDGET_MS=0 is documented to disable the deep-zoom
// too-slow abort, but the parse sent 0 and negatives to the 3000 ms default, so
// the abort stayed on (and latched GPU deep zoom off for the session). Checked:
//   • the parse: positive → that budget, 0 / negative → 0 (disabled), unset, blank,
//     NaN or garbage → the default;
//   • a disabled budget never aborts, however slow band 0 is; an enabled one
//     aborts exactly when band0 × bandCount exceeds it;
//   • end to end: the field initialiser reads the variable. The type is loaded
//     fresh in a collectible AssemblyLoadContext with the variable set, so its
//     static initialiser runs again and the field it produces is read back.

using System;
using System.Reflection;
using System.Runtime.Loader;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // sets FF_GPU_PERTURB_BUDGET_MS
public sealed class S1490PerturbBudgetTests
{
    private const string Var = "FF_GPU_PERTURB_BUDGET_MS";

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("-5", 0.0)]
    [InlineData("-1e9", 0.0)]
    [InlineData("-Infinity", 0.0)]
    [InlineData("1500", 1500.0)]
    [InlineData("1e9", 1e9)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 250 ", 250.0)]
    public void Parse_number(string value, double expected)
        => Assert.Equal(expected, MandelbrotKernelSource.ParsePerturbBudget(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData("3000ms")]
    [InlineData("NaN")]
    [InlineData("1,500")]   // invariant culture: not 1500, not 1.5
    public void Parse_unset_or_garbage_is_default(string? value)
        => Assert.Equal(3000.0, MandelbrotKernelSource.ParsePerturbBudget(value));

    [Fact]
    public void Default_constant_is_3000()
        => Assert.Equal(3000.0, MandelbrotKernelSource.DefaultPerturbBudgetMs);

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Disabled_budget_never_aborts(string value)
    {
        double budget = MandelbrotKernelSource.ParsePerturbBudget(value);
        foreach (double band0 in new[] { 0.0, 1.0, 1e3, 1e9, double.MaxValue })
            foreach (int bands in new[] { 1, 2, 68, 10_000 })
                Assert.False(MandelbrotKernelSource.PerturbTooSlow(band0, bands, budget));
    }

    [Theory]
    [InlineData(100.0, 30, false)]    // 3000 = budget: not over
    [InlineData(100.0, 31, true)]
    [InlineData(1.0, 2999, false)]
    [InlineData(1.0, 3001, true)]
    [InlineData(0.0, 10_000, false)]
    public void Enabled_budget_aborts_only_over_it(double band0Ms, int bandCount, bool expected)
        => Assert.Equal(expected, MandelbrotKernelSource.PerturbTooSlow(band0Ms, bandCount,
            MandelbrotKernelSource.ParsePerturbBudget(null)));

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("-5", 0.0)]
    [InlineData("1500", 1500.0)]
    [InlineData(null, 3000.0)]
    [InlineData("garbage", 3000.0)]
    public void Field_initialiser_reads_the_variable(string? value, double expected)
    {
        // Make sure the default context's copy initialised before the variable
        // changes, so no other test sees this value.
        _ = MandelbrotKernelSource.PerturbBudgetMs;
        string? saved = Environment.GetEnvironmentVariable(Var);
        var alc = new AssemblyLoadContext("S1490-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            Environment.SetEnvironmentVariable(Var, value);
            Assembly asm = alc.LoadFromAssemblyPath(typeof(MandelbrotKernelSource).Assembly.Location);
            Assert.NotSame(typeof(MandelbrotKernelSource).Assembly, asm);
            Type t = asm.GetType(typeof(MandelbrotKernelSource).FullName!, throwOnError: true)!;
            FieldInfo f = t.GetField(nameof(MandelbrotKernelSource.PerturbBudgetMs),
                BindingFlags.Public | BindingFlags.Static)!;
            Assert.Equal(expected, (double)f.GetValue(null)!);

            // The fresh copy's abort agrees with its field.
            MethodInfo tooSlow = t.GetMethod(nameof(MandelbrotKernelSource.PerturbTooSlow),
                new[] { typeof(double), typeof(int) })!;
            Assert.Equal(expected > 0, (bool)tooSlow.Invoke(null, new object[] { 1e9, 68 })!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
            alc.Unload();
        }
    }
}
