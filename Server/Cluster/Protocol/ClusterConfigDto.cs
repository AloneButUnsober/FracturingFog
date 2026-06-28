// Server/Cluster/Protocol/ClusterConfigDto.cs
// Admin → Master. Read / write the three live-tunable cluster knobs
// surfaced by the MasterConfigView in UI.Avalonia (D-5e):
//   - clusterMaxJobs                    (concurrent-job cap; 0 = unlimited)
//   - clusterArtifactRetentionMinutes   (job-dir eviction window; 0 = never)
//   - clusterTileTargetPixels           (planner fallback when no hint /
//                                        worker EMA; 0 = TilePlanner.DefaultTilePixels)
//
// Get is parameter-less. Set returns the post-apply values so the UI can
// distinguish "saved exactly what I sent" from "clamped to the allowed
// range" without a second round-trip.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class ClusterConfigGetRequestDto
{
    // intentionally empty — parameter-less RPC, body present for parity
    // with the request/response shape of the other cluster.* admin calls.
}

public sealed class ClusterConfigSetRequestDto
{
    [JsonPropertyName("clusterMaxJobs")]                  public int? ClusterMaxJobs                  { get; set; }
    [JsonPropertyName("clusterArtifactRetentionMinutes")] public int? ClusterArtifactRetentionMinutes { get; set; }
    [JsonPropertyName("clusterTileTargetPixels")]         public int? ClusterTileTargetPixels         { get; set; }
}

public sealed class ClusterConfigDto
{
    [JsonPropertyName("clusterMaxJobs")]                  public int ClusterMaxJobs                  { get; set; }
    [JsonPropertyName("clusterArtifactRetentionMinutes")] public int ClusterArtifactRetentionMinutes { get; set; }
    [JsonPropertyName("clusterTileTargetPixels")]         public int ClusterTileTargetPixels         { get; set; }
}
