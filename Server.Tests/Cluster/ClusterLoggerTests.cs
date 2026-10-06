// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Logging;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

// #1159 — ClusterLogger: NDJSON events, and (the fix) events queued before
// Dispose survive thread-pool starvation. Shares the non-parallel collection
// with SessionLoggerTests because the starvation test caps the process pool.
[Collection(ThreadPoolExclusiveCollection.Name)]
public sealed class ClusterLoggerTests
{
    private static string TempDir()
    {
        string p = Path.Combine(Path.GetTempPath(), "ff-cluster-log-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(p);
        return p;
    }

    private static string[] ReadAllEventLines(string dir)
        => Directory.GetFiles(dir, "cluster-*.log").SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void Events_AreNdjson_WithTsKindAndFields()
    {
        string dir = TempDir();
        try
        {
            var log = new ClusterLogger(dir);
            log.Event("job-ready", new Dictionary<string, object?> { ["jobId"] = "J1", ["bytes"] = 42 });
            log.Dispose();
            var line = Assert.Single(ReadAllEventLines(dir));
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("job-ready", doc.RootElement.GetProperty("kind").GetString());
            Assert.Equal("J1", doc.RootElement.GetProperty("jobId").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("bytes").GetInt32());
            Assert.True(doc.RootElement.TryGetProperty("ts", out _));
            log.Event("after-dispose");   // ignored, no throw
            Assert.Single(ReadAllEventLines(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Regression: the drain used to be a Task.Run pump that Dispose waited on
    // for 3 s; with the pool starved it never ran and every queued event was
    // lost. Deterministic starvation: cap the pool at its minimum and block
    // every worker (restored in finally).
    [Fact]
    public void Dispose_KeepsQueuedEvents_EvenWhenTheThreadPoolIsStarved()
    {
        string dir = TempDir();
        using var gate = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo));
        var blockers = Enumerable.Range(0, minWorkers)
            .Select(_ => Task.Run(() => gate.Wait(TimeSpan.FromSeconds(30))))
            .ToArray();
        try
        {
            Thread.Sleep(300);
            var log = new ClusterLogger(dir);
            for (int i = 0; i < 100; i++)
                log.Event("tile-delivered", new Dictionary<string, object?> { ["n"] = i });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Dispose();
            sw.Stop();

            var ns = ReadAllEventLines(dir)
                .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("n").GetInt32())
                .ToHashSet();
            Assert.Equal(100, ns.Count);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dispose took {sw.Elapsed} (hit the flush timeout)");
        }
        finally
        {
            gate.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            Task.WaitAll(blockers, TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
