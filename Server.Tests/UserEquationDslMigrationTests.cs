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

        var a = store.GetByName("TransA")!;
        Assert.Equal(UserEquationKind.Dsl, a.Kind);
        Assert.DoesNotContain("Complex.", a.Source);   // now DSL text

        var b = store.GetByName("TransB")!;
        Assert.Equal(UserEquationKind.Dsl, b.Kind);
        Assert.Contains("re(", b.Source);              // z.Real -> re(z)

        var noDsl = store.GetByName("NoDsl")!;
        Assert.Equal(UserEquationKind.UserEquation, noDsl.Kind);   // left editable
        Assert.Equal("return z.GetType().ToString().Length + c;", noDsl.Source);

        // A timestamped snapshot of the pre-migration file exists.
        string dir = Path.GetDirectoryName(EquationsFile)!;
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
        Assert.Equal(UserEquationKind.Dsl, entry.Kind);

        // The persisted DSL text compiles + runs on the live calculator.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters { UserEquationSource = entry.Source },
        };
        calc.Compile(entry.Source);
        Assert.True(calc.IsCompiled, calc.LastError);
        Assert.True(calc.UsingDsl);
    }
}
