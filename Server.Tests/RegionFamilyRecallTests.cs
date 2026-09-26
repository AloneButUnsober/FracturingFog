// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #960 (#941 S1) — region recall is authoritative per family. RegionFractalParams.ApplyTo
// is an overlay and Snapshot omits fields at their default, so recall used to leave
// whatever was live (an animation, the previous region) in place: a plain Julia region
// after a faithful-implosion region kept faithful mode on; a default-seed dual-orbit
// region kept an animated seed. FractalRegion.ApplyFamilyParams resets the family's
// params to defaults first, then overlays.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegionFamilyRecallTests
{
    static FractalRegion Region(FractalType type, RegionFractalParams? rp) => new()
    {
        Name = "t", FractalType = type, CenterX = 0, CenterY = 0, Zoom = 1, Params = rp,
    };

    [Fact]
    public void Plain_julia_region_turns_faithful_implosion_off()
    {
        // The reported leak: a "Parabolic implosion" region engages faithful mode; a plain
        // Julia region's snapshot omits the flag (null) — previously it stayed on.
        var p = new FractalParameters
        {
            FaithfulImplosion = true, FaithfulImplosionP = 1, FaithfulImplosionQ = 3,
            FaithfulImplosionApproach = 0.05, FaithfulImplosionParentPath = "1/2",
        };
        var plain = Region(FractalType.Julia, new RegionFractalParams { JuliaCRe = -0.8, JuliaCIm = 0.156 });
        plain.ApplyFamilyParams(p);

        var d = new FractalParameters();
        Assert.False(p.FaithfulImplosion);
        Assert.Equal(d.FaithfulImplosionApproach, p.FaithfulImplosionApproach);
        Assert.Equal(d.FaithfulImplosionQ, p.FaithfulImplosionQ);
        Assert.Equal(d.FaithfulImplosionParentPath, p.FaithfulImplosionParentPath);
        Assert.Equal(new Complex(-0.8, 0.156), p.JuliaC);
    }

    [Fact]
    public void Default_seed_dual_orbit_region_restores_animated_seeds()
    {
        // Snapshot omits the seeds at their defaults (0.5 / 0 / 0.3 / 0) — an animated
        // seed used to survive recall of a default-seed region.
        var p = new FractalParameters
        {
            DualOrbitCSeedX = 0.91, DualOrbitCSeedY = -0.4, DualOrbitCSeedZ = 0.77, DualOrbitSZ = 0.2,
            DualOrbitCEqualsS = true,
        };
        var region = Region(FractalType.DualOrbitEscape,
            RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, new FractalParameters()));
        region.ApplyFamilyParams(p);

        Assert.Equal(0.5, p.DualOrbitCSeedX);
        Assert.Equal(0.0, p.DualOrbitCSeedY);
        Assert.Equal(0.3, p.DualOrbitCSeedZ);
        Assert.Equal(0.0, p.DualOrbitSZ);
        Assert.False(p.DualOrbitCEqualsS);
    }

    [Fact]
    public void Legacy_region_without_params_gets_family_defaults()
    {
        var p = new FractalParameters { JuliaC = new Complex(0.3, 0.6), FaithfulImplosion = true };
        Region(FractalType.Julia, null).ApplyFamilyParams(p);
        var d = new FractalParameters();
        Assert.Equal(d.JuliaC, p.JuliaC);
        Assert.False(p.FaithfulImplosion);
    }

    [Fact]
    public void Recall_leaves_other_families_and_shared_state_alone()
    {
        var p = new FractalParameters
        {
            BulbPower = 11, MandelboxScale = -1.7, DualOrbitCSeedX = 0.9, QJuliaCX = 0.42,
        };
        var lighting = p.Lighting;
        Region(FractalType.Julia, new RegionFractalParams { JuliaCRe = 0.1, JuliaCIm = 0.2 }).ApplyFamilyParams(p);
        Assert.Equal(11, p.BulbPower);
        Assert.Equal(-1.7, p.MandelboxScale);
        Assert.Equal(0.9, p.DualOrbitCSeedX);
        Assert.Equal(0.42, p.QJuliaCX);
        Assert.Equal(lighting, p.Lighting);
    }

    [Fact]
    public void Family_properties_cover_mode_gated_fields_and_nothing_foreign()
    {
        var julia = RegionFractalParams.FamilyProperties(FractalType.Julia).Select(pi => pi.Name).ToHashSet();
        // captured only while faithful mode / domain warp is engaged — found via the engaged base
        Assert.Contains(nameof(FractalParameters.JuliaC), julia);
        Assert.Contains(nameof(FractalParameters.FaithfulImplosion), julia);
        Assert.Contains(nameof(FractalParameters.FaithfulImplosionApproach), julia);
        Assert.Contains(nameof(FractalParameters.FaithfulImplosionParentPath), julia);
        Assert.Contains(nameof(FractalParameters.DomainWarpStrength), julia);
        Assert.DoesNotContain(nameof(FractalParameters.BulbPower), julia);
        Assert.DoesNotContain(nameof(FractalParameters.Lighting), julia);

        var dual = RegionFractalParams.FamilyProperties(FractalType.DualOrbitEscape).Select(pi => pi.Name).ToHashSet();
        Assert.Contains(nameof(FractalParameters.DualOrbitCSeedX), dual);
        Assert.Contains(nameof(FractalParameters.DualOrbitCEqualsS), dual);
        Assert.DoesNotContain(nameof(FractalParameters.JuliaC), dual);

        var kleinian = RegionFractalParams.FamilyProperties(FractalType.Kleinian).Select(pi => pi.Name).ToHashSet();
        Assert.Contains(nameof(FractalParameters.KleinianCustomSpheres), kleinian);
        Assert.Contains(nameof(FractalParameters.KleinianRotationAxisX), kleinian);   // gated on angle ≠ 0

        // user-code families own no generic block
        Assert.DoesNotContain(RegionFractalParams.FamilyProperties(FractalType.UserBulb),
            pi => pi.Name.StartsWith("UserBulb", StringComparison.Ordinal));
    }

    [Fact]
    public void Kleinian_custom_spheres_do_not_leak_into_a_preset_region()
    {
        var p = new FractalParameters();
        p.KleinianCustomSpheres.Add(new KleinianSphereDef());
        Region(FractalType.Kleinian, RegionFractalParams.Snapshot(FractalType.Kleinian, new FractalParameters()))
            .ApplyFamilyParams(p);
        Assert.Empty(p.KleinianCustomSpheres);
    }

    [Fact]
    public void Built_in_region_recall_is_path_independent()
    {
        // For every built-in region: recalling it over a "dirty" live state (every family
        // param moved off its default, every mode engaged) gives the same family params as
        // recalling it over fresh params — i.e. what was live before no longer matters.
        int checkedRegions = 0;
        foreach (var region in FractalRegionLibrary.Instance.BuiltIns)
        {
            var props = RegionFractalParams.FamilyProperties(region.FractalType);
            if (props.Count == 0) continue;

            var fresh = new FractalParameters();
            region.ApplyFamilyParams(fresh);

            var dirty = new FractalParameters();
            foreach (var pi in props) TryDirty(pi, dirty);
            region.ApplyFamilyParams(dirty);

            foreach (var pi in props)
                Assert.True(ValueEquals(pi.GetValue(fresh), pi.GetValue(dirty)),
                    $"{region.Name} ({region.FractalType}).{pi.Name}: fresh {pi.GetValue(fresh)} vs after-dirty {pi.GetValue(dirty)}");
            checkedRegions++;
        }
        Assert.True(checkedRegions >= 10, $"only {checkedRegions} built-in regions checked");
    }

    static void TryDirty(PropertyInfo pi, FractalParameters p)
    {
        try
        {
            object? v = pi.GetValue(p);
            object? nv = v switch
            {
                double d => d + 0.123,
                float f => f + 0.123f,
                int i => i + 1,
                long l => l + 1,
                bool => true,
                string s => s + "x",
                Complex c => c + new Complex(0.11, -0.07),
                Enum e => Enum.GetValues(e.GetType()).GetValue(
                    (Array.IndexOf(Enum.GetValues(e.GetType()), e) + 1) % Enum.GetValues(e.GetType()).Length),
                _ => v,
            };
            pi.SetValue(p, nv);
        }
        catch { }
    }

    static bool ValueEquals(object? a, object? b)
    {
        if (a is System.Collections.IList la && b is System.Collections.IList lb)
            return la.Count == lb.Count;
        return Equals(a, b);
    }
}
