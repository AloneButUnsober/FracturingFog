using FracturingFog.Server;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void RecordFailure_StripsWindowsPaths()
    {
        var m = new Metrics();
        m.RecordFailure("render-failed",
            "ffmpeg exited with code 1 at C:\\NeverEnding\\AloneButUnsober\\out.png");
        Assert.NotNull(m.LastErrorMessage);
        Assert.DoesNotContain("C:\\", m.LastErrorMessage);
        Assert.Contains("<path>", m.LastErrorMessage);
    }

    [Fact]
    public void RecordFailure_StripsPosixPaths()
    {
        var m = new Metrics();
        m.RecordFailure("internal",
            "open /var/lib/fracturingfog/server-work/job-abc/out.png failed");
        Assert.NotNull(m.LastErrorMessage);
        Assert.DoesNotContain("/var/lib/", m.LastErrorMessage);
        Assert.Contains("<path>", m.LastErrorMessage);
    }

    [Fact]
    public void RecordFailure_TruncatesLongMessages()
    {
        var m = new Metrics();
        m.RecordFailure("internal", new string('x', 10_000));
        Assert.NotNull(m.LastErrorMessage);
        Assert.True(m.LastErrorMessage!.Length <= 241); // 240 + "…"
    }

    [Fact]
    public void RecordFailure_PreservesShortMessage()
    {
        var m = new Metrics();
        m.RecordFailure("timeout", "render exceeded 1 minute(s)");
        Assert.Equal("render exceeded 1 minute(s)", m.LastErrorMessage);
    }

    [Fact]
    public void RecordSuccess_IncrementsCompleted()
    {
        var m = new Metrics();
        m.RecordSuccess();
        m.RecordSuccess();
        Assert.Equal(2, m.Completed);
    }

    [Fact]
    public void BeginRender_TracksInFlight()
    {
        var m = new Metrics();
        Assert.Equal(0, m.InFlight);
        using (m.BeginRender())
        {
            Assert.Equal(1, m.InFlight);
        }
        Assert.Equal(0, m.InFlight);
    }
}
