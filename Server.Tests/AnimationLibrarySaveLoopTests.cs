using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Phase 3c deliverable: the editor's Save loop runs through
/// <see cref="AnimationLibrary.ReplaceOrAdd"/>. These tests cover the
/// idempotent-name contract <see cref="AnimationEditorViewModel.SaveAsync"/>
/// relies on (re-save under the same name updates in place; new name adds)
/// and the FractalRegion+AnimationName attach contract the Save Region
/// dialog writes.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class AnimationLibrarySaveLoopTests
{
    private static AnimationData MakeTestAnimation(string name, double freq = 0.1) => new()
    {
        Name = name,
        Description = "Phase 3c save-loop test",
        Category = "User",
        TargetFractalTypes = new List<FractalType> { FractalType.Julia },
        Tracks = new List<AnimationTrack>
        {
            new()
            {
                ParamName = "JuliaC",
                Mode = AnimationMode.Sine,
                Min = 0.1,
                Max = 0.5,
                FrequencyHz = freq,
                Enabled = true,
            },
        },
    };

    /// <summary>Re-saving under the same name updates the existing entry
    /// rather than duplicating. The editor relies on this — every Save
    /// after the first one would otherwise grow the library indefinitely.</summary>
    [Fact]
    public void ReplaceOrAdd_SameName_UpdatesInPlace()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        string name = $"FF-Phase3c-Test-{Guid.NewGuid():N}";
        int baseline = lib.Animations.Count;

        try
        {
            Assert.True(lib.ReplaceOrAdd(MakeTestAnimation(name, freq: 0.1)));
            Assert.Equal(baseline + 1, lib.Animations.Count);
            Assert.Equal(0.1, lib.GetByName(name)!.Tracks[0].FrequencyHz);

            // Re-save under the same name with a different field. Count
            // must NOT grow; the field must update.
            Assert.True(lib.ReplaceOrAdd(MakeTestAnimation(name, freq: 0.9)));
            Assert.Equal(baseline + 1, lib.Animations.Count);
            Assert.Equal(0.9, lib.GetByName(name)!.Tracks[0].FrequencyHz);
        }
        finally
        {
            lib.Remove(name);
        }
    }

    /// <summary>EnumerateNames-style listing surfaces the freshly saved
    /// animation so the Save Region dropdown and editor Load combo see
    /// it on next open.</summary>
    [Fact]
    public void Animations_EnumerationIncludesSavedEntry()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        string name = $"FF-Phase3c-Enum-{Guid.NewGuid():N}";

        try
        {
            Assert.True(lib.ReplaceOrAdd(MakeTestAnimation(name)));
            var names = lib.Animations.Select(a => a.Name).ToList();
            Assert.Contains(name, names);
        }
        finally
        {
            lib.Remove(name);
        }
    }

    /// <summary>FractalRegion + AnimationName end-to-end through the
    /// FractalRegionLibrary singleton: AddUserRegion stamps + persists,
    /// FindByName returns it with the AnimationName carried through, the
    /// Save Region dialog's downstream consumer
    /// <c>HostColorThemeService.GetRegionAnimationName</c> reads exactly
    /// that field.</summary>
    [Fact]
    public void FractalRegion_WithAnimationName_PersistsThroughLibrary()
    {
        var regions = FractalRegionLibrary.Instance;
        var anims = AnimationLibrary.Instance;
        anims.Load();

        string regionName = $"FF-Phase3c-Region-{Guid.NewGuid():N}";
        string animName = $"FF-Phase3c-Anim-{Guid.NewGuid():N}";

        try
        {
            Assert.True(anims.ReplaceOrAdd(MakeTestAnimation(animName)));

            var region = new FractalRegion
            {
                Name = regionName,
                CenterX = -0.5,
                CenterY = 0.0,
                Zoom = 1.0,
                Iterations = 256,
                FractalType = FractalType.Julia,
                AnimationName = animName,
            };
            Assert.True(regions.AddUserRegion(region));

            var loaded = regions.FindByName(regionName);
            Assert.NotNull(loaded);
            Assert.Equal(animName, loaded!.AnimationName);
        }
        finally
        {
            regions.RemoveUserRegion(regionName);
            anims.Remove(animName);
        }
    }
}
