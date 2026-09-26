// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #966 — every user JSON store loads per entry (TolerantJsonList): one entry this
// build can't read must not hide the others, must survive a save, and a wholly
// unparseable file must be backed up before it can be overwritten. #964 lost (in
// memory) every user animation to a single other-branch enum value; the same
// all-or-nothing load sat in these twelve stores. Runs under the isolated test data
// root (TestDataRootIsolation) — never the real %APPDATA%.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Services;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class StoreTolerantLoadTests
{
    // ── the helper ───────────────────────────────────────────────────────────

    sealed class Item { public string Name { get; set; } = ""; public int N { get; set; } }

    [Fact]
    public void Helper_skips_and_preserves_unreadable_entries()
    {
        var list = new TolerantJsonList<Item>();
        using var doc = JsonDocument.Parse("""[ {"Name":"a","N":1}, {"Name":"bad","N":"x"}, {"Name":"c","N":3}, null ]""");
        var items = list.ReadArray(doc.RootElement, (JsonSerializerOptions?)null);
        Assert.Equal(new[] { "a", "c" }, items.Select(i => i.Name));
        Assert.Equal(1, list.UnreadableCount);

        string json = list.Serialize(items, null, i => i.Name);
        Assert.Contains("\"bad\"", json);
        Assert.Contains("\"x\"", json);                       // verbatim, not re-shaped

        items.Add(new Item { Name = "BAD", N = 9 });          // readable now owns the name
        Assert.DoesNotContain("\"x\"", list.Serialize(items, null, i => i.Name));
    }

    [Fact]
    public void Helper_backs_up_an_unparseable_file_and_ignores_a_blank_one()
    {
        string dir = Path.Combine(AppDataPaths.Root, "tolerant-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "x.json");
            File.WriteAllText(path, "[ {\"Name\": \"cut");
            var list = new TolerantJsonList<Item>();
            Assert.Empty(list.LoadFile(path, (JsonSerializerOptions?)null));
            var backups = Directory.GetFiles(dir, "x.json.*.unreadable*.bak");
            Assert.Single(backups);
            Assert.Contains("cut", File.ReadAllText(backups[0]));

            File.WriteAllText(path, "   ");
            Assert.Empty(list.LoadFile(path, (JsonSerializerOptions?)null));
            Assert.Single(Directory.GetFiles(dir, "x.json.*.unreadable*.bak"));   // blank = no new backup
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── every store ──────────────────────────────────────────────────────────

    /// <summary>One store under test: its file, the JSON name of its Name property, and
    /// how to load / list names / save / count preserved entries.</summary>
    public sealed record Store(
        string Label, string File, string NameKey, bool Envelope, string ListKey,
        Func<IEnumerable<string>> LoadNames, Action Save, Func<int> Unreadable)
    {
        public override string ToString() => Label;
    }

    static Store Singleton(string label, string file, Action load, Func<IEnumerable<string>> names, Action save, Func<int> unreadable)
        => new(label, file, "Name", false, "", () => { load(); return names(); }, save, unreadable);

    static LightingFxPresetFile? s_fx;
    static SlideshowConfigFile? s_show;
    static WorkspaceLayoutFile? s_ws;
    static LookStore? s_looks;

    public static IEnumerable<object[]> Stores()
    {
        yield return new object[] { Singleton("regions", "regions.json",
            () => FractalRegionLibrary.Instance.Load(), () => FractalRegionLibrary.Instance.UserRegions.Select(r => r.Name),
            () => FractalRegionLibrary.Instance.Save(), () => FractalRegionLibrary.Instance.UnreadableCount) };
        yield return new object[] { Singleton("scenes", "scenes.json",
            () => SceneLibrary.Instance.Load(), () => SceneLibrary.Instance.Scenes.Select(s => s.Name),
            () => SceneLibrary.Instance.Save(), () => SceneLibrary.Instance.UnreadableCount) };
        yield return new object[] { Singleton("themes", "colorthemes.json",
            () => UserColorThemeLibrary.Instance.Load(), () => UserColorThemeLibrary.Instance.Themes.Select(t => t.Name),
            () => UserColorThemeLibrary.Instance.Save(), () => UserColorThemeLibrary.Instance.UnreadableCount) };
        yield return new object[] { Singleton("animations", "animations.json",
            () => AnimationLibrary.Instance.Load(), () => AnimationLibrary.Instance.Animations.Select(a => a.Name),
            () => AnimationLibrary.Instance.Save(), () => AnimationLibrary.Instance.UnreadableCount) };
        yield return new object[] { Singleton("sandbox", "sandboxequations.json",
            () => SandboxEquationStore.Instance.Load(), () => SandboxEquationStore.Instance.Equations.Select(e => e.Name),
            () => SandboxEquationStore.Instance.Save(), () => SandboxEquationStore.Instance.UnreadableCount) };
        yield return new object[] { Singleton("userbulbs", "userbulbs.json",
            () => UserBulbStore.Instance.Load(), () => UserBulbStore.Instance.Equations.Select(e => e.Name),
            () => UserBulbStore.Instance.Save(), () => UserBulbStore.Instance.UnreadableCount) };
        yield return new object[] { Singleton("colorgen", "colorgen.json",
            () => UserColorGenStore.Instance.Load(), () => UserColorGenStore.Instance.Entries.Select(e => e.Name),
            () => UserColorGenStore.Instance.Save(), () => UserColorGenStore.Instance.UnreadableCount) };
        yield return new object[] { Singleton("userequations", "userequations.json",
            () => UserEquationStore.Instance.Load(), () => UserEquationStore.Instance.Equations.Select(e => e.Name),
            () => UserEquationStore.Instance.Save(), () => UserEquationStore.Instance.UnreadableCount) };
        yield return new object[] { Singleton("watermarks", "userwatermarks.json",
            () => UserWatermarkStore.Instance.Load(), () => UserWatermarkStore.Instance.Watermarks.Select(w => w.Name),
            () => UserWatermarkStore.Instance.Save(), () => UserWatermarkStore.Instance.UnreadableCount) };
        yield return new object[] { new Store("lighting-fx", "lighting-fx-presets.json", "name", true, "presets",
            () => { s_fx = LightingFxPresetLibrary.Load(); return s_fx.Presets.Select(p => p.Name); },
            () => LightingFxPresetLibrary.Save(s_fx!), () => s_fx!.Preserved.UnreadableCount) };
        yield return new object[] { new Store("slideshow", "slideshow-configs.json", "name", true, "configs",
            () => { s_show = SlideshowConfigLibrary.Load(); return s_show.Configs.Select(c => c.Name); },
            () => SlideshowConfigLibrary.Save(s_show!), () => s_show!.Preserved.UnreadableCount) };
        yield return new object[] { new Store("workspaces", "window-workspaces.json", "name", true, "layouts",
            () => { s_ws = WorkspaceLayoutLibrary.Load(); return s_ws.Layouts.Select(l => l.Name); },
            () => WorkspaceLayoutLibrary.Save(s_ws!), () => s_ws!.Preserved.UnreadableCount) };
        yield return new object[] { new Store("looks", "palette-looks.json", "Name", false, "",
            () => { s_looks = new LookStore(AppDataPaths.Combine("palette-looks.json")); return s_looks.Load().Select(l => l.Name); },
            () => s_looks!.Save(new FracturingFog.Imaging.Look("SavedMarker",
                new List<(byte r, byte g, byte b)> { (1, 2, 3), (4, 5, 6) },
                new FracturingFog.Imaging.LookMaterial(0.5f, 0f),
                new FracturingFog.Imaging.LookLighting((9, 9, 9), (8, 8, 8)))), () => 0) };
    }

    static string Body(Store s, string entries)
        => s.Envelope ? $$"""{ "activeName": "Alpha", "{{s.ListKey}}": [ {{entries}} ] }""" : $"[ {entries} ]";

    // A readable entry, and one whose Name is the wrong JSON type (throws for every store).
    static string Good(Store s, string name) => $$"""{ "{{s.NameKey}}": "{{name}}" }""";
    static string Poison(Store s) => $$"""{ "{{s.NameKey}}": [ "PoisonMarker-966" ] }""";

    static void WithFile(Store s, string json, Action body)
    {
        string path = AppDataPaths.Combine(s.File);
        Directory.CreateDirectory(AppDataPaths.Root);
        File.WriteAllText(path, json);
        try { body(); }
        finally
        {
            foreach (var f in Directory.GetFiles(AppDataPaths.Root, s.File + "*")) File.Delete(f);
            try { s.LoadNames(); } catch { }       // leave the singleton on an empty store
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void One_unreadable_entry_does_not_hide_the_others_and_survives_save(Store s)
    {
        WithFile(s, Body(s, $"{Good(s, "Alpha")}, {Poison(s)}, {Good(s, "Omega")}"), () =>
        {
            var names = s.LoadNames().ToList();
            Assert.Contains("Alpha", names);
            Assert.Contains("Omega", names);                 // entries AFTER the bad one used to vanish
            if (s.Label != "looks") Assert.Equal(1, s.Unreadable());

            s.Save();
            string saved = File.ReadAllText(AppDataPaths.Combine(s.File));
            Assert.Contains("PoisonMarker-966", saved);      // written back verbatim, not deleted
            Assert.Contains("Omega", saved);

            var again = s.LoadNames().ToList();              // and the file still loads
            Assert.Contains("Omega", again);
        });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void Unparseable_file_is_backed_up_before_it_can_be_overwritten(Store s)
    {
        WithFile(s, "[ { \"Name\": \"TruncatedMarker-966\", ", () =>
        {
            s.LoadNames();
            s.Save();                                        // what used to destroy the only copy
            var backups = Directory.GetFiles(AppDataPaths.Root, s.File + ".*.unreadable*.bak");
            Assert.NotEmpty(backups);
            Assert.Contains("TruncatedMarker-966", File.ReadAllText(backups[0]));
        });
    }
}
