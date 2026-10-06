// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.IO;
using System.Linq;
using FracturingFog.Server.Logging;
using Xunit;

namespace FracturingFog.Server.Tests;

// Non-parallel: one test caps the process-wide thread pool for a moment.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolExclusiveCollection { public const string Name = "ThreadPoolExclusive"; }

[Collection(ThreadPoolExclusiveCollection.Name)]
public sealed class SessionLoggerTests
{
    [Fact]
    public void Open_WritesHeaderAndCloseMarker()
    {
        string dir = TempDir();
        try
        {
            var log = SessionLogger.Open(dir, "127.0.0.1:12345", clientCertThumbprint: "ABC123");
            log.Info("hello");
            log.Warn("uh oh");
            log.Err("crash");
            log.Dispose();

            string[] lines = File.ReadAllLines(log.Path);
            Assert.Contains(lines, l => l.StartsWith("# FracturingFog server session"));
            Assert.Contains(lines, l => l.Contains("127.0.0.1:12345"));
            Assert.Contains(lines, l => l.Contains("ABC123"));
            Assert.Contains(lines, l => l.Contains("[INFO] hello"));
            Assert.Contains(lines, l => l.Contains("[WARN] uh oh"));
            Assert.Contains(lines, l => l.Contains("[ERR ] crash"));
            Assert.Contains(lines, l => l.StartsWith("# closed"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Dispose_FlushesQueuedLines()
    {
        // The async pump should drain remaining queued lines before the
        // close marker — a fast-write, fast-dispose pair must not lose
        // the user lines that were enqueued just before Dispose ran.
        string dir = TempDir();
        try
        {
            var log = SessionLogger.Open(dir, "test", null);
            for (int i = 0; i < 50; i++) log.Info($"line {i}");
            log.Dispose();

            string content = File.ReadAllText(log.Path);
            for (int i = 0; i < 50; i++)
                Assert.Contains($"line {i}", content);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void WritesAfterDispose_AreIgnored()
    {
        string dir = TempDir();
        try
        {
            var log = SessionLogger.Open(dir, "test", null);
            log.Info("before");
            log.Dispose();
            log.Info("after");   // should be silently swallowed
            log.Warn("after w");

            string content = File.ReadAllText(log.Path);
            Assert.Contains("before", content);
            Assert.DoesNotContain("after", content.Replace("# closed", ""));
        }
        finally { TryDelete(dir); }
    }

    // Regression (full-suite flake → real bug): the drain used to be a
    // thread-pool task, and Dispose gave up after 3 s. With the pool starved
    // (every worker blocked — a loaded server, or a busy test run) the pump
    // never ran, Dispose closed the file, and every queued line was lost. The
    // pump is now a dedicated thread: lines survive and Dispose returns promptly.
    [Fact]
    public void Dispose_DrainsQueuedLines_EvenWhenTheThreadPoolIsStarved()
    {
        string dir = TempDir();
        using var gate = new System.Threading.ManualResetEventSlim(false);
        // Deterministic starvation: cap the pool at its minimum, then block every
        // worker. A pool-scheduled drain can then never run (the pool cannot
        // inject threads past the cap), which is exactly the condition that lost
        // lines before. Process-wide, hence the non-parallel collection; restored
        // in finally.
        System.Threading.ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        System.Threading.ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        Assert.True(System.Threading.ThreadPool.SetMaxThreads(minWorkers, maxIo));
        var blockers = Enumerable.Range(0, minWorkers)
            .Select(_ => System.Threading.Tasks.Task.Run(() => gate.Wait(System.TimeSpan.FromSeconds(30))))
            .ToArray();
        try
        {
            System.Threading.Thread.Sleep(300);   // let the blockers occupy the pool
            var log = SessionLogger.Open(dir, "starved", null);
            for (int i = 0; i < 200; i++) log.Info($"line {i}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Dispose();
            sw.Stop();

            string content = File.ReadAllText(log.Path);
            for (int i = 0; i < 200; i++) Assert.Contains($"line {i}", content);
            Assert.Contains("# closed", content);
            Assert.True(sw.Elapsed < System.TimeSpan.FromSeconds(2), $"Dispose took {sw.Elapsed} (hit the flush timeout)");
        }
        finally
        {
            gate.Set();
            System.Threading.ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            System.Threading.Tasks.Task.WaitAll(blockers, System.TimeSpan.FromSeconds(30));
            TryDelete(dir);
        }
    }

    private static string TempDir()
    {
        string p = Path.Combine(Path.GetTempPath(), "ff-session-log-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
