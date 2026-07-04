using System;
using System.Linq;
using FracturingFog.Hosting;
using FracturingFog.Models;
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
}
