// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #964 — one unreadable entry in animations.json (here: a FractalType this build
// doesn't know, as written by an other-branch build) used to throw out of the
// single List<AnimationData> deserialize and clear EVERY user animation; the next
// Save then deleted them from disk. Load is now per-entry and preserves what it
// can't read. Runs under the isolated test data root (TestDataRootIsolation).

using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class AnimationLibraryTolerantLoadTests
{
    static string FilePath => AppDataPaths.Combine("animations.json");

    static string Entry(string name, string type) => $$"""
        {
          "Name": "{{name}}",
          "Category": "User",
          "TargetFractalTypes": [ "{{type}}" ],
          "Tracks": [ { "ParamName": "DualOrbitCSeedX", "Mode": "Sine", "Min": 0.2, "Max": 0.8, "FrequencyHz": 0.1, "Enabled": true } ]
        }
        """;

    static void WithFile(string json, System.Action body)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        File.WriteAllText(FilePath, json);
        try { body(); }
        finally
        {
            File.Delete(FilePath);
            foreach (var f in Directory.GetFiles(AppDataPaths.Root, "animations.json.*.unreadable*.bak")) File.Delete(f);
            AnimationLibrary.Instance.Load();
        }
    }

    [Fact]
    public void Unknown_fractal_type_entry_does_not_hide_the_others()
    {
        WithFile($"[{Entry("Before", "DualOrbitEscape")},{Entry("Future", "SemigroupJulia")},{Entry("After", "DualOrbitEscape")}]", () =>
        {
            var lib = AnimationLibrary.Instance;
            lib.Load();
            Assert.NotNull(lib.GetByName("Before"));
            Assert.NotNull(lib.GetByName("After"));        // entries AFTER the bad one were lost before #964
            Assert.Null(lib.GetByName("Future"));
            Assert.Equal(1, lib.UnreadableCount);
            Assert.NotNull(lib.GetByName("Julia C orbit")); // built-ins still merged
        });
    }

    [Fact]
    public void Save_round_trips_the_unreadable_entry_verbatim()
    {
        WithFile($"[{Entry("Keep", "DualOrbitEscape")},{Entry("Future", "SemigroupJulia")}]", () =>
        {
            var lib = AnimationLibrary.Instance;
            lib.Load();
            lib.Save();

            string saved = File.ReadAllText(FilePath);
            Assert.Contains("\"Future\"", saved);
            Assert.Contains("SemigroupJulia", saved);

            lib.Load();                                     // and it still loads the rest
            Assert.NotNull(lib.GetByName("Keep"));
            Assert.Equal(1, lib.UnreadableCount);
        });
    }

    [Fact]
    public void Readable_animation_with_the_same_name_replaces_the_unreadable_one()
    {
        WithFile($"[{Entry("Clash", "SemigroupJulia")}]", () =>
        {
            var lib = AnimationLibrary.Instance;
            lib.Load();
            var mine = new Abstractions.Animation.AnimationData { Name = "Clash", Category = "User" };
            Assert.True(lib.ReplaceOrAdd(mine));
            string saved = File.ReadAllText(FilePath);
            Assert.DoesNotContain("SemigroupJulia", saved);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(saved, "\"Clash\""));
        });
    }

    [Fact]
    public void Unparseable_file_is_backed_up_before_it_can_be_overwritten()
    {
        WithFile("[ { \"Name\": \"Truncated\", ", () =>
        {
            var lib = AnimationLibrary.Instance;
            lib.Load();
            Assert.NotNull(lib.GetByName("Julia C orbit"));
            var backups = Directory.GetFiles(AppDataPaths.Root, "animations.json.*.unreadable*.bak");
            Assert.Single(backups);
            Assert.Contains("Truncated", File.ReadAllText(backups[0]));
        });
    }
}
