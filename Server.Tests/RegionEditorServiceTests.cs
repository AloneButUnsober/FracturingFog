// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Hosting;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Animation Roadmap Sub-goal B (Region Editor) — Phase R0 service layer.
/// Covers the metadata-preserving edit contract exposed by
/// <see cref="HostColorThemeService.GetRegionForEdit"/> and
/// <see cref="HostColorThemeService.UpdateRegionMetadata"/>:
///   • geometry is preserved across a metadata edit (no live-view recapture),
///   • rename moves the entry (old name gone, new present),
///   • editing a built-in clones into a user region, leaving the built-in,
///   • a rename that collides with a different region is refused,
///   • keep/clear toggles for the embedded watermark are honoured.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class RegionEditorServiceTests
{
    private static FractalRegion MakeUserRegion(string name) => new()
    {
        Name = name,
        CenterX = -0.743643887037151,
        CenterXLo = 1.2345e-17,
        CenterY = 0.131825904205330,
        Zoom = 12345.0,
        Iterations = 1777,
        FractalType = FractalType.Mandelbrot,
        Description = "original description",
    };

    [Fact]
    public void GetRegionForEdit_UserRegion_EchoesGeometryAndMetadata()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-RegEdit-Get-{Guid.NewGuid():N}";

        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(name)));

            var model = svc.GetRegionForEdit(name);
            Assert.NotNull(model);
            Assert.False(model!.IsBuiltIn);
            Assert.Equal(name, model.OriginalName);
            Assert.Equal(name, model.Name);
            Assert.Equal("original description", model.Description);
            Assert.Equal("Mandelbrot", model.FractalTypeName);
            Assert.Equal(12345.0, model.Zoom);
            Assert.Equal(1777, model.Iterations);
            Assert.False(model.HasEmbeddedWatermark);
            Assert.False(model.HasLightingOverride);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void GetRegionForEdit_UnknownName_ReturnsNull()
    {
        var svc = new HostColorThemeService();
        Assert.Null(svc.GetRegionForEdit($"FF-RegEdit-Missing-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void UpdateRegionMetadata_InPlace_PreservesGeometry()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-RegEdit-InPlace-{Guid.NewGuid():N}";

        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(name)));

            var model = svc.GetRegionForEdit(name)!;
            model.Description = "edited description";
            model.AnimationName = "some-animation";

            var res = svc.UpdateRegionMetadata(model);
            Assert.True(res.Success);
            Assert.False(res.Cloned);
            Assert.Equal(name, res.SavedName);

            var saved = lib.FindByName(name);
            Assert.NotNull(saved);
            // Metadata updated…
            Assert.Equal("edited description", saved!.Description);
            Assert.Equal("some-animation", saved.AnimationName);
            // …geometry preserved bit-for-bit (NOT recaptured from a live view).
            Assert.Equal(-0.743643887037151, saved.CenterX);
            Assert.Equal(1.2345e-17, saved.CenterXLo);
            Assert.Equal(12345.0, saved.Zoom);
            Assert.Equal(1777, saved.Iterations);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void UpdateRegionMetadata_Rename_MovesEntry()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-RegEdit-Rename-{Guid.NewGuid():N}";
        string renamed = name + "-v2";

        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(name)));

            var model = svc.GetRegionForEdit(name)!;
            model.Name = renamed;

            var res = svc.UpdateRegionMetadata(model);
            Assert.True(res.Success);
            Assert.Equal(renamed, res.SavedName);

            Assert.Null(lib.UserRegions.FirstOrDefault(r =>
                string.Equals(r.Name, name, StringComparison.Ordinal)));
            var moved = lib.FindByName(renamed);
            Assert.NotNull(moved);
            Assert.Equal(1777, moved!.Iterations); // geometry rode along
        }
        finally
        {
            lib.RemoveUserRegion(name);
            lib.RemoveUserRegion(renamed);
        }
    }

    [Fact]
    public void UpdateRegionMetadata_BuiltIn_ClonesLeavingOriginal()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string builtInName = "Classic Full View"; // ships in _builtIns
        string cloneName = $"FF-RegEdit-Clone-{Guid.NewGuid():N}";

        // Sanity: the built-in exists and is immutable.
        var builtIn = lib.FindByName(builtInName);
        Assert.NotNull(builtIn);
        Assert.True(builtIn!.IsBuiltIn);

        try
        {
            var model = svc.GetRegionForEdit(builtInName)!;
            Assert.True(model.IsBuiltIn);
            model.Name = cloneName;
            model.Description = "my clone";

            var res = svc.UpdateRegionMetadata(model);
            Assert.True(res.Success);
            Assert.True(res.Cloned);
            Assert.Equal(cloneName, res.SavedName);

            // Built-in untouched.
            var stillBuiltIn = lib.FindByName(builtInName);
            Assert.NotNull(stillBuiltIn);
            Assert.True(stillBuiltIn!.IsBuiltIn);

            // Clone is a user region carrying the built-in's geometry.
            var clone = lib.UserRegions.FirstOrDefault(r =>
                string.Equals(r.Name, cloneName, StringComparison.Ordinal));
            Assert.NotNull(clone);
            Assert.Equal("my clone", clone!.Description);
            Assert.Equal(builtIn.CenterX, clone.CenterX);
            Assert.Equal(builtIn.Zoom, clone.Zoom);
        }
        finally { lib.RemoveUserRegion(cloneName); }
    }

    [Fact]
    public void UpdateRegionMetadata_RenameCollision_Refused()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string a = $"FF-RegEdit-CollA-{Guid.NewGuid():N}";
        string b = $"FF-RegEdit-CollB-{Guid.NewGuid():N}";

        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(a)));
            Assert.True(lib.AddUserRegion(MakeUserRegion(b)));

            var model = svc.GetRegionForEdit(a)!;
            model.Name = b; // collide with the other region

            var res = svc.UpdateRegionMetadata(model);
            Assert.False(res.Success);
            Assert.NotNull(res.ErrorMessage);
            // Both originals still present, untouched.
            Assert.NotNull(lib.FindByName(a));
            Assert.NotNull(lib.FindByName(b));
        }
        finally
        {
            lib.RemoveUserRegion(a);
            lib.RemoveUserRegion(b);
        }
    }

    [Fact]
    public void UpdateRegionMetadata_RecaptureFromLiveView_ReframesGeometryKeepsMetadata()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-RegEdit-Recap-{Guid.NewGuid():N}";

        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(name)));

            var model = svc.GetRegionForEdit(name)!;
            model.Description = "retagged while reframing";

            // Live view sitting somewhere completely different from the stored
            // geometry. Phase R3 "Capture current view" re-snaps geometry from
            // this while still applying the edited metadata.
            var live = new FractalViewState
            {
                CenterX = 0.360240443437614,
                CenterY = -0.641313061064803,
                Zoom = 987654.0,
                PreferredIterations = 4242,
                FractalType = FractalType.Mandelbrot,
            };

            var res = svc.UpdateRegionMetadata(model, live);
            Assert.True(res.Success);
            Assert.Equal(name, res.SavedName);

            var saved = lib.FindByName(name)!;
            // Metadata applied…
            Assert.Equal("retagged while reframing", saved.Description);
            // …geometry re-framed from the live view (NOT the stored 12345/1777).
            Assert.Equal(0.360240443437614, saved.CenterX);
            Assert.Equal(-0.641313061064803, saved.CenterY);
            Assert.Equal(987654.0, saved.Zoom);
            Assert.Equal(4242, saved.Iterations);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void Save_IsAtomic_KeepsRollbackBackupAndNoTempLeftover()
    {
        var lib = FractalRegionLibrary.Instance;
        // Runs under the test data-root redirect (TestDataRootIsolation), so
        // this path points at a throwaway temp dir, never the real user file.
        string file = AppDataPaths.Combine("regions.json");
        string bak  = file + ".bak";
        string tmp  = file + ".tmp";
        string a = $"FF-RegEdit-Atomic-A-{Guid.NewGuid():N}";
        string b = $"FF-RegEdit-Atomic-B-{Guid.NewGuid():N}";

        try
        {
            // First save creates the file; a second save must swap atomically,
            // moving the prior good copy aside to regions.json.bak.
            Assert.True(lib.AddUserRegion(MakeUserRegion(a)));
            Assert.True(lib.AddUserRegion(MakeUserRegion(b)));

            Assert.True(File.Exists(file));
            Assert.True(File.Exists(bak), "atomic swap should leave a .bak rollback copy");
            Assert.False(File.Exists(tmp), "temp file must not linger after a successful swap");

            // The rollback copy is the previous good state — before B existed.
            string bakJson = File.ReadAllText(bak);
            Assert.Contains(a, bakJson);
            Assert.DoesNotContain(b, bakJson);
        }
        finally
        {
            lib.RemoveUserRegion(a);
            lib.RemoveUserRegion(b);
        }
    }

    [Fact]
    public void UpdateRegionMetadata_ClearWatermark_DropsEmbed()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-RegEdit-Wm-{Guid.NewGuid():N}";

        try
        {
            var region = MakeUserRegion(name);
            region.EmbeddedWatermark = new WatermarkDef { Text = "© test" };
            Assert.True(lib.AddUserRegion(region));

            var model = svc.GetRegionForEdit(name)!;
            Assert.True(model.HasEmbeddedWatermark);
            model.KeepEmbeddedWatermark = false; // clear it

            var res = svc.UpdateRegionMetadata(model);
            Assert.True(res.Success);
            Assert.Null(lib.FindByName(name)!.EmbeddedWatermark);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void CuratedThemeOnly_RoundTrips_Echoes_And_Resolves()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-Curated-{Guid.NewGuid():N}";
        string theme = svc.EnumerateThemeNames().First();

        try
        {
            var r = MakeUserRegion(name);
            r.CuratedThemes = new System.Collections.Generic.List<string> { theme };
            r.UseCuratedThemesOnly = true;
            Assert.True(lib.AddUserRegion(r));

            // Echoed into the edit model.
            var model = svc.GetRegionForEdit(name)!;
            Assert.True(model.UseCuratedThemesOnly);

            // Resolves for the recall path.
            Assert.True(svc.TryGetRegionCuratedThemeToApply(name, out var resolved));
            Assert.Equal(theme, resolved);

            // Persists across an in-place edit.
            var res = svc.UpdateRegionMetadata(model);
            Assert.True(res.Success);
            Assert.True(lib.FindByName(name)!.UseCuratedThemesOnly);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void CuratedThemeOnly_Off_Or_EmptyList_DoesNotResolve()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-Curated-Off-{Guid.NewGuid():N}";

        try
        {
            // Flag off (default) → recall leaves the active theme alone.
            var r = MakeUserRegion(name);
            r.CuratedThemes = new System.Collections.Generic.List<string> { svc.EnumerateThemeNames().First() };
            Assert.True(lib.AddUserRegion(r));
            Assert.False(svc.TryGetRegionCuratedThemeToApply(name, out _));

            // Flag on but no curated whitelist → the service drops the flag on
            // save, and nothing resolves.
            var model = svc.GetRegionForEdit(name)!;
            model.UseCuratedThemesOnly = true;
            model.CuratedThemes = null;
            Assert.True(svc.UpdateRegionMetadata(model).Success);
            Assert.False(lib.FindByName(name)!.UseCuratedThemesOnly);
            Assert.False(svc.TryGetRegionCuratedThemeToApply(name, out _));
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void BuiltIn_AcidWarp_Regions_Are_CuratedThemeOnly()
    {
        var lib = FractalRegionLibrary.Instance;
        foreach (var n in new[] { "Acid Fog - Rings", "Acid Fog - Classic" })
        {
            var r = lib.All.First(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            Assert.True(r.UseCuratedThemesOnly, $"{n} should default to curated-theme-only");
            Assert.NotNull(r.CuratedThemes);
            Assert.Contains("Acid Fog Spectrum", r.CuratedThemes!);
        }
    }

    [Fact]
    public void AcidWarp_To_AcidFog_Rename_Has_BackCompat_Aliases()
    {
        // #250 — user-facing "Acid Warp" -> "Acid Fog". Old saved references
        // (regions, curated themes) must still resolve forward to the new names.
        Assert.Equal("Acid Fog - Rings",   LegacyNameAliases.Resolve("Acid Warp - Rings"));
        Assert.Equal("Acid Fog - Classic", LegacyNameAliases.Resolve("Acid Warp - Classic"));
        Assert.Equal("Acid Fog Spectrum",  LegacyNameAliases.Resolve("Acid Warp Spectrum"));

        // A stale region recall (curated theme under the OLD name) still resolves.
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-LegacyCurated-{Guid.NewGuid():N}";
        try
        {
            var r = MakeUserRegion(name);
            r.CuratedThemes = new System.Collections.Generic.List<string> { "Acid Warp Spectrum" };
            r.UseCuratedThemesOnly = true;
            Assert.True(lib.AddUserRegion(r));

            Assert.True(svc.TryGetRegionCuratedThemeToApply(name, out var resolved));
            Assert.Equal("Acid Fog Spectrum", resolved); // forwarded to the current name
        }
        finally { lib.RemoveUserRegion(name); }
    }

    // ── Smoke #2: per-region Cycle (palette-rotation) toggle ──────────────────

    private static FractalRegion MakeAcidFogRegion(string name) => new()
    {
        Name = name,
        CenterX = 0.0, CenterY = 0.0, Zoom = 1.0, Iterations = 64,
        FractalType = FractalType.AcidWarp,
        Description = "acid fog",
    };

    [Fact]
    public void CycleToggle_RoundTrips_On_AcidFog_Region()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-Cycle-{Guid.NewGuid():N}";
        try
        {
            var r = MakeAcidFogRegion(name);
            r.PaletteCycleEnabled = true;
            Assert.True(lib.AddUserRegion(r));

            // Editor echoes the saved value; user turns cycling off and saves.
            var model = svc.GetRegionForEdit(name)!;
            Assert.True(model.CycleEnabled);
            model.CycleEnabled = false;
            Assert.True(svc.UpdateRegionMetadata(model).Success);

            Assert.False(lib.FindByName(name)!.PaletteCycleEnabled);
            Assert.False(svc.GetRegionCycleEnabled(name));
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void CycleToggle_Echoes_TypeDefault_When_Region_Has_No_Opinion()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string acid = $"FF-CycleDefA-{Guid.NewGuid():N}";
        string mandel = $"FF-CycleDefM-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(MakeAcidFogRegion(acid)));   // null cycle
            Assert.True(lib.AddUserRegion(MakeUserRegion(mandel)));    // null cycle

            // No stored opinion → service returns null; the editor model shows
            // the type default (on for Acid Fog, off for a static fractal).
            Assert.Null(svc.GetRegionCycleEnabled(acid));
            Assert.Null(svc.GetRegionCycleEnabled(mandel));
            Assert.True(svc.GetRegionForEdit(acid)!.CycleEnabled);
            Assert.False(svc.GetRegionForEdit(mandel)!.CycleEnabled);
        }
        finally { lib.RemoveUserRegion(acid); lib.RemoveUserRegion(mandel); }
    }

    [Fact]
    public void CycleToggle_Not_Persisted_On_NonAcidFog_Region()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-CycleMandel-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(MakeUserRegion(name)));
            var model = svc.GetRegionForEdit(name)!;
            model.CycleEnabled = true; // meaningless on a Mandelbrot region
            Assert.True(svc.UpdateRegionMetadata(model).Success);

            // Dropped to null so recall uses the type default, never spinning the
            // LUT on a fractal that isn't meant to cycle.
            Assert.Null(lib.FindByName(name)!.PaletteCycleEnabled);
            Assert.Null(svc.GetRegionCycleEnabled(name));
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void BuiltIn_AcidFog_Regions_Enable_Cycle_By_Default()
    {
        var lib = FractalRegionLibrary.Instance;
        foreach (var n in new[] { "Acid Fog - Rings", "Acid Fog - Classic" })
        {
            var r = lib.All.First(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            Assert.True(r.PaletteCycleEnabled, $"{n} should default to cycle on");
        }
    }
}
