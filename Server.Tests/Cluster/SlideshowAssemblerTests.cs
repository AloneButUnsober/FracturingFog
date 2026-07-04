// Server.Tests/Cluster/SlideshowAssemblerTests.cs
// D-4c — SlideshowAssembler walks a slideshow job's slides dir and
// writes a slides-manifest.json describing each per-slide PNG. The
// tests exercise: manifest schema, per-slide sha256, displayMs
// override semantics, and the missing-file refusal.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class SlideshowAssemblerTests
{
    private static JobSubmitDto BuildSubmit(int slideCount,
        int defaultDisplayMs = 5000,
        List<int>? perSlideMs = null)
    {
        var slides = new List<RenderRequestDto>(slideCount);
        for (int i = 0; i < slideCount; i++)
            slides.Add(new RenderRequestDto
            {
                Mode        = "image",
                RegionName  = $"R{i}",
                ThemeName   = $"T{i}",
                FractalType = "Mandelbrot",
                CenterX     = 0, CenterY = 0, Zoom = 1.0,
                Width       = 64, Height = 64,
            });

        return new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode        = "slideshow",
                FractalType = "Mandelbrot",
                Width       = 64, Height = 64,
            },
            Slides = slides,
            SlideshowDefaultDisplayMs = defaultDisplayMs,
            SlideDisplayMs = perSlideMs,
        };
    }

    [Fact]
    public void Assemble_Writes_Manifest_With_Per_Slide_Entries()
    {
        using var td = new TempDir();
        var jobs = new JobStore(td.Path);
        string jobId = JobStore.NewJobId();

        var submit = BuildSubmit(3);
        var plan = SlideshowPlanner.PlanSlideshow(submit);
        jobs.Create(jobId, submit, plan);

        // Lay down 3 fake PNGs.
        for (int i = 0; i < 3; i++)
            jobs.WriteSlideBytes(jobId, i, new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)i });

        var result = SlideshowAssembler.Assemble(jobs, jobId, submit);

        Assert.Equal(3, result.SlideCount);
        Assert.True(File.Exists(result.ArtifactPath));
        Assert.EndsWith(SlideshowAssembler.ArtifactExt, result.ArtifactPath);
        Assert.True(result.ArtifactBytes > 0);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.ArtifactPath));
        var root = doc.RootElement;
        Assert.Equal("slideshow", root.GetProperty("mode").GetString());
        Assert.Equal(3, root.GetProperty("slideCount").GetInt32());

        var entries = root.GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());
        for (int i = 0; i < 3; i++)
        {
            var e = entries[i];
            Assert.Equal(i, e.GetProperty("slideIndex").GetInt32());
            Assert.Equal($"slide_{i + 1:D5}.png", e.GetProperty("name").GetString());
            Assert.True(e.GetProperty("bytes").GetInt64() > 0);
            Assert.False(string.IsNullOrEmpty(e.GetProperty("sha256").GetString()));
            Assert.Equal($"R{i}", e.GetProperty("regionName").GetString());
            Assert.Equal($"T{i}", e.GetProperty("themeName").GetString());
            Assert.Equal(5000,    e.GetProperty("displayMs").GetInt32());
        }
    }

    [Fact]
    public void Assemble_Per_Slide_DisplayMs_Overrides_Default()
    {
        using var td = new TempDir();
        var jobs = new JobStore(td.Path);
        string jobId = JobStore.NewJobId();

        var submit = BuildSubmit(3,
            defaultDisplayMs: 5000,
            perSlideMs: new() { 1000, 0, 3000 });   // 0 = use default
        var plan = SlideshowPlanner.PlanSlideshow(submit);
        jobs.Create(jobId, submit, plan);

        for (int i = 0; i < 3; i++)
            jobs.WriteSlideBytes(jobId, i, new byte[] { (byte)(0x10 + i) });

        var result = SlideshowAssembler.Assemble(jobs, jobId, submit);

        using var doc = JsonDocument.Parse(File.ReadAllText(result.ArtifactPath));
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(1000, entries[0].GetProperty("displayMs").GetInt32());
        Assert.Equal(5000, entries[1].GetProperty("displayMs").GetInt32()); // 0 → default
        Assert.Equal(3000, entries[2].GetProperty("displayMs").GetInt32());
    }

    [Fact]
    public void Assemble_Throws_When_A_Slide_Is_Missing()
    {
        using var td = new TempDir();
        var jobs = new JobStore(td.Path);
        string jobId = JobStore.NewJobId();

        var submit = BuildSubmit(3);
        var plan = SlideshowPlanner.PlanSlideshow(submit);
        jobs.Create(jobId, submit, plan);

        // Write only slides 0 and 2; slide 1 is missing.
        jobs.WriteSlideBytes(jobId, 0, new byte[] { 0x01 });
        jobs.WriteSlideBytes(jobId, 2, new byte[] { 0x03 });

        var ex = Assert.Throws<InvalidOperationException>(
            () => SlideshowAssembler.Assemble(jobs, jobId, submit));
        Assert.Contains("slide #1", ex.Message);
    }

    [Fact]
    public void JobStore_SlidesDir_And_Counters_Behave()
    {
        using var td = new TempDir();
        var jobs = new JobStore(td.Path);
        string jobId = JobStore.NewJobId();

        var submit = BuildSubmit(2);
        var plan = SlideshowPlanner.PlanSlideshow(submit);
        jobs.Create(jobId, submit, plan);

        Assert.Equal(0, jobs.CountSlides(jobId));
        Assert.False(jobs.SlideExists(jobId, 0));

        jobs.WriteSlideBytes(jobId, 0, new byte[] { 0xAA, 0xBB });
        Assert.True(jobs.SlideExists(jobId, 0));
        Assert.False(jobs.SlideExists(jobId, 1));
        Assert.Equal(1, jobs.CountSlides(jobId));

        jobs.WriteSlideBytes(jobId, 1, new byte[] { 0xCC });
        Assert.Equal(2, jobs.CountSlides(jobId));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-ssa-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
