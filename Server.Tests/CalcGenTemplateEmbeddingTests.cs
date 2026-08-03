// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 5a smoke follow-up — the CalcGen "Compile & Load" path
// (CalculatorGenHotLoad → generate C# → Roslyn) embeds the equation source in a
// single-line `//` comment. A multi-line equation used to break out of that
// comment: its 2nd+ lines became code (CS1002 "; expected") and the template's
// later `using` directives then followed code (CS1529 "A using must precede all
// other elements..."). CalculatorGenApi.OneLine collapses the source's newlines
// so the comment stays one line. These tests hot-load a multi-line equation
// end-to-end (real Roslyn compile) to prove the generated file compiles.

using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenTemplateEmbeddingTests
{
    [Fact]
    public void MultiLineEquation_HotLoads_NoTemplateBreakout()
    {
        // A multi-line source (as several saved equations are after migration).
        const string multiline = "sin(z * n)\r\n+\r\n(c)^2\r\n+ c";
        var r = FracturingFog.CalculatorGen.CalculatorGenHotLoad
            .TryCompileAndLoad(multiline, "MultiLineSmoke");
        Assert.True(r.Ok, $"multi-line equation should generate + compile: {r.Error}");
        Assert.NotNull(r.CalculatorType);
    }

    [Fact]
    public void SingleLineEquation_StillHotLoads()
    {
        var r = FracturingFog.CalculatorGen.CalculatorGenHotLoad
            .TryCompileAndLoad("z*z + c", "SingleLineSmoke");
        Assert.True(r.Ok, $"single-line control should compile: {r.Error}");
    }
}
