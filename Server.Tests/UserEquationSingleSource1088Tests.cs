// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1088 part B — the User Equation data model collapses to ONE source plus a
// "use CalcGen" flag. Before #1088 a saved entry carried Kind (0 = User
// Equation tab, 1 = DSL tab) and FractalParameters carried two sources and the
// active tab. These tests pin:
//   - legacy JSON (Kind 1 / 0 / absent) maps onto UseCalcGen and rewrites as
//     UseCalcGen only;
//   - a recalled region (live, batch, scene, video: ApplyHeadlessParams /
//     UserEquationEntry.ApplyTo) carries source AND flag, replacing stale state;
//   - the flag never changes the interpreted image;
//   - the two-tab editor (until #1089) maps its tabs onto the single source.
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using FracturingFog.Security;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserEquationSingleSource1088Tests
{
    private static string EquationsFile => AppDataPaths.Combine("userequations.json");

    private const string LegacyJson = """
        [
          { "Name": "SS1088_Dsl",     "Source": "z^3 + c", "Kind": 1 },
          { "Name": "SS1088_Live",    "Source": "z*z + c", "Kind": 0 },
          { "Name": "SS1088_NoKind",  "Source": "z*z*z*z + c" }
        ]
        """;

    private static UserEquationStore LoadLegacy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EquationsFile)!);
        File.WriteAllText(EquationsFile, LegacyJson);
        var store = UserEquationStore.Instance;
        store.Load();
        return store;
    }

    [Fact]
    public void LegacyKind_MapsToUseCalcGen_AndIsRewrittenWithoutKind()
    {
        var store = LoadLegacy();
        try
        {
            Assert.True(store.GetByName("SS1088_Dsl")!.UseCalcGen);
            Assert.False(store.GetByName("SS1088_Live")!.UseCalcGen);
            Assert.False(store.GetByName("SS1088_NoKind")!.UseCalcGen);

            // Load saved (the language upgrade stamps + saves); the file now
            // speaks the new model only.
            store.Save();
            string json = File.ReadAllText(EquationsFile);
            Assert.DoesNotContain("\"Kind\"", json);
            Assert.Contains("\"UseCalcGen\": true", json);

            // And it round-trips.
            store.Load();
            Assert.True(store.GetByName("SS1088_Dsl")!.UseCalcGen);
            Assert.False(store.GetByName("SS1088_Live")!.UseCalcGen);
        }
        finally { foreach (var n in new[] { "SS1088_Dsl", "SS1088_Live", "SS1088_NoKind" }) store.Remove(n); }
    }

    [Fact]
    public void RecalledRegion_CarriesSourceAndFlag_ReplacingStaleState()
    {
        var store = LoadLegacy();
        try
        {
            var region = new FractalRegion { FractalType = FractalType.UserEquation, UserEquationName = "SS1088_Dsl" };
            // Stale state from an earlier equation: must not survive the recall.
            var p = new FractalParameters { UserEquationSource = "sin(z) + c", UserEquationUseCalcGen = false };
            region.ApplyHeadlessParams(p);   // batch / scene / video recall path
            Assert.Equal("z^3 + c", p.UserEquationSource);
            Assert.True(p.UserEquationUseCalcGen);
            Assert.Equal("SS1088_Dsl", p.UserEquationName);

            var live = new FractalRegion { FractalType = FractalType.UserEquation, UserEquationName = "SS1088_Live" };
            live.ApplyHeadlessParams(p);
            Assert.Equal("z*z + c", p.UserEquationSource);
            Assert.False(p.UserEquationUseCalcGen);
        }
        finally { foreach (var n in new[] { "SS1088_Dsl", "SS1088_Live", "SS1088_NoKind" }) store.Remove(n); }
    }

    // A legacy DSL-tab entry renders exactly what its text says, the same as the
    // text with the flag off (the flag routes the editor, never the image).
    [Fact]
    public void LegacyDslEntry_RendersTheSameAsItsText()
    {
        var store = LoadLegacy();
        try
        {
            var p = new FractalParameters();
            new FractalRegion { FractalType = FractalType.UserEquation, UserEquationName = "SS1088_Dsl" }.ApplyHeadlessParams(p);
            var viaRegion = Render(p);
            var plain = Render(new FractalParameters { UserEquationSource = "z^3 + c", UserCodeOrigin = UserCodeOrigin.Interactive });
            Assert.Equal(plain.ColorBuffer, viaRegion.ColorBuffer);
            Assert.True(viaRegion.ColorBuffer.Distinct().Count() > 4, "recalled equation rendered flat");
        }
        finally { foreach (var n in new[] { "SS1088_Dsl", "SS1088_Live", "SS1088_NoKind" }) store.Remove(n); }
    }

    private static UserEquationCalculator Render(FractalParameters fp)
    {
        var c = new UserEquationCalculator(64, 48)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 100,
            ColorMap = new MonoBandMap(),
            FractalParameters = fp,
        };
        c.Calculate(default);
        Assert.True(c.IsCompiled, c.LastError);
        return c;
    }

    [Fact]
    public void Clone_CopiesTheFlag()
    {
        var p = new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true };
        var q = p.Clone();
        Assert.True(q.UserEquationUseCalcGen);
        Assert.Equal("z^3 + c", q.UserEquationSource);
    }

    [Fact]
    public void Promoted_CarriesTheFlag()
    {
        var store = UserEquationStore.Instance;
        const string name = "SS1088_Promoted";
        try
        {
            store.SaveEquation(name, "z^3 + c", useCalcGen: true);
            store.SetPromoted(name, true);
            var r = RegisteredFractalCatalog.All.Single(x => x.Name == name);
            Assert.True(r.UseCalcGen);
        }
        finally { store.Remove(name); }
    }

    // ── Two-tab editor (until #1089) on the single source ──

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
                catch (System.InvalidOperationException) { /* already initialised elsewhere */ }
                s_rxReady = true;
            }
        }
        return new UserEquationViewModel(p);
    }

    [Fact]
    public void Editor_OpensOnTheFlagsTab_WithTheSource()
    {
        var p = new FractalParameters { UserEquationSource = "z^3 + c", UserEquationUseCalcGen = true };
        var vm = NewEditor(p);
        Assert.Equal(1, vm.ActiveTabIndex);
        Assert.Equal("z^3 + c", vm.DslSource);
        Assert.Equal("z^3 + c", vm.Source);   // equation language: both tabs read it

        var q = new FractalParameters { UserEquationSource = "return Complex.Pow(z, 2) + c;" };
        var vm2 = NewEditor(q);
        Assert.Equal(0, vm2.ActiveTabIndex);
        Assert.Equal("return Complex.Pow(z, 2) + c;", vm2.Source);
        Assert.Equal("z*z + c", vm2.DslSource);   // C#-style text stays off the CalcGen tab
        Assert.False(q.UserEquationUseCalcGen);
    }

    [Fact]
    public void Editor_TabSwitch_PublishesThatTabAsTheSource()
    {
        var p = new FractalParameters { UserEquationSource = "z*z + c" };
        var vm = NewEditor(p);
        vm.DslSource = "z^3 + c";                 // inactive tab: source unchanged
        Assert.Equal("z*z + c", p.UserEquationSource);
        Assert.False(p.UserEquationUseCalcGen);

        vm.ActiveTabIndex = 1;
        Assert.Equal("z^3 + c", p.UserEquationSource);
        Assert.True(p.UserEquationUseCalcGen);

        vm.DslSource = "z^4 + c";                 // active tab: source follows
        Assert.Equal("z^4 + c", p.UserEquationSource);

        vm.ActiveTabIndex = 0;
        Assert.Equal("z*z + c", p.UserEquationSource);
        Assert.False(p.UserEquationUseCalcGen);
    }

    [Fact]
    public void Editor_SelectingALegacyDslEntry_LoadsIntoTheCalcGenTab()
    {
        var store = LoadLegacy();
        try
        {
            var p = new FractalParameters { UserEquationSource = "z*z + c" };
            var vm = NewEditor(p);
            vm.SelectedSavedName = "SS1088_Dsl";
            Assert.Equal(1, vm.ActiveTabIndex);
            Assert.Equal("z^3 + c", vm.DslSource);
            Assert.Equal("z^3 + c", p.UserEquationSource);
            Assert.True(p.UserEquationUseCalcGen);

            vm.SelectedSavedName = "SS1088_Live";
            Assert.Equal(0, vm.ActiveTabIndex);
            Assert.Equal("z*z + c", p.UserEquationSource);
            Assert.False(p.UserEquationUseCalcGen);
        }
        finally { foreach (var n in new[] { "SS1088_Dsl", "SS1088_Live", "SS1088_NoKind" }) store.Remove(n); }
    }
}
