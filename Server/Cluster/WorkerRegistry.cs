// Server/Cluster/WorkerRegistry.cs
// Thread-safe in-memory registry of connected worker nodes. Keyed by
// WorkerId (UUID-style base32). Pins the worker's cert thumbprint at
// first registration so a subsequent register call with a different cert
// is refused. A timeout sweep marks workers stale when they miss three
// heartbeat intervals.
//
// Phase D-1 keeps state purely in-memory. Phase D-6 adds disk persistence
// of the (WorkerId, thumbprint) pin so a master restart does not throw
// away every cluster member.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Tls;

namespace FracturingFog.Server.Cluster;

public sealed class WorkerRegistry
{
    private readonly ConcurrentDictionary<string, WorkerEntry> _byId = new(StringComparer.Ordinal);

    /// <summary>Heartbeat interval pushed to workers; staleness is
    /// computed as 3× this value. Default 5s → 15s stale window.</summary>
    public int HeartbeatIntervalSeconds { get; init; } = 5;

    /// <summary>Wall-clock provider. Swappable so tests can advance time
    /// deterministically without sleeping.</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;

    /// <summary>Register a fresh or resuming worker.</summary>
    /// <param name="dto">Worker-supplied capabilities.</param>
    /// <param name="thumbprint">SHA-1 thumbprint of the worker's TLS cert,
    /// pre-normalised via <see cref="ServerCertLoader.NormalizeThumbprint"/>.</param>
    /// <param name="error">Set to a wire-protocol error code on failure.</param>
    public WorkerEntry? Register(WorkerRegisterDto dto, string thumbprint, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(thumbprint))
        {
            error = "thumbprint-missing";
            return null;
        }

        // Resume path — worker supplied a previously issued WorkerId.
        // Verify the cert pin matches the entry the master remembers.
        if (!string.IsNullOrEmpty(dto.ResumeWorkerId)
            && _byId.TryGetValue(dto.ResumeWorkerId!, out var existing))
        {
            if (!string.Equals(existing.CertThumbprint, thumbprint, StringComparison.Ordinal))
            {
                error = "thumbprint-pin-mismatch";
                return null;
            }
            existing.UpdateFromRegister(dto, NowUtc());
            return existing;
        }

        // Fresh registration — synthesise a stable id. We do NOT use the
        // worker name because operators may run two nodes with the same
        // name; the id must be cluster-unique. Cert thumbprint alone
        // would be stable but exposes the cert's identity in plaintext
        // log lines, so we derive a fresh random id and only pin to the
        // thumbprint internally.
        string id = NewWorkerId();
        var entry = new WorkerEntry(id, thumbprint);
        entry.UpdateFromRegister(dto, NowUtc());

        if (!_byId.TryAdd(id, entry))
        {
            // Astronomically unlikely (128 bits of entropy); fall back to
            // a retry rather than throw — the caller will issue worker.register again.
            error = "id-collision";
            return null;
        }
        return entry;
    }

    /// <summary>Resolve an existing worker by id, enforcing the thumbprint
    /// pin. Returns null when the id is unknown OR the cert does not match.</summary>
    public WorkerEntry? Lookup(string workerId, string thumbprint, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(workerId))
        {
            error = "unknown-worker";
            return null;
        }
        if (!_byId.TryGetValue(workerId, out var entry))
        {
            error = "unknown-worker";
            return null;
        }
        if (!string.Equals(entry.CertThumbprint, thumbprint, StringComparison.Ordinal))
        {
            error = "thumbprint-pin-mismatch";
            return null;
        }
        return entry;
    }

    /// <summary>Record a heartbeat against the entry. Throws if the
    /// caller did not pre-resolve via <see cref="Lookup"/>.</summary>
    public void Heartbeat(WorkerEntry entry, HeartbeatDto dto)
    {
        entry.RecordHeartbeat(dto, NowUtc());
    }

    /// <summary>Snapshot of every registered worker — copy, so callers can
    /// iterate without holding any internal lock.</summary>
    public IReadOnlyList<WorkerEntry> Snapshot() => _byId.Values.ToArray();

    /// <summary>Median of per-worker EMA tile cost (ms per kilopixel)
    /// across workers that have at least one sample. Returns 0 when no
    /// worker has reported yet — the planner falls back to default tile
    /// sizing in that case.</summary>
    public double MedianMsPerKilopixel()
    {
        List<double>? samples = null;
        foreach (var w in _byId.Values)
        {
            double e = w.EmaMsPerKilopixel;
            if (e > 0)
            {
                samples ??= new List<double>();
                samples.Add(e);
            }
        }
        if (samples == null || samples.Count == 0) return 0;
        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>Number of workers currently considered live (within the
    /// stale window).</summary>
    public int LiveCount()
    {
        DateTime now = NowUtc();
        TimeSpan window = TimeSpan.FromSeconds(HeartbeatIntervalSeconds * 3);
        int n = 0;
        foreach (var w in _byId.Values)
            if (now - w.LastHeartbeatUtc <= window) n++;
        return n;
    }

    /// <summary>Remove workers that have missed 3× the heartbeat window.
    /// Returns the ids that were evicted so the caller can log them.</summary>
    public IReadOnlyList<string> SweepStale()
    {
        DateTime now = NowUtc();
        TimeSpan window = TimeSpan.FromSeconds(HeartbeatIntervalSeconds * 3);
        var evicted = new List<string>();
        foreach (var kv in _byId)
        {
            if (now - kv.Value.LastHeartbeatUtc > window
                && _byId.TryRemove(kv.Key, out _))
                evicted.Add(kv.Key);
        }
        return evicted;
    }

    /// <summary>Explicit removal, e.g. from worker.kill admin call or a
    /// clean worker shutdown.</summary>
    public bool Remove(string workerId)
        => _byId.TryRemove(workerId, out _);

    private static string NewWorkerId()
    {
        // 16 bytes (128 bits) of entropy, Crockford-style base32 without
        // padding for compact log lines and URL-safe admin UI routes.
        Span<byte> raw = stackalloc byte[16];
        RandomNumberGenerator.Fill(raw);
        return Base32CrockfordEncode(raw);
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

public sealed class WorkerEntry
{
    public string WorkerId { get; }
    public string CertThumbprint { get; }

    public string WorkerName { get; private set; } = "";
    public string OsPlatform { get; private set; } = "";
    public string CpuModel { get; private set; } = "";
    public int LogicalCores { get; private set; }
    public long TotalRamBytes { get; private set; }
    public IReadOnlyList<string> Gpus { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedFractalTypes { get; private set; } = Array.Empty<string>();
    public int MaxConcurrentTiles { get; private set; } = 1;
    public int PreferredTilePixels { get; private set; } = 512;
    public string EngineBuildSha { get; private set; } = "";
    public string ProtocolVersion { get; private set; } = "";

    public DateTime RegisteredAtUtc { get; private set; }
    public DateTime LastHeartbeatUtc { get; private set; }
    public int TilesInFlight { get; private set; }
    public double CpuPercent { get; private set; } = -1;
    public long FreeRamBytes { get; private set; }
    public string? LastNote { get; private set; }

    private int _quiesce;
    public bool Quiesced => Volatile.Read(ref _quiesce) != 0;

    // D-3b — per-worker tile-time EMA. Coordinator calls RecordTileTime
    // on each tile.deliver; planner uses the registry's median for
    // adaptive tile sizing on subsequent jobs. 0 = no data yet.
    private readonly object _emaLock = new();
    private double _emaMsPerKilopixel;
    public double EmaMsPerKilopixel
    {
        get { lock (_emaLock) return _emaMsPerKilopixel; }
    }
    public int TileSamples { get; private set; }
    private const double EmaAlpha = 0.3;

    /// <summary>Append a per-tile timing sample to the worker's EMA.
    /// pixels = tile width × height; renderMs = worker-reported wall ms.
    /// Calls are thread-safe; cheap (one lock per delivery).</summary>
    public void RecordTileTime(long pixels, long renderMs)
    {
        if (pixels <= 0 || renderMs <= 0) return;
        double sample = renderMs * 1000.0 / pixels; // ms per kilopixel
        lock (_emaLock)
        {
            _emaMsPerKilopixel = _emaMsPerKilopixel <= 0
                ? sample
                : EmaAlpha * sample + (1.0 - EmaAlpha) * _emaMsPerKilopixel;
            TileSamples++;
        }
    }

    internal WorkerEntry(string workerId, string thumbprint)
    {
        WorkerId = workerId;
        CertThumbprint = thumbprint;
    }

    internal void UpdateFromRegister(WorkerRegisterDto dto, DateTime now)
    {
        // Trim everything operator-supplied to the lengths advertised in
        // the dev plan — defends the admin log lines from being filled
        // with a 1 MB WorkerName.
        WorkerName            = Truncate(dto.WorkerName, 64);
        OsPlatform            = Truncate(dto.OsPlatform, 16);
        CpuModel              = Truncate(dto.CpuModel, 128);
        LogicalCores          = Math.Max(0, dto.LogicalCores);
        TotalRamBytes         = Math.Max(0, dto.TotalRamBytes);
        Gpus                  = (dto.Gpus ?? new()).Select(g => Truncate(g, 96)).ToArray();
        SupportedFractalTypes = (dto.SupportedFractalTypes ?? new()).Select(s => Truncate(s, 32)).ToArray();
        MaxConcurrentTiles    = Math.Clamp(dto.MaxConcurrentTiles, 1, 256);
        PreferredTilePixels   = Math.Clamp(dto.PreferredTilePixels, 64, 8192);
        EngineBuildSha        = Truncate(dto.EngineBuildSha, 64);
        ProtocolVersion       = Truncate(dto.ProtocolVersion, 8);

        RegisteredAtUtc  = now;
        LastHeartbeatUtc = now;
    }

    internal void RecordHeartbeat(HeartbeatDto dto, DateTime now)
    {
        LastHeartbeatUtc = now;
        TilesInFlight    = Math.Max(0, dto.TilesInFlight);
        CpuPercent       = dto.CpuPercent;
        FreeRamBytes     = Math.Max(0, dto.FreeRamBytes);
        LastNote         = string.IsNullOrEmpty(dto.Note) ? null : Truncate(dto.Note!, 256);
    }

    public void SetQuiesce(bool quiesced)
        => Volatile.Write(ref _quiesce, quiesced ? 1 : 0);

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
