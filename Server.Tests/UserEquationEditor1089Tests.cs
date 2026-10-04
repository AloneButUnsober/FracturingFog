// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1089 — the single User Equation editor: one source box plus a CalcGen
// toggle, replacing the two-tab notebook. These tests pin:
//   - the editor opens on the parameters' source and flag, and the toggle
//     routes Compile & Load / Compile + Save / Generate;
//   - Compile & Load runs asynchronously with a visible state (idle →
//     compiling → compiled / failed), and an edit makes it stale;
//   - save / select round-trips the single source and the flag, and a legacy
//     (pre-#1088 "Kind": 1) entry loads into the unified editor;
//   - C#-style text is accepted and the quick fix offers its converted form;
//     a typo gets a positioned span with its Did-you-mean;
//   - the CalcGen report says what CalcGen will and won't accelerate;
//   - Morph validates with the equation language;
//   - the old tab bindings are gone (no ActiveTabIndex / DslSource routing).
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows.Input;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserEquationEditor1089Tests
{
    private static readonly object s_rxLock = new();
    private static bool s_rxReady;

    // The view-model uses WhenAnyValue; the app initialises ReactiveUI through
    // Avalonia, a test has to do it itself (once per process).
    private static UserEquationViewModel NewEditor(FractalParameters p)
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
        return new UserEquationViewModel(p);
    }

    private static bool CanRun(ICommand c) => c.CanExecute(null);

    [Fact]
    public void Opens_OnTheParametersSourceAndFlag()
    {
        var on = NewEditor(new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true });
        Assert.True(on.UseCalcGen);
        Assert.Equal("z^3 + c", on.Source);

        var off = NewEditor(new FractalParameters { UserEquationSource = "z*z + c" });
        Assert.False(off.UseCalcGen);
        Assert.Equal("z*z + c", off.Source);

        var blank = NewEditor(new FractalParameters());
        Assert.Equal("z*z + c", blank.Source);
    }

    [Fact]
    public void Toggle_RoutesTheCalcGenActions_AndThePersistedFlag()
    {
        var p = new FractalParameters { UserEquationSource = "z*z + c" };
        var vm = NewEditor(p);
        Assert.False(CanRun(vm.HotLoadViaCalcGenCommand));
        Assert.False(CanRun(vm.HotLoadAndPersistCommand));
        Assert.False(CanRun(vm.GenerateViaCalcGenCommand));

        vm.UseCalcGen = true;
        Assert.True(p.UserEquationUseCalcGen);
        Assert.True(CanRun(vm.HotLoadViaCalcGenCommand));
        Assert.True(CanRun(vm.HotLoadAndPersistCommand));
        Assert.True(CanRun(vm.GenerateViaCalcGenCommand));

        // Off again returns the view to the live interpreter (host recompiles).
        int compiles = 0;
        vm.CompileRequested += () => compiles++;
        vm.UseCalcGen = false;
        Assert.False(p.UserEquationUseCalcGen);
        Assert.Equal(1, compiles);
    }

    [Fact]
    public async Task CompileAndLoad_ShowsCompiling_ThenCompiled_AndAnEditMakesItStale()
    {
        var vm = NewEditor(new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true });
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? gotEquation = null;
        vm.HotLoadRequested += (eq, _) => { gotEquation = eq; return gate.Task; };

        Assert.Equal(UserEquationViewModel.CalcGenCompileState.Idle, vm.CompileState);
        var run = vm.HotLoadViaCalcGenCommand.Execute().ToTask();
        Assert.Equal(UserEquationViewModel.CalcGenCompileState.Compiling, vm.CompileState);
        Assert.True(vm.IsCompiling);
        Assert.False(CanRun(vm.HotLoadViaCalcGenCommand));   // one compile at a time
        Assert.Contains("Compiling", vm.CompileStateText);
        Assert.Equal("z^3 + c", gotEquation);

        gate.SetResult(null);
        await run;
        Assert.Equal(UserEquationViewModel.CalcGenCompileState.Compiled, vm.CompileState);
        Assert.False(vm.StatusIsError);

        vm.Source = "z^4 + c";   // the loaded calculator no longer matches the text
        Assert.Equal(UserEquationViewModel.CalcGenCompileState.Idle, vm.CompileState);
    }

    [Fact]
    public async Task CompileAndLoad_Failure_IsShownAsFailed()
    {
        var vm = NewEditor(new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true });
        vm.HotLoadRequested += (_, _) => Task.FromResult<string?>("Compile failed: CS0001 boom");
        await vm.HotLoadViaCalcGenCommand.Execute().ToTask();
        Assert.Equal(UserEquationViewModel.CalcGenCompileState.Failed, vm.CompileState);
        Assert.True(vm.CompileFailed);
        Assert.True(vm.StatusIsError);
        Assert.Contains("boom", vm.StatusText);
    }

    [Fact]
    public async Task CompileAndLoad_TranslatesCSharpStyleText()
    {
        var vm = NewEditor(new FractalParameters { UserEquationSource = "return Complex.Pow(z, 3) + c;", UserEquationUseCalcGen = true });
        string? gotEquation = null;
        vm.HotLoadRequested += (eq, _) => { gotEquation = eq; return Task.FromResult<string?>(null); };
        await vm.HotLoadViaCalcGenCommand.Execute().ToTask();
        Assert.NotNull(gotEquation);
        Assert.DoesNotContain("Complex", gotEquation);
        Assert.True(EquationLanguage.TryParse(gotEquation!, out _, out _));
    }

    [Fact]
    public async Task SaveAndSelect_RoundTripTheSourceAndFlag()
    {
        var store = UserEquationStore.Instance;
        const string a = "UE1089_On", b = "UE1089_Off";
        try
        {
            var p = new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true };
            var vm = NewEditor(p);
            vm.NamePromptRequested += _ => Task.FromResult<string?>(a);
            await vm.SaveCommand.Execute().ToTask();
            Assert.True(store.GetByName(a)!.UseCalcGen);
            Assert.Equal("z^3 + c", store.GetByName(a)!.Source);

            store.SaveEquation(b, "z*z*z*z + c", useCalcGen: false);
            vm.SelectedSavedName = b;
            Assert.False(vm.UseCalcGen);
            Assert.Equal("z*z*z*z + c", vm.Source);
            Assert.Equal("z*z*z*z + c", p.UserEquationSource);
            Assert.False(p.UserEquationUseCalcGen);

            vm.SelectedSavedName = a;
            Assert.True(vm.UseCalcGen);
            Assert.Equal("z^3 + c", vm.Source);
            Assert.True(p.UserEquationUseCalcGen);
            Assert.Equal(a, p.UserEquationName);   // loading isn't an edit
        }
        finally { store.Remove(a); store.Remove(b); }
    }

    [Fact]
    public void LegacyDslTabEntry_LoadsIntoTheUnifiedEditor()
    {
        string file = AppDataPaths.Combine("userequations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """[ { "Name": "UE1089_Legacy", "Source": "z^3 + c", "Kind": 1 } ]""");
        var store = UserEquationStore.Instance;
        try
        {
            store.Load();
            var p = new FractalParameters();
            var vm = NewEditor(p);
            vm.SelectedSavedName = "UE1089_Legacy";
            Assert.True(vm.UseCalcGen);
            Assert.Equal("z^3 + c", vm.Source);
            Assert.True(p.UserEquationUseCalcGen);
        }
        finally { store.Remove("UE1089_Legacy"); }
    }

    [Fact]
    public void CSharpStyleText_IsAccepted_AndTheQuickFixConvertsIt()
    {
        const string cs = "return Complex.Pow(z, 2) + c;";
        var vm = NewEditor(new FractalParameters { UserEquationSource = cs });
        Assert.False(vm.StatusIsError);
        Assert.Contains("C#-style", vm.StatusText);
        Assert.True(vm.HasSuggestedFix);
        Assert.Equal(0, vm.ErrorSpanStart);
        Assert.Equal(cs.Length, vm.ErrorSpanLength);

        string converted = vm.SuggestedFix!;
        vm.ApplyFixCommand.Execute().Subscribe();
        Assert.Equal(converted, vm.Source);
        Assert.True(EquationLanguage.TryParse(vm.Source, out _, out _));
        Assert.False(vm.HasSuggestedFix);
        Assert.Equal("✓ Equation parses", vm.StatusText);
    }

    [Fact]
    public void Typo_GetsAPositionedSpan_WithItsDidYouMean()
    {
        var vm = NewEditor(new FractalParameters { UserEquationSource = "z*z + sni(z)" });
        Assert.True(vm.StatusIsError);
        Assert.Equal(6, vm.ErrorSpanStart);
        Assert.Equal(3, vm.ErrorSpanLength);
        Assert.Equal("sin", vm.SuggestedFix);

        vm.ApplyFixCommand.Execute().Subscribe();
        Assert.Equal("z*z + sin(z)", vm.Source);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public void CalcGenReport_SaysWhatCalcGenWillAndWontAccelerate()
    {
        var p = new FractalParameters { UserEquationSource = "z*z + c", UserEquationUseCalcGen = true };
        var vm = NewEditor(p);
        Assert.Contains("compiles this", vm.CalcGenReport);
        Assert.Contains("Perturbation (deep zoom): on", vm.CalcGenReport);

        vm.SeedExpression = "c";
        Assert.Contains("z₀ seed is interpreter-only", vm.CalcGenReport);

        // A value-dependent kind: the interpreter takes it, CalcGen refuses.
        vm.Source = "log(re(c)) > 0 ? z*z + c : z";
        vm.TriggerCompile();
        Assert.Contains("can't compile", vm.CalcGenReport);
        Assert.True(vm.StatusIsError);           // with CalcGen on, Compile & Load would fail
        Assert.StartsWith("CalcGen:", vm.StatusText);

        vm.UseCalcGen = false;                    // off: only the interpreter matters
        Assert.False(vm.StatusIsError);
    }

    // Morph validates with the equation language, as each frame's compile does
    // (let / && only exist there, not in CalcGen's older dialect).
    [Fact]
    public void Morph_ValidatesWithTheEquationLanguage()
    {
        Assert.Null(EquationMorph.Validate("let w = z*z in w + c", "re(z) > 0 && im(z) > 0 ? z*z*z + c : z*z + c"));
        Assert.NotNull(EquationMorph.Validate("z*z + sni(z)", "z*z + c"));
    }

    [Fact]
    public void TheTwoTabNotebook_IsGone()
    {
        var props = typeof(UserEquationViewModel).GetProperties().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("ActiveTabIndex", props);
        Assert.DoesNotContain("DslSource", props);
        Assert.DoesNotContain("ValidateForCalcGen", props);
        Assert.Contains("UseCalcGen", props);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string axaml = File.ReadAllText(Path.Combine(dir!.FullName, "UI.Avalonia", "Views", "UserEquationView.axaml"));
        Assert.DoesNotContain("TabControl", axaml);
        Assert.DoesNotContain("ActiveTabIndex", axaml);
        Assert.DoesNotContain("DslSource", axaml);
        Assert.Contains("{Binding UseCalcGen", axaml);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(axaml, @"Text=""\{Binding Source, Mode=TwoWay\}"""));
    }
}
