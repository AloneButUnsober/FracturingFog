// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Protocol/ServerStatusDto.cs
// Returned by the server.status RPC. Polled by the ServerAdmin dialog
// every second while the dialog is visible.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Protocol;

public sealed class ServerStatusDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    [JsonPropertyName("inFlight")]
    public int InFlight { get; set; }

    [JsonPropertyName("completed")]
    public long Completed { get; set; }

    [JsonPropertyName("failed")]
    public long Failed { get; set; }

    [JsonPropertyName("lastErrorCode")]
    public string? LastErrorCode { get; set; }

    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; set; }

    [JsonPropertyName("maxMinutes")]
    public int MaxMinutes { get; set; }

    [JsonPropertyName("allowOverride")]
    public bool AllowOverride { get; set; }

    [JsonPropertyName("queueDepth")]
    public int QueueDepth { get; set; }
}
