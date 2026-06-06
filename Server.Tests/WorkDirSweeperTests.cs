using System;
using System.IO;
using FracturingFog.Server;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class WorkDirSweeperTests
{
    [Fact]
    public void Sweeps_OnlyOldJobDirs()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ff-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string old1 = Path.Combine(root, "job-old-1");
            string old2 = Path.Combine(root, "job-old-2");
            string recent = Path.Combine(root, "job-recent");
            string notAJob = Path.Combine(root, "logs");
            foreach (var d in new[] { old1, old2, recent, notAJob })
                Directory.CreateDirectory(d);

            // Push old dirs' timestamps into the past.
            DateTime longAgo = DateTime.UtcNow.AddHours(-6);
            Directory.SetLastWriteTimeUtc(old1, longAgo);
            Directory.SetLastWriteTimeUtc(old2, longAgo);
            // recent + notAJob remain at "now".

            int deleted = WorkDirSweeper.Sweep(root, staleAgeHours: 1.0);
            Assert.Equal(2, deleted);
            Assert.False(Directory.Exists(old1));
            Assert.False(Directory.Exists(old2));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(notAJob));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Disabled_When_StaleAgeIsZero()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "ff-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string old = Path.Combine(root, "job-old");
            Directory.CreateDirectory(old);
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

            int deleted = WorkDirSweeper.Sweep(root, staleAgeHours: 0);
            Assert.Equal(0, deleted);
            Assert.True(Directory.Exists(old));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MissingDir_Is_NoOp()
    {
        string ghost = Path.Combine(Path.GetTempPath(),
            "ff-sweep-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(ghost));
        int deleted = WorkDirSweeper.Sweep(ghost, staleAgeHours: 1.0);
        Assert.Equal(0, deleted);
    }
}
