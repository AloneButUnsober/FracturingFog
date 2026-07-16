// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/TileNextResultDto.cs
// Master → Worker, returned from a tile.next long-poll.
//
// D-1 ships the envelope and the wait-again path only. The tile payload
// (TileJobDto) lands in Phase D-2 — until then the master always returns
// WaitAgain=true after holding the poll for TileNextHoldSeconds.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class TileNextResultDto
{
    /// <summary>Master had no tile to assign within the long-poll window.
    /// Worker should immediately reissue tile.next.</summary>
    [JsonPropertyName("waitAgain")]
    public bool WaitAgain { get; set; }

    /// <summary>Set when the master wants the worker to disconnect (e.g.
    /// admin worker.kill or master shutdown). Worker MUST close cleanly
    /// after observing this flag.</summary>
    [JsonPropertyName("shutdown")]
    public bool Shutdown { get; set; }

    /// <summary>Non-null when the master has a tile to assign — worker
    /// renders it and ships pixels back via tile.deliver, then issues a
    /// fresh tile.next. Null with WaitAgain=true means the long-poll
    /// elapsed with no work; worker reissues tile.next immediately.</summary>
    [JsonPropertyName("tile")]
    public TileJobDto? Tile { get; set; }
}
