// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Bug: the User-Equation dialog's per-equation render settings — Escape r (#541),
// z0 seed (#542), Convergence bailout (#544) and Colour interior / orbit (#583) —
// were NOT saved with the equation, so a Save lost them and selecting a different
// saved equation never restored (or reset) them. Root cause: UserEquationEntry
// carried only Name/Source/Promoted/Kind. These tests pin the persistence
// contract: SaveEquation stores the four settings, they round-trip through disk,
// and legacy JSON (missing the fields) loads as the auto/blank/off defaults (so a
// selection RESETS them). AppDataPaths is redirected to a temp dir for the test
// process (TestDataRootIsolation), so this never touches real user data.

using System.Text.Json;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserEquationRenderSettingsPersistenceTests
{
    [Fact]
    public void SaveEquation_PersistsRenderSettings_AndRoundTripsThroughDisk()
    {
        var store = UserEquationStore.Instance;
        const string name = "RenderSettingsRoundTrip_UEBUG";
        try
        {
            var saved = store.SaveEquation(
                name, "z*z + c", UserEquationKind.UserEquation,
                escapeRadius: 128.0, seed: "c", bailoutCondition: "prev == z", colorInterior: true);
            Assert.NotNull(saved);

            // Re-read from disk to prove the JSON carries the fields.
            store.Load();
            var e = store.GetByName(name);
            Assert.NotNull(e);
            Assert.Equal(128.0, e!.EscapeRadius);
            Assert.Equal("c", e.Seed);
            Assert.Equal("prev == z", e.BailoutCondition);
            Assert.True(e.ColorInterior);
        }
        finally { store.Remove(name); }
    }

    // Overwriting an existing entry updates the settings too (a Save after tweaking
    // Escape r must not keep the old value).
    [Fact]
    public void SaveEquation_Overwrite_UpdatesRenderSettings()
    {
        var store = UserEquationStore.Instance;
        const string name = "RenderSettingsOverwrite_UEBUG";
        try
        {
            store.SaveEquation(name, "z*z + c", UserEquationKind.UserEquation,
                escapeRadius: 32.0, seed: null, bailoutCondition: null, colorInterior: false);
            store.SaveEquation(name, "z*z + c", UserEquationKind.UserEquation,
                escapeRadius: 999.0, seed: "2*c", bailoutCondition: "n > 5", colorInterior: true);

            var e = store.GetByName(name);
            Assert.NotNull(e);
            Assert.Equal(999.0, e!.EscapeRadius);
            Assert.Equal("2*c", e.Seed);
            Assert.Equal("n > 5", e.BailoutCondition);
            Assert.True(e.ColorInterior);
        }
        finally { store.Remove(name); }
    }

    // A default Save (no settings) leaves them at the auto/blank/off defaults —
    // so an old workflow that never set them is unchanged.
    [Fact]
    public void SaveEquation_Defaults_AreAutoBlankOff()
    {
        var store = UserEquationStore.Instance;
        const string name = "RenderSettingsDefaults_UEBUG";
        try
        {
            var e = store.SaveEquation(name, "z*z + c");
            Assert.NotNull(e);
            Assert.Equal(0.0, e!.EscapeRadius);      // 0 = auto
            Assert.Null(e.Seed);                     // z0 = 0
            Assert.Null(e.BailoutCondition);         // escape-radius only
            Assert.False(e.ColorInterior);           // flat interior
        }
        finally { store.Remove(name); }
    }

    // Legacy JSON without the new fields deserialises to the defaults, so
    // selecting such an entry RESETS the live controls (the second half of the
    // bug — stale values leaking across a selection).
    [Fact]
    public void LegacyEntry_MissingFields_DeserialiseToDefaults()
    {
        var e = JsonSerializer.Deserialize<UserEquationEntry>(
            "{\"Name\":\"legacy\",\"Source\":\"z*z + c\"}");
        Assert.NotNull(e);
        Assert.Equal(0.0, e!.EscapeRadius);
        Assert.Null(e.Seed);
        Assert.Null(e.BailoutCondition);
        Assert.False(e.ColorInterior);
    }
}
