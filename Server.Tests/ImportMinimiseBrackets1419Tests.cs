// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1419 — Paste LaTeX / Paste MathML optionally run DslFormatter.MinimiseParens
// on the importer's result. These tests pin:
//   - with the option on, a bracket-heavy import arrives minimised, with the
//     identical DslCanonical parse tree as the raw import, and the status
//     says how many pairs went;
//   - with the option off, the editor gets the importer's text untouched;
//   - an import with nothing redundant keeps the plain status;
//   - the preference defaults on, also for a dsl-editor.json written before it.
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class ImportMinimiseBrackets1419Tests
{
    private static readonly object s_rxLock = new();
    private static bool s_rxReady;

    private const string BracketyLatex = @"\left(\left(z^{2}\right)\right)+\left(c\right)";

    private const string BracketyMathml =
        "<math><mrow><mo>(</mo><mrow><mo>(</mo><mi>z</mi><mo>*</mo><mi>z</mi><mo>)</mo></mrow><mo>)</mo></mrow>" +
        "<mo>+</mo><mrow><mo>(</mo><mi>c</mi><mo>)</mo></mrow></math>";

    private static UserEquationViewModel NewEditor()
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
        return new UserEquationViewModel(new FractalParameters());
    }

    private static int Opens(string s) => s.Count(ch => ch == '(');

    // Run with the option set, restoring the persisted value afterwards.
    private static UserEquationViewModel WithOption(bool on, Action<UserEquationViewModel> import)
    {
        var vm = NewEditor();
        bool was = vm.MinimiseImportBrackets;
        try
        {
            vm.MinimiseImportBrackets = on;
            import(vm);
            return vm;
        }
        finally { vm.MinimiseImportBrackets = was; }
    }

    [Fact]
    public void PasteLatex_On_MinimisesWithTheSameTree()
    {
        var raw = LatexImporter.Import(BracketyLatex);
        Assert.True(raw.Ok, raw.Error);
        string? rawTree = DslCanonical.Of(raw.Dsl, DslDialect.Complex2D);
        Assert.NotNull(rawTree);

        var vm = WithOption(true, v => v.ImportLatexFromText(BracketyLatex));

        Assert.Equal("z^2 + c", vm.Source);
        Assert.Equal(rawTree, DslCanonical.Of(vm.Source, DslDialect.Complex2D));
        int removed = Opens(raw.Dsl) - Opens(vm.Source);
        Assert.True(removed >= 3, $"raw import was \"{raw.Dsl}\"");
        Assert.False(vm.StatusIsError);
        Assert.Equal($"✓ Imported LaTeX; removed {removed} redundant bracket pairs", vm.StatusText);
    }

    [Fact]
    public void PasteLatex_Off_LeavesTheImportUntouched()
    {
        var raw = LatexImporter.Import(BracketyLatex);
        Assert.True(raw.Ok, raw.Error);

        var vm = WithOption(false, v => v.ImportLatexFromText(BracketyLatex));

        Assert.Equal(raw.Dsl, vm.Source);
        Assert.Equal("✓ Imported LaTeX", vm.StatusText);
    }

    [Fact]
    public void PasteMathml_On_MinimisesWithTheSameTree_OffUntouched()
    {
        var raw = MathmlImporter.Import(BracketyMathml);
        Assert.True(raw.Ok, raw.Error);
        string? rawTree = DslCanonical.Of(raw.Dsl, DslDialect.Complex2D);
        Assert.NotNull(rawTree);

        var on = WithOption(true, v => v.ImportMathmlFromText(BracketyMathml));
        Assert.Equal(rawTree, DslCanonical.Of(on.Source, DslDialect.Complex2D));
        Assert.Equal(0, Opens(on.Source));
        Assert.StartsWith("✓ Imported MathML; removed ", on.StatusText);

        var off = WithOption(false, v => v.ImportMathmlFromText(BracketyMathml));
        Assert.Equal(raw.Dsl, off.Source);
        Assert.Equal("✓ Imported MathML", off.StatusText);
    }

    [Fact]
    public void PasteLatex_NothingRedundant_KeepsThePlainStatus()
    {
        const string latex = @"\frac{z}{z+c}";
        var raw = LatexImporter.Import(latex);
        Assert.True(raw.Ok, raw.Error);

        var vm = WithOption(true, v => v.ImportLatexFromText(latex));

        Assert.Equal(DslCanonical.Of(raw.Dsl, DslDialect.Complex2D), DslCanonical.Of(vm.Source, DslDialect.Complex2D));
        Assert.Contains("(", vm.Source);    // z/(z+c) keeps its needed pair
        if (vm.Source == raw.Dsl) Assert.Equal("✓ Imported LaTeX", vm.StatusText);
    }

    [Fact]
    public void Preference_DefaultsOn_IncludingForAnOlderFile()
    {
        Assert.True(new DslEditorPrefs().MinimiseImportBrackets);

        string file = Path.Combine(AppDataPaths.Root, "dsl-editor.json");
        string? before = File.Exists(file) ? File.ReadAllText(file) : null;
        try
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            File.WriteAllText(file, "{ \"codeView\": { \"UserEquation\": true } }");
            Assert.True(DslEditorPrefsStore.Load().MinimiseImportBrackets);

            DslEditorPrefsStore.Save(new DslEditorPrefs { MinimiseImportBrackets = false });
            Assert.False(DslEditorPrefsStore.Load().MinimiseImportBrackets);
        }
        finally
        {
            if (before is null) File.Delete(file); else File.WriteAllText(file, before);
        }
    }
}
