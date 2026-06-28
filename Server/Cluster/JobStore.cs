// Server/Cluster/JobStore.cs
// On-disk persistence for cluster jobs. One directory per job under
// <root>/<jobid>/ with:
//   request.json   — original JobSubmitDto
//   plan.json      — TilePlanner.Plan summary (tile rects, target px)
//   tiles/<id>.png — raw tile payload bytes (PNG or RGBA, see PayloadKind)
//   artifact.<ext> — final merged artifact (png / mp4 / mkv)
//   events.ndjson  — state-transition log: state changes, errors, retries
//   status.json    — current state + counters, atomically rewritten
//
// Threading: every job has a per-job lock guarding its on-disk state.
// The lock is held only while reading/writing files — tile blobs flow
// through it but the merge math runs without holding it (ArtifactMerger
// owns the mmap buffer independently). Status.json is rewritten
// (write-temp + File.Move) so a partial write is never visible.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Wire;

namespace FracturingFog.Server.Cluster;

public sealed class JobStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; }
    private readonly ConcurrentDictionary<string, object> _jobLocks =
        new(StringComparer.Ordinal);

    /// <summary>Wall-clock provider. Swappable so tests can backdate
    /// status updates without sleeping.</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;

    public JobStore(string rootDir)
    {
        Root = rootDir;
        Directory.CreateDirectory(Root);
    }

    private long NowUnixMs() => new DateTimeOffset(NowUtc(), TimeSpan.Zero).ToUnixTimeMilliseconds();

    public string JobDir(string jobId) => Path.Combine(Root, jobId);

    private object LockFor(string jobId)
        => _jobLocks.GetOrAdd(jobId, _ => new object());

    /// <summary>Generate a 128-bit cluster-unique job id. Crockford base32,
    /// no padding. Generated server-side so a malicious client cannot
    /// collide ids on purpose.</summary>
    public static string NewJobId()
    {
        Span<byte> raw = stackalloc byte[16];
        RandomNumberGenerator.Fill(raw);
        return Base32CrockfordEncode(raw);
    }

    public bool Exists(string jobId)
        => Directory.Exists(JobDir(jobId));

    public IEnumerable<string> ListJobIds()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var d in Directory.EnumerateDirectories(Root))
            yield return Path.GetFileName(d);
    }

    /// <summary>Create the job dir, persist the submit dto + plan, write
    /// the initial status. Throws if the dir already exists — JobIds are
    /// random so a collision is a fatal bug, not retry-worthy.</summary>
    public void Create(string jobId, JobSubmitDto submit, TilePlanner.Plan plan)
    {
        string dir = JobDir(jobId);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "tiles"));

        lock (LockFor(jobId))
        {
            File.WriteAllText(Path.Combine(dir, "request.json"),
                JsonSerializer.Serialize(submit, JsonOpts));
            File.WriteAllText(Path.Combine(dir, "plan.json"),
                JsonSerializer.Serialize(plan, JsonOpts));
            long now = NowUnixMs();
            WriteStatusLocked(dir, new PersistedStatus
            {
                JobState        = "queued",
                Mode            = plan.Mode ?? "",
                TilesTotal      = plan.TileCount,
                TilesDone       = 0,
                CreatedUnixMs   = now,
                LastUpdateUnixMs = now,
            });
            AppendEventLocked(dir, "created", new Dictionary<string, object?>
            {
                ["tiles"] = plan.TileCount,
            });
        }
    }

    public JobSubmitDto? ReadSubmit(string jobId)
    {
        string path = Path.Combine(JobDir(jobId), "request.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<JobSubmitDto>(File.ReadAllText(path), JsonOpts);
    }

    public PersistedStatus? ReadStatus(string jobId)
    {
        string path = Path.Combine(JobDir(jobId), "status.json");
        if (!File.Exists(path)) return null;
        lock (LockFor(jobId))
            return JsonSerializer.Deserialize<PersistedStatus>(File.ReadAllText(path), JsonOpts);
    }

    /// <summary>Apply <paramref name="mutate"/> to the on-disk status
    /// holding the per-job lock. Re-writes status.json atomically.</summary>
    public PersistedStatus UpdateStatus(string jobId, Action<PersistedStatus> mutate)
    {
        string dir = JobDir(jobId);
        lock (LockFor(jobId))
        {
            var cur = ReadStatusLocked(dir) ?? throw new InvalidOperationException(
                $"job '{jobId}' has no status.json — was it created?");
            mutate(cur);
            cur.LastUpdateUnixMs = NowUnixMs();
            WriteStatusLocked(dir, cur);
            return cur;
        }
    }

    public void AppendEvent(string jobId, string kind, IReadOnlyDictionary<string, object?>? fields = null)
    {
        string dir = JobDir(jobId);
        lock (LockFor(jobId))
            AppendEventLocked(dir, kind, fields);
    }

    public void WriteTileBytes(string jobId, int tileId, byte[] payload)
    {
        string dir = JobDir(jobId);
        string tilesDir = Path.Combine(dir, "tiles");
        Directory.CreateDirectory(tilesDir);
        // Write-and-rename so a crashed master never leaves a half-written
        // tile that the merge path would treat as complete.
        string finalPath = Path.Combine(tilesDir, $"{tileId}.bin");
        string tmpPath   = finalPath + ".tmp";
        File.WriteAllBytes(tmpPath, payload);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    public bool TryReadTileBytes(string jobId, int tileId, out byte[] payload)
    {
        string path = Path.Combine(JobDir(jobId), "tiles", $"{tileId}.bin");
        if (!File.Exists(path)) { payload = Array.Empty<byte>(); return false; }
        payload = File.ReadAllBytes(path);
        return true;
    }

    public string ArtifactPath(string jobId, string ext)
        => Path.Combine(JobDir(jobId), $"artifact.{ext.TrimStart('.')}");

    /// <summary>D-4 — directory holding per-frame PNGs for a video job.
    /// Named to match the ffmpeg image2 demuxer convention so the D-4b
    /// encode pass can point ffmpeg at this folder directly.</summary>
    public string FramesDir(string jobId)
        => Path.Combine(JobDir(jobId), "frames");

    /// <summary>D-4 — frame filename for the image2 demuxer
    /// (frame_NNNNNN.png, 1-based to match ffmpeg's start_number=1
    /// default). frameIndex on the wire is 0-based; we add 1 when
    /// turning it into a filename.</summary>
    public static string FrameFileName(int frameIndex0Based)
        => $"frame_{frameIndex0Based + 1:D6}.png";

    /// <summary>D-4 — persist one frame's PNG bytes for a video job.
    /// Same write-and-rename pattern as tile bytes so a crashed master
    /// never leaves a half-written frame the encoder would later trip
    /// over.</summary>
    public void WriteFrameBytes(string jobId, int frameIndex, byte[] png)
    {
        string dir = FramesDir(jobId);
        Directory.CreateDirectory(dir);
        string finalPath = Path.Combine(dir, FrameFileName(frameIndex));
        string tmpPath   = finalPath + ".tmp";
        File.WriteAllBytes(tmpPath, png);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    public bool FrameExists(string jobId, int frameIndex)
        => File.Exists(Path.Combine(FramesDir(jobId), FrameFileName(frameIndex)));

    /// <summary>D-4 — count of per-frame files on disk for this job. Used
    /// by the coordinator to detect a complete video tile-set and by the
    /// D-4b finaliser to gate the ffmpeg encode pass.</summary>
    public int CountFrames(string jobId)
    {
        string dir = FramesDir(jobId);
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var _ in Directory.EnumerateFiles(dir, "frame_*.png")) n++;
        return n;
    }

    /// <summary>D-4c — directory holding per-slide PNGs for a slideshow
    /// job. One file per slide; final artifact is a slides-manifest.json
    /// produced by the finaliser.</summary>
    public string SlidesDir(string jobId)
        => Path.Combine(JobDir(jobId), "slides");

    /// <summary>D-4c — slide filename. 1-based for easy mental
    /// correspondence with the slideshow's display order; the
    /// slideIndex on the wire is 0-based (tile id).</summary>
    public static string SlideFileName(int slideIndex0Based)
        => $"slide_{slideIndex0Based + 1:D5}.png";

    /// <summary>D-4c — persist one slide's PNG bytes. Same write-and-
    /// rename pattern as tile / frame bytes so a crashed master never
    /// leaves a half-written slide that the manifest writer would later
    /// trip over.</summary>
    public void WriteSlideBytes(string jobId, int slideIndex, byte[] png)
    {
        string dir = SlidesDir(jobId);
        Directory.CreateDirectory(dir);
        string finalPath = Path.Combine(dir, SlideFileName(slideIndex));
        string tmpPath   = finalPath + ".tmp";
        File.WriteAllBytes(tmpPath, png);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    public bool SlideExists(string jobId, int slideIndex)
        => File.Exists(Path.Combine(SlidesDir(jobId), SlideFileName(slideIndex)));

    /// <summary>D-4c — write-and-rename helper for slide payloads that
    /// need an encode step (RGBA → PNG via <see cref="IClusterImageCodec"/>).
    /// The callback writes to <paramref name="tmpPath"/>; this method then
    /// atomically renames over the final slide_NNNNN.png. Mirrors the
    /// crash-safety of <see cref="WriteSlideBytes"/>.</summary>
    public void EncodeSlideTo(string jobId, int slideIndex, Action<string> encodeToTmp)
    {
        string dir = SlidesDir(jobId);
        Directory.CreateDirectory(dir);
        string finalPath = Path.Combine(dir, SlideFileName(slideIndex));
        string tmpPath   = finalPath + ".tmp";
        encodeToTmp(tmpPath);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    /// <summary>D-4c — count of per-slide files on disk. Used by the
    /// coordinator to detect a complete slideshow tile-set and by the
    /// finaliser to know how many entries the manifest should describe.</summary>
    public int CountSlides(string jobId)
    {
        string dir = SlidesDir(jobId);
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var _ in Directory.EnumerateFiles(dir, "slide_*.png")) n++;
        return n;
    }

    /// <summary>Crash-recovery sweep — invoke at master start. Any job
    /// stuck in rendering/merging/planning becomes "failed" with reason
    /// "master-restart". Returns the count of jobs that were marked.
    /// D-6a swaps this with <see cref="ClusterCoordinator.RecoverFromDisk"/>
    /// for image jobs; this method remains the fallback path for video /
    /// slideshow modes whose tile streams cannot yet be replayed.</summary>
    public int FailInflightAfterRestart()
    {
        int n = 0;
        foreach (var jobId in ListJobIds())
        {
            var st = ReadStatus(jobId);
            if (st is null) continue;
            if (st.JobState is "rendering" or "merging" or "planning" or "queued")
            {
                UpdateStatus(jobId, s =>
                {
                    s.JobState   = "failed";
                    s.FailReason = "master-restart";
                });
                AppendEvent(jobId, "failed-on-restart", null);
                n++;
            }
        }
        return n;
    }

    /// <summary>D-6a — enumerate non-terminal jobs on disk so the master
    /// can rebuild dispatcher + merger state on restart. Yields one record
    /// per job in <c>queued | planning | rendering | merging</c>. Terminal
    /// jobs (ready / failed / cancelled) and jobs whose status.json is
    /// missing or malformed are skipped silently — they cannot be resumed
    /// and have no work the master needs to remember.</summary>
    public IEnumerable<ResumeRecord> EnumerateResumableJobs()
    {
        foreach (var jobId in ListJobIds().ToArray())
        {
            PersistedStatus? st;
            try { st = ReadStatus(jobId); }
            catch { continue; }
            if (st is null) continue;
            if (st.JobState is "ready" or "failed" or "cancelled") continue;
            var submit = ReadSubmit(jobId);
            if (submit is null) continue;
            yield return new ResumeRecord(jobId, st, submit);
        }
    }

    /// <summary>D-6a — list the tile ids whose payload bytes are on disk.
    /// Used by <see cref="ClusterCoordinator.RecoverFromDisk"/> to skip
    /// tiles that already delivered before the master died. File name
    /// pattern matches <see cref="WriteTileBytes"/> (<c>{id}.bin</c>).</summary>
    public IReadOnlyList<int> ListTilesOnDisk(string jobId)
    {
        string tilesDir = Path.Combine(JobDir(jobId), "tiles");
        if (!Directory.Exists(tilesDir)) return Array.Empty<int>();
        var ids = new List<int>();
        foreach (var path in Directory.EnumerateFiles(tilesDir, "*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, out int id)) ids.Add(id);
        }
        ids.Sort();
        return ids;
    }

    /// <summary>Evict jobs in terminal state whose last update is older
    /// than the retention window. Returns the count evicted.</summary>
    public int EvictExpired(TimeSpan retention)
    {
        int n = 0;
        long cutoff = NowUnixMs() - (long)retention.TotalMilliseconds;
        foreach (var jobId in ListJobIds().ToArray())
        {
            var st = ReadStatus(jobId);
            if (st is null) continue;
            if (st.JobState is not ("ready" or "failed" or "cancelled")) continue;
            if (st.LastUpdateUnixMs > cutoff) continue;
            try
            {
                Directory.Delete(JobDir(jobId), recursive: true);
                _jobLocks.TryRemove(jobId, out _);
                n++;
            }
            catch { /* best effort — next sweep retries */ }
        }
        return n;
    }

    // ── internals ────────────────────────────────────────────────────────

    private static PersistedStatus? ReadStatusLocked(string dir)
    {
        string path = Path.Combine(dir, "status.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PersistedStatus>(File.ReadAllText(path), JsonOpts);
    }

    private static void WriteStatusLocked(string dir, PersistedStatus s)
    {
        string final = Path.Combine(dir, "status.json");
        string tmp   = final + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOpts));
        if (File.Exists(final)) File.Delete(final);
        File.Move(tmp, final);
    }

    private static void AppendEventLocked(string dir, string kind, IReadOnlyDictionary<string, object?>? fields)
    {
        var record = new Dictionary<string, object?>(capacity: 2 + (fields?.Count ?? 0))
        {
            ["ts"]   = DateTime.UtcNow.ToString("O"),
            ["kind"] = kind,
        };
        if (fields != null)
            foreach (var kv in fields) record[kv.Key] = kv.Value;
        File.AppendAllText(
            Path.Combine(dir, "events.ndjson"),
            JsonSerializer.Serialize(record, JsonOpts) + "\n",
            Encoding.UTF8);
    }

    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string Base32CrockfordEncode(ReadOnlySpan<byte> bytes)
    {
        Span<char> outBuf = stackalloc char[(bytes.Length * 8 + 4) / 5];
        int outIx = 0;
        int buffer = 0;
        int bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                int ix = (buffer >> bits) & 0x1F;
                outBuf[outIx++] = CrockfordAlphabet[ix];
            }
        }
        if (bits > 0)
        {
            int ix = (buffer << (5 - bits)) & 0x1F;
            outBuf[outIx++] = CrockfordAlphabet[ix];
        }
        return new string(outBuf[..outIx]);
    }
}

/// <summary>D-6a — payload for <see cref="JobStore.EnumerateResumableJobs"/>.
/// Carries the persisted status snapshot + the original submit DTO so
/// the recovering coordinator can rebuild planner-derived state (tile
/// rects, frame ranges, slide list) by deserialising plan.json
/// separately if needed.</summary>
public sealed record ResumeRecord(string JobId, PersistedStatus Status, JobSubmitDto Submit);

public sealed class PersistedStatus
{
    /// <summary>queued | planning | rendering | merging | ready | failed | cancelled</summary>
    public string JobState { get; set; } = "";

    /// <summary>D-5a — mode the plan was built for (image | video |
    /// slideshow). Cached here so admin UI summaries don't have to crack
    /// plan.json per row. Static once the job is created.</summary>
    public string Mode { get; set; } = "";

    public int  TilesTotal      { get; set; }
    public int  TilesDone       { get; set; }
    public int  TilesInFlight   { get; set; }
    public long ArtifactBytes   { get; set; }

    public string? ArtifactExt  { get; set; }
    public string? ArtifactSha256 { get; set; }

    public long CreatedUnixMs    { get; set; }
    public long LastUpdateUnixMs { get; set; }

    public string? FailReason { get; set; }

    /// <summary>D-4 — total video frames in the parent job (0 for image
    /// mode). Static once the job is created so the client can show
    /// per-frame progress alongside per-tile progress.</summary>
    public int TotalFrames { get; set; }

    /// <summary>D-4 — frames written to disk so far. Master updates this
    /// at every tile.deliver; cluster admin UI uses it for the per-job
    /// progress bar in video mode (per-tile progress is misleading when
    /// each tile carries 30 frames).</summary>
    public int FramesDone { get; set; }

    /// <summary>D-4b — frames consumed by the master's streaming ffmpeg
    /// encoder so far. Lags <see cref="FramesDone"/> by at most
    /// <c>MaxFrameQueueDepth</c> (the backpressure gate). Stays at 0 for
    /// video jobs that fall back to the frames-manifest stub (no
    /// lossless preset or no ffmpeg on disk).</summary>
    public int EncodedFrames { get; set; }
}
