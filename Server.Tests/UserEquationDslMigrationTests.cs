// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 5a — saved user equations are converted to the safe DSL on load.
// These tests prove:
//   1. a translatable C# entry is rewritten to DSL text + retagged Kind=Dsl,
//   2. an untranslatable entry (member/statement with no DSL form) is left
//      untouched (still editable via the live path),
//   3. the original userequations.json is snapshotted before the destructive
//      rewrite (recoverable backup), and
//   4. the migration is idempotent (a second run converts nothing).
//
// AppDataPaths is redirected to a throwaway temp dir for the whole test process
// (TestDataRootIsolation), so the UserEquationStore singleton never touches real
// user data. Shares the region-library collection to serialise singleton use.

using System.IO;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserEquationDslMigrationTests
{
    private static string EquationsFile => AppDataPaths.Combine("userequations.json");

    private static void SeedFile(params UserEquationEntry[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EquationsFile)!);
        File.WriteAllText(EquationsFile,
            JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Migration_ConvertsTranslatable_LeavesUntranslatable_AndBacksUp()
    {
        // Clear stale backups from sibling tests so backups[0] below is this
        // test's own pre-migration snapshot (the temp data root is shared).
        string dir = Path.GetDirectoryName(EquationsFile)!;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "userequations.json.*.dslmigration.bak")) File.Delete(f);

        // A translatable C# body (Complex.* + a member access) and one with no
        // DSL form (an unsupported member the preprocessor can't take).
        SeedFile(
            new UserEquationEntry { Name = "TransA", Source = "return Complex.Pow(z, 2) + c;", Kind = UserEquationKind.UserEquation },
            new UserEquationEntry { Name = "TransB", Source = "return z.Real + c;",             Kind = UserEquationKind.UserEquation },
            new UserEquationEntry { Name = "NoDsl",  Source = "return z.GetType().ToString().Length + c;", Kind = UserEquationKind.UserEquation });

        var store = UserEquationStore.Instance;
        store.Load();

        int converted = UserEquationDslMigration.Run(store);
        Assert.Equal(2, converted);

        // #27 Phase 5a fix — converted entries stay on the live-rendering
        // UserEquation tab; only the source is rewritten to DSL text.
        var a = store.GetByName("TransA")!;
        Assert.Equal(UserEquationKind.UserEquation, a.Kind);
        Assert.DoesNotContain("Complex.", a.Source);   // now DSL text

        var b = store.GetByName("TransB")!;
        Assert.Equal(UserEquationKind.UserEquation, b.Kind);
        Assert.Contains("re(", b.Source);              // z.Real -> re(z)

        var noDsl = store.GetByName("NoDsl")!;
        Assert.Equal(UserEquationKind.UserEquation, noDsl.Kind);   // left editable
        Assert.Equal("return z.GetType().ToString().Length + c;", noDsl.Source);

        // A timestamped snapshot of the pre-migration file exists.
        var backups = Directory.GetFiles(dir, "userequations.json.*dslmigration*.bak");
        Assert.NotEmpty(backups);
        Assert.Contains("Complex.Pow(z, 2)", File.ReadAllText(backups[0])); // original preserved

        // Idempotent: a second run converts nothing.
        Assert.Equal(0, UserEquationDslMigration.Run(store));
    }

    [Fact]
    public void Migration_ConvertedEquation_StillRenders()
    {
        SeedFile(new UserEquationEntry
        {
            Name = "Renders",
            Source = "return Complex.Sin(z) + c;",
            Kind = UserEquationKind.UserEquation,
        });

        var store = UserEquationStore.Instance;
        store.Load();
        Assert.Equal(1, UserEquationDslMigration.Run(store));

        var entry = store.GetByName("Renders")!;
        Assert.Equal(UserEquationKind.UserEquation, entry.Kind); // stays on the live tab
        Assert.DoesNotContain("Complex.", entry.Source);         // source is DSL now

        // The persisted DSL text compiles + runs on the live calculator.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters { UserEquationSource = entry.Source },
        };
        calc.Compile(entry.Source);
        Assert.True(calc.IsCompiled, calc.LastError);
        Assert.True(calc.UsingDsl);
    }

    [Fact]
    public void PowFixRepair_ReTranslatesBadPowerForms_FromBackup()
    {
        string dir = Path.GetDirectoryName(EquationsFile)!;
        Directory.CreateDirectory(dir);
        string powMarker = AppDataPaths.Combine(".userequations-powfix");
        if (File.Exists(powMarker)) File.Delete(powMarker);
        foreach (var f in Directory.GetFiles(dir, "userequations.json.*.dslmigration.bak")) File.Delete(f);

        // Pre-migration backup holds the ORIGINAL C# (negative power of z).
        string backup = Path.Combine(dir, "userequations.json.20260101-000000.dslmigration.bak");
        File.WriteAllText(backup, JsonSerializer.Serialize(new[]
        {
            new UserEquationEntry { Name = "NegPow", Source = "return z * Complex.Pow(z,-3) + c;", Kind = UserEquationKind.UserEquation },
        }, new JsonSerializerOptions { WriteIndented = true }));

        // Current store carries the BAD migrated DSL a prior build produced
        // (1/(z)^3 — NaN at z=0, renders blank).
        SeedFile(new UserEquationEntry { Name = "NegPow", Source = "z * (1/(z)^3) + c", Kind = UserEquationKind.UserEquation });

        var store = UserEquationStore.Instance;
        store.Load();
        UserEquationDslMigration.Run(store);

        var e = store.GetByName("NegPow")!;
        Assert.Contains("pow(", e.Source);                       // re-baked to Complex.Pow form
        Assert.DoesNotContain("1/(z)^3", e.Source.Replace(" ", ""));
        Assert.True(File.Exists(powMarker));

        // Renders finite at the z=0 seed now (was NaN).
        var expr = SandboxExpression.Parse(e.Source);
        var got = expr.EvalStep(System.Numerics.Complex.Zero, new System.Numerics.Complex(0.3, 0.1), 0, expr.NewEnv());
        Assert.False(double.IsNaN(got.Real) || double.IsNaN(got.Imaginary));
    }

    [Fact]
    public void CorrectiveFlip_MovesDslEntriesBackToUserEquationTab()
    {
        // A prior build wrongly flipped migrated equations to Kind=Dsl (an editor
        // tab that doesn't render live). The one-time corrective flips them back
        // to UserEquation so the safe interpreter renders them by default; the
        // DSL source is preserved.
        string marker = AppDataPaths.Combine(".userequations-kindfix");
        if (File.Exists(marker)) File.Delete(marker);

        SeedFile(new UserEquationEntry
        {
            Name = "WasFlipped",
            Source = "sin(z) + c",
            Kind = UserEquationKind.Dsl,
        });

        var store = UserEquationStore.Instance;
        store.Load();
        UserEquationDslMigration.Run(store);

        var e = store.GetByName("WasFlipped")!;
        Assert.Equal(UserEquationKind.UserEquation, e.Kind); // moved to the live tab
        Assert.Equal("sin(z) + c", e.Source);                // DSL source preserved
        Assert.True(File.Exists(marker));                    // one-time marker set

        // The flipped entry renders on the live interpreter.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters { UserEquationSource = e.Source },
        };
        calc.Compile(e.Source);
        Assert.True(calc.IsCompiled, calc.LastError);
        Assert.True(calc.UsingDsl);
    }
}
