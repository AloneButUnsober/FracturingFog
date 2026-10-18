// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/OidnInstaller.cs
//
// #1338 — on-demand installer for Intel Open Image Denoise (OidnDenoiser), the
// FFmpeg pattern (FfmpegInstaller): the user asks for it, nothing ships in the
// packages or the repo.
//
//   1. Pick the official RenderKit release archive for this OS / CPU, pinned to
//      OidnDenoiser.Version with its SHA-256 (GitHub's published digest) — a
//      pinned build keeps every machine's denoise the same, and an unexpected
//      file is rejected rather than trusted.
//   2. Download it (progress), SHA-256 it, compare.
//   3. Extract only what the CPU denoiser needs — the OpenImageDenoise library, its
//      core and CPU device modules, oneTBB — plus the licence files, into a
//      staging folder, then swap it into place (the user data folder: writable on
//      every platform, an AppImage's own folder is not).
//   4. OidnDenoiser.Refresh() so the running app picks it up.
//
// OIDN is Apache-2.0 (oneTBB too); the licence files travel with the binaries.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>#1338 — downloads and unpacks the Intel Open Image Denoise runtime
/// (see the file header).</summary>
public static class OidnInstaller
{
    /// <summary>A release archive and its published SHA-256.</summary>
    public sealed record Asset(string FileName, string Sha256);

    private const string UserAgent = "FracturingFog-OidnInstaller/1.0";

    /// <summary>The archive for this OS / CPU, or null where OIDN publishes none.</summary>
    public static Asset? AssetForThisPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture;
        string v = OidnDenoiser.Version;
        if (OperatingSystem.IsWindows() && arch == Architecture.X64)
            return new($"oidn-{v}.x64.windows.zip", "f11f91bc072a5e3a564515724cb72ab8fcfbc445c84a84c197ff9b16cc01396f");
        if (OperatingSystem.IsLinux() && arch == Architecture.X64)
            return new($"oidn-{v}.x86_64.linux.tar.gz", "743c3e2aff8c220d5d70fe6cb970fb3d36f2702d2693c61d1d148e404cf37cd6");
        if (OperatingSystem.IsMacOS() && arch == Architecture.Arm64)
            return new($"oidn-{v}.arm64.macos.tar.gz", "98e0aca8e7ab69e9f4f0191582a500fd8b0d9085d68662f18ab6469e934a6efd");
        if (OperatingSystem.IsMacOS() && arch == Architecture.X64)
            return new($"oidn-{v}.x86_64.macos.tar.gz", "a4341639005a33ce0d944bd7c771ce34de1465be7eb3189b7c3b5b0064429832");
        return null;
    }

    public static string DownloadUrl(Asset a)
        => $"https://github.com/RenderKit/oidn/releases/download/v{OidnDenoiser.Version}/{a.FileName}";

    /// <summary>Whether an archive entry (a path inside the release) is part of the
    /// CPU runtime or its licences. GPU device modules (CUDA, HIP, SYCL, Metal) and
    /// their stacks, the tools and the headers are left out.</summary>
    public static bool ShouldExtract(string entryPath)
    {
        string p = entryPath.Replace('\\', '/');
        string name = p[(p.LastIndexOf('/') + 1)..];
        if (name.Length == 0) return false;
        if (p.Contains("/doc/", StringComparison.Ordinal))
            return name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("third-party-programs", StringComparison.OrdinalIgnoreCase);
        bool lib = p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/lib/", StringComparison.Ordinal);
        if (!lib) return false;
        bool native = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                   || name.Contains(".so", StringComparison.Ordinal)
                   || name.EndsWith(".dylib", StringComparison.Ordinal);
        if (!native) return false;
        if (name.Contains("OpenImageDenoise", StringComparison.Ordinal))
            return !(name.Contains("device_cuda", StringComparison.Ordinal) || name.Contains("device_hip", StringComparison.Ordinal)
                  || name.Contains("device_sycl", StringComparison.Ordinal) || name.Contains("device_metal", StringComparison.Ordinal));
        // oneTBB, which the CPU device runs on (not its NUMA "tbbbind" helpers).
        return name.StartsWith("tbb12", StringComparison.Ordinal) || name.StartsWith("libtbb.", StringComparison.Ordinal);
    }

    /// <summary>Download, verify and unpack the runtime for this platform.</summary>
    public static async Task<InstallResult> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var asset = AssetForThisPlatform();
        if (asset is null)
            return new InstallResult { Outcome = InstallOutcome.DownloadFailed, ErrorDetail = $"Open Image Denoise publishes no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})." };
        string tmp = Path.Combine(Path.GetTempPath(), $"ff-{asset.FileName}.{Environment.ProcessId}.part");
        try
        {
            progress?.Report(new InstallProgress { Phase = InstallPhase.Downloading, Fraction = 0, Message = asset.FileName });
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                using var resp = await http.GetAsync(DownloadUrl(asset), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    if (total > 0) progress?.Report(new InstallProgress { Phase = InstallPhase.Downloading, Fraction = (double)got / total });
                }
            }
            progress?.Report(new InstallProgress { Phase = InstallPhase.Verifying });
            return InstallFromArchive(tmp, asset, OidnDenoiser.InstallDirectory, progress);
        }
        catch (OperationCanceledException) { return new InstallResult { Outcome = InstallOutcome.Cancelled }; }
        catch (Exception ex) { return new InstallResult { Outcome = InstallOutcome.DownloadFailed, ErrorDetail = ex.Message }; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Verify <paramref name="archivePath"/> against <paramref name="asset"/>'s
    /// SHA-256 and unpack the runtime into <paramref name="targetDir"/> (replaced
    /// whole). The download step's tail; public for tests and offline installs.</summary>
    public static InstallResult InstallFromArchive(string archivePath, Asset asset, string targetDir, IProgress<InstallProgress>? progress = null)
    {
        string sha;
        using (var fs = File.OpenRead(archivePath)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            return new InstallResult { Outcome = InstallOutcome.HashMismatch, ErrorDetail = $"SHA-256 {sha} does not match the published {asset.Sha256}." };

        progress?.Report(new InstallProgress { Phase = InstallPhase.Extracting });
        string staging = targetDir.TrimEnd('/', '\\') + ".new";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            int files = asset.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? ExtractZip(archivePath, staging) : ExtractTarGz(archivePath, staging);
            if (!File.Exists(Path.Combine(staging, OidnDenoiser.LibraryFileName)))
                return new InstallResult { Outcome = InstallOutcome.ExtractFailed, ErrorDetail = $"{OidnDenoiser.LibraryFileName} is not in the archive ({files} files taken)." };
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetDir))!);
            Directory.Move(staging, targetDir);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            return new InstallResult { Outcome = InstallOutcome.ExtractFailed, ErrorDetail = ex.Message };
        }
        OidnDenoiser.Refresh();
        progress?.Report(new InstallProgress { Phase = InstallPhase.Done, Fraction = 1 });
        return new InstallResult { Outcome = InstallOutcome.Installed, NewVersion = OidnDenoiser.Version };
    }

    private static int ExtractZip(string path, string dir)
    {
        int n = 0;
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            if (!ShouldExtract(e.FullName)) continue;
            e.ExtractToFile(Path.Combine(dir, e.Name), overwrite: true);
            n++;
        }
        return n;
    }

    // Unix archives: the libraries are versioned files plus symlinks to them
    // (libOpenImageDenoise.so.2 → .so.2.5.1); regular files first, then the links.
    private static int ExtractTarGz(string path, string dir)
    {
        int n = 0;
        var links = new List<(string Name, string Target)>();
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            TarEntry? e;
            while ((e = tar.GetNextEntry()) != null)
            {
                if (!ShouldExtract(e.Name)) continue;
                string name = Path.GetFileName(e.Name);
                if (e.EntryType is TarEntryType.SymbolicLink) { links.Add((name, Path.GetFileName(e.LinkName))); continue; }
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                e.ExtractToFile(Path.Combine(dir, name), overwrite: true);
                n++;
            }
        }
        foreach (var (name, target) in links)
        {
            string at = Path.Combine(dir, name), to = Path.Combine(dir, target);
            if (!File.Exists(to)) continue;
            try { File.CreateSymbolicLink(at, target); }
            catch { File.Copy(to, at, overwrite: true); }
            n++;
        }
        return n;
    }
}
