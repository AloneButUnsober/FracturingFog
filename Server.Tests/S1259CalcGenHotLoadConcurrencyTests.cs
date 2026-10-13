// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1259 — CalculatorGenHotLoad keeps a process-wide type cache and context
// list. xUnit runs test classes in parallel (and the live UI compiles off the
// UI thread), and unguarded concurrent TryCompileAndLoad calls corrupted the
// Dictionary ("Operations that change non-concurrent collections must have
// exclusive access"). Hammer it from many threads with distinct and repeated
// equations: every call must succeed and return the class its name asks for.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FracturingFog.CalculatorGen;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1259CalcGenHotLoadConcurrencyTests
{
    private static readonly (string Dsl, string Name)[] Equations =
    {
        ("z*z + c", "C1259a"),
        ("z*z*z + c", "C1259b"),
        ("z*z + sin(c)", "C1259c"),
        ("z*z*z*z + c", "C1259d"),
    };

    [Fact]
    public void Concurrent_Compiles_All_Succeed()
    {
        const int Repeats = 16;   // repeats hit the cache while other threads write it
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, Equations.Length * Repeats, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var (dsl, name) = Equations[i % Equations.Length];
            try
            {
                var r = CalculatorGenHotLoad.TryCompileAndLoad(dsl, name);
                if (!r.Ok) failures.Add($"[{dsl}] {r.Error}");
                else if (!r.CalculatorType!.Name.StartsWith(name, StringComparison.Ordinal))
                    failures.Add($"[{dsl}] got {r.CalculatorType.Name}");
            }
            catch (Exception ex)
            {
                failures.Add($"[{dsl}] {ex.GetType().Name}: {ex.Message}");
            }
        });
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Distinct()));
    }
}
