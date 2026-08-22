// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 MC-cap UI toggle (3D-Rendering-Roadmap §S9, #391) — the
// boundary-cap flag (#422) is now a user control on the UserBulb mesh-export panel,
// persisted per bulb in UserBulbSnapshot.ExportCapBoundary so a saved bulb reopens
// with the choice the user made. This guards that the new snapshot field survives a
// JSON round-trip (and that its absence in an older snapshot reads as null → the VM
// keeps its default-on).

using System.Text.Json;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class UserBulbExportSnapshotTests
{
    [Fact]
    public void ExportCapBoundary_Round_Trips_Through_Json()
    {
        var snap = new UserBulbSnapshot { ExportCapBoundary = false };
        string json = JsonSerializer.Serialize(snap);
        var back = JsonSerializer.Deserialize<UserBulbSnapshot>(json)!;
        Assert.False(back.ExportCapBoundary);

        var snapOn = new UserBulbSnapshot { ExportCapBoundary = true };
        var backOn = JsonSerializer.Deserialize<UserBulbSnapshot>(JsonSerializer.Serialize(snapOn))!;
        Assert.True(backOn.ExportCapBoundary);
    }

    [Fact]
    public void Older_Snapshot_Without_The_Field_Reads_As_Null()
    {
        // A snapshot JSON from before the toggle existed has no ExportCapBoundary
        // key; it must deserialize to null so the VM leaves its default (on).
        var back = JsonSerializer.Deserialize<UserBulbSnapshot>("{}")!;
        Assert.Null(back.ExportCapBoundary);
    }
}
