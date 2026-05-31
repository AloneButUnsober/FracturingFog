using System.IO;
using System.Linq;
using FracturingFog.Server.Logging;
using Xunit;

namespace FracturingFog.Server.Tests;

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
