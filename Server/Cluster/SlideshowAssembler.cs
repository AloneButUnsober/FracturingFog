// Server/Cluster/SlideshowAssembler.cs
// D-4c — Writes the final slides-manifest.json artifact for a slideshow
// job. The manifest describes every per-slide PNG on disk so a client
// renderer can iterate the set: filename, byte count, SHA-256, plus the
// per-slide display ms (when the client supplied a duration list) and
// the region/theme summary captured from the slide's RenderRequestDto.
//
// Naming mirrors ArtifactMerger / VideoFramePipeline: it owns the
// final-artifact write for one job mode. No long-running subprocess
// (slideshow has no encode pass) so this is a static helper rather
// than an IAsyncDisposable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

using FracturingFog.Server.Cluster.Protocol;

namespace FracturingFog.Server.Cluster;

public static class SlideshowAssembler
{
    public const string ArtifactExt = "slides-manifest.json";

    /// <summary>Walks <see cref="JobStore.SlidesDir"/> for the named job,
    /// writes a slides-manifest.json next to it, and returns the manifest
    /// path. Per-slide ordering follows the on-disk slide_NNNNN.png
    /// naming so the manifest entry order matches the original tile id
    /// order, even if files were written out-of-order by workers.</summary>
    public static AssembleResult Assemble(
        JobStore jobs, string jobId,
        JobSubmitDto submit,
        IReadOnlyList<string?>? slideRegionNames = null,
        IReadOnlyList<string?>? slideThemeNames  = null)
    {
        string slidesDir = jobs.SlidesDir(jobId);
        if (!Directory.Exists(slidesDir))
            throw new InvalidOperationException(
                $"slides dir missing for job '{jobId}': {slidesDir}");

        int defaultDisplayMs = submit.SlideshowDefaultDisplayMs;
        var perSlideOverride = submit.SlideDisplayMs;
        int slideCount = submit.Slides?.Count ?? jobs.CountSlides(jobId);

        var entries = new List<object>(slideCount);
        long totalBytes = 0;
        for (int i = 0; i < slideCount; i++)
        {
            string fname = JobStore.SlideFileName(i);
            string fpath = Path.Combine(slidesDir, fname);
            if (!File.Exists(fpath))
                throw new InvalidOperationException(
                    $"slide #{i} missing on disk: {fpath}");

            var fi = new FileInfo(fpath);
            totalBytes += fi.Length;

            int displayMs = defaultDisplayMs;
            if (perSlideOverride != null && i < perSlideOverride.Count)
            {
                int ov = perSlideOverride[i];
                if (ov > 0) displayMs = ov;
            }

            string sha;
            using (var fs = File.OpenRead(fpath))
            using (var sh = SHA256.Create())
                sha = Convert.ToBase64String(sh.ComputeHash(fs));

            string? regionName = null;
            string? themeName  = null;
            if (slideRegionNames != null && i < slideRegionNames.Count)
                regionName = slideRegionNames[i];
            if (slideThemeNames != null && i < slideThemeNames.Count)
                themeName = slideThemeNames[i];

            // Pull from the submit dto when the caller didn't pre-flatten.
            if (regionName is null && submit.Slides != null && i < submit.Slides.Count)
                regionName = submit.Slides[i].RegionName ?? submit.Request.RegionName;
            if (themeName is null && submit.Slides != null && i < submit.Slides.Count)
                themeName = submit.Slides[i].ThemeName ?? submit.Request.ThemeName;

            entries.Add(new
            {
                slideIndex = i,
                name       = fname,
                bytes      = fi.Length,
                sha256     = sha,
                displayMs,
                regionName,
                themeName,
            });
        }

        string manifestPath = jobs.ArtifactPath(jobId, ArtifactExt);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            jobId,
            mode              = "slideshow",
            slideCount,
            totalBytes,
            defaultDisplayMs,
            entries,
        }, new JsonSerializerOptions { WriteIndented = true }));

        long manifestBytes = new FileInfo(manifestPath).Length;
        string manifestSha;
        using (var fs = File.OpenRead(manifestPath))
        using (var sh = SHA256.Create())
            manifestSha = Convert.ToBase64String(sh.ComputeHash(fs));

        return new AssembleResult(
            ArtifactPath: manifestPath,
            ArtifactBytes: manifestBytes,
            ArtifactSha256: manifestSha,
            SlideTotalBytes: totalBytes,
            SlideCount: slideCount);
    }

    public sealed record AssembleResult(
        string ArtifactPath,
        long   ArtifactBytes,
        string ArtifactSha256,
        long   SlideTotalBytes,
        int    SlideCount);
}
