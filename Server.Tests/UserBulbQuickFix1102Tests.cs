// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1102 — the User Bulb editor's quick fix: a parse error's "Did you mean 'x'?"
// becomes an Apply fix (Ctrl+.) for the source box and for chain steps. Chain
// parse errors now carry the step index from the parser through the
// calculator and render host to the editor.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserBulbQuickFix1102Tests
{
    private static readonly object s_rxLock = new();
    private static bool s_rxReady;

    private static UserBulbViewModel NewEditor(FractalParameters p)
    {
        lock (s_rxLock)
        {
            if (!s_rxReady)
            {
                try { ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp(); }
                catch (InvalidOperationException) { /* already initialised elsewhere */ }
                s_rxReady = true;
            }
        }
        return new UserBulbViewModel(p);
    }

    [Fact]
    public void ChainParseError_NamesTheStep_AndCarriesItsIndex()
    {
        var steps = new List<UserBulbChainStep>
        {
            new() { OutputName = "a", Source = "z^8 + c" },
            new() { OutputName = "b", Source = "aa^4 + c" },
        };
        var ex = Assert.Throws<SbxParseException>(() => SandboxBulbChain.Parse(steps, new[] { "t" }));
        Assert.Equal(1, ex.StepIndex);
        Assert.Equal("Step 2 (b): Unknown identifier 'aa' at line 1, col 1. Did you mean 'a'?", ex.Message);
        Assert.Equal(0, ex.Position);
        Assert.Equal(2, ex.Length);
    }

    [Fact]
    public void Calculator_ReportsTheErrorStep()
    {
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbChain = new List<UserBulbChainStep>
                {
                    new() { OutputName = "a", Source = "z^8 + c" },
                    new() { OutputName = "b", Source = "aa^4 + c" },
                },
            },
        };
        calc.Compile("z^8 + c");
        Assert.False(calc.IsCompiled);
        Assert.Equal(1, calc.LastErrorStep);
        Assert.Equal(0, calc.LastErrorPosition);

        calc.FractalParameters.UserBulbChain.Clear();
        calc.Compile("z^8 + sni(c)");
        Assert.Equal(-1, calc.LastErrorStep);
        Assert.Equal(6, calc.LastErrorPosition);
    }

    [Fact]
    public void QuickFix_InTheSourceBox_AppliesTheSuggestion_AndRecompiles()
    {
        var p = new FractalParameters { UserBulbSource = "sni(z)*1.5 + c" };
        var vm = NewEditor(p);
        vm.Source = "sni(z)*1.5 + c";
        int compiles = 0;
        vm.CompileRequested += (_, _) => compiles++;

        vm.ShowError("Unknown function 'sni' at line 1, col 1. Did you mean 'sin'?");
        vm.SetErrorSpan(0, 3);
        Assert.Equal("sin", vm.SuggestedFix);
        Assert.True(vm.HasSuggestedFix);

        vm.ApplyFixCommand.Execute().Subscribe();
        Assert.Equal("sin(z)*1.5 + c", vm.Source);
        Assert.Equal("sin(z)*1.5 + c", p.UserBulbSource);
        Assert.Equal(1, compiles);
        Assert.False(vm.HasSuggestedFix);
    }

    [Fact]
    public void QuickFix_InAChainStep_EditsThatStep()
    {
        var p = new FractalParameters
        {
            UserBulbSource = "z^8 + c",
            UserBulbChain = new List<UserBulbChainStep>
            {
                new() { OutputName = "a", Source = "z^8 + c" },
                new() { OutputName = "b", Source = "aa^4 + c" },
            },
        };
        var vm = NewEditor(p);
        vm.ShowError("Step 2 (b): Unknown identifier 'aa' at line 1, col 1. Did you mean 'a'?");
        vm.SetErrorSpan(0, 2, stepIndex: 1);
        Assert.Equal("a", vm.SuggestedFix);
        Assert.Equal(1, vm.ErrorSpanStep);

        vm.ApplyFixCommand.Execute().Subscribe();
        Assert.Equal("a^4 + c", vm.Chain[1].Source);
        Assert.Equal("a^4 + c", p.UserBulbChain[1].Source);
        Assert.Same(p.UserBulbChain[1], vm.Chain[1]);
        Assert.Equal("z^8 + c", vm.Chain[0].Source);          // other steps untouched
        Assert.Equal("z^8 + c", vm.Source);                    // and the source box
    }

    [Fact]
    public void NoSuggestion_NoQuickFix()
    {
        var vm = NewEditor(new FractalParameters { UserBulbSource = "z^8 + c" });
        vm.ShowError("Expected ')' at line 1, col 8.");
        vm.SetErrorSpan(7, 1);
        Assert.False(vm.HasSuggestedFix);
        vm.ShowError(string.Empty);
        vm.SetErrorSpan(-1, 0);
        Assert.False(vm.HasSuggestedFix);
        Assert.Equal(-1, vm.ErrorSpanStep);
    }
}
