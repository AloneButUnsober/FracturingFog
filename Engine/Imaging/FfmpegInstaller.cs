// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/FfmpegInstaller.cs
//
// Auto-installer for FFmpeg (GPL build from BtbN/FFmpeg-Builds). Drives
// the startup modal A-path (Download now) and the FloatingMenu Update
// flow.
//
// Wire:
//   1. Query GitHub release API (https://api.github.com/repos/BtbN/
//      FFmpeg-Builds/releases/tags/latest) for the target asset.
//   2. Pull the SHA-256 digest from the asset's `digest` field
//      (GitHub computes this; BtbN does not ship a .sha256 sidecar).
//      Missing digest → surface as a warning; caller decides whether
//      to abort or proceed.
//   3. Download the zip with a HEAD-then-GET progress pump.
//   4. SHA-256 the bytes and compare to the API digest.
//   5. Open the zip in-memory, find <root>/bin/ffmpeg.exe, write it
//      atomically into <AppBase>\Tools\ffmpeg.exe (via .new + rename).
//   6. Run the new binary with -version, parse, compare to current.
//      If older → reject (caller surfaces the rejection in the UI).
//
// Threading: all I/O is async. Caller marshals UI updates onto the
// dispatcher via the IProgress<InstallProgress> callback.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Imaging
{
    public enum InstallPhase
    {
        Idle,
        QueryingRelease,
        Downloading,
        Verifying,
        Extracting,
        DetectingVersion,
        Done,
        Failed,
    }

    public sealed class InstallProgress
    {
        public InstallPhase Phase { get; init; }
        /// <summary>0..1, or -1 when phase is non-quantifiable.</summary>
        public double Fraction { get; init; } = -1;
        public string? Message { get; init; }
    }

    public enum InstallOutcome
    {
        Installed,
        UpdatedFromOlder,
        SkippedNotNewer,
        RejectedOlder,
        HashMismatch,
        HashUnavailable,
        DownloadFailed,
        ExtractFailed,
        Cancelled,
    }

    public sealed class InstallResult
    {
        public InstallOutcome Outcome { get; init; }
        public string? PreviousVersion { get; init; }
        public string? NewVersion { get; init; }
        public string? ErrorDetail { get; init; }
        public bool Success =>
            Outcome == InstallOutcome.Installed ||
            Outcome == InstallOutcome.UpdatedFromOlder;
    }

    public static class FfmpegInstaller
    {
        private const string ReleaseApiUrl =
            "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

        private const string TargetAssetName =
            "ffmpeg-master-latest-win64-gpl.zip";

        private const string DirectAssetUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/" +
            TargetAssetName;

        private const string UserAgent = "FracturingFog-FfmpegInstaller/1.0";

        /// <summary>Tools/ffmpeg.exe under the app base. Returned even when
        /// the file is absent, so callers can plan the install path.</summary>
        public static string TargetPath =>
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");

        /// <summary>Whether ffmpeg.exe exists at the target path (or anywhere
        /// FfmpegEncoder.FindFfmpeg would discover it). Delegates to the
        /// encoder so discovery rules stay in one place.</summary>
        public static bool IsInstalled() => FracturingFog.FfmpegEncoder.IsAvailable();

        /// <summary>
        /// Runs the local ffmpeg.exe with -version and returns the first line
        /// ("ffmpeg version N-XXXXXX-gXXXXXX-YYYYMMDD..."). Returns null on
        /// any failure (no exe / non-zero exit / parse error). Sync-over-process
        /// with a hard 5-second cap — ffmpeg -version is non-interactive and
        /// finishes in ~100 ms even on cold start.
        /// </summary>
        public static string? TryReadInstalledVersion()
        {
            string? exe = FracturingFog.FfmpegEncoder.FindFfmpeg();
            if (exe == null) return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-hide_banner -version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                string stdout = p.StandardOutput.ReadToEnd();
                int nl = stdout.IndexOf('\n');
                string firstLine = (nl >= 0 ? stdout[..nl] : stdout).Trim();
                return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
            }
            catch { return null; }
        }

        /// <summary>
        /// Drives the full download / verify / extract / version-check
        /// pipeline. Reports phase + fraction via <paramref name="progress"/>.
        /// Cancellation kills any in-flight download.
        /// </summary>
        public static async Task<InstallResult> InstallAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken ct)
        {
            // Phase X.2 / Slice 2.5 — the auto-installer targets a Windows
            // `ffmpeg.exe` from the BtbN/FFmpeg-Builds win64-gpl release.
            // Linux/macOS hosts use the OS package manager (apt / brew) so
            // this code path is Win-only by construction. FfmpegSetupDialog
            // hides the Download button on non-Win and routes users to the
            // package-manager instructions panel; this early-out is a safety
            // net for any other caller that reaches the installer directly.
            if (!OperatingSystem.IsWindows())
            {
                return new InstallResult
                {
                    Outcome = InstallOutcome.DownloadFailed,
                    ErrorDetail = "Auto-install is Windows-only. " +
                                  "Use 'sudo apt install ffmpeg' on Linux or " +
                                  "'brew install ffmpeg' on macOS, then click " +
                                  "the rescan button in the FFmpeg Setup dialog.",
                };
            }

            string? previousVersion = TryReadInstalledVersion();

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            http.Timeout = TimeSpan.FromMinutes(10);

            // ── 1. Query release metadata for the SHA-256 digest ──────────
            progress?.Report(new InstallProgress
            {
                Phase = InstallPhase.QueryingRelease,
                Message = "Querying GitHub release metadata…",
            });

            string? expectedSha256 = null;
            string? downloadUrl = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                req.Headers.UserAgent.ParseAdd(UserAgent);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    (expectedSha256, downloadUrl) = TryParseReleaseAsset(body, TargetAssetName);
                }
            }
            catch
            {
                // Non-fatal — we'll fall back to the direct URL with no hash.
            }

            downloadUrl ??= DirectAssetUrl;

            // ── 2. Download the zip to a temp file ────────────────────────
            string tmpDir = Path.Combine(Path.GetTempPath(), "FracturingFog-ffmpeg");
            Directory.CreateDirectory(tmpDir);
            string tmpZip = Path.Combine(tmpDir, $"{Guid.NewGuid():N}.zip");

            try
            {
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Downloading,
                    Fraction = 0,
                    Message = "Downloading ffmpeg-master-latest-win64-gpl.zip…",
                });

                long? total = null;
                using (var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return new InstallResult
                        {
                            Outcome = InstallOutcome.DownloadFailed,
                            PreviousVersion = previousVersion,
                            ErrorDetail = $"HTTP {(int)resp.StatusCode} from {downloadUrl}",
                        };
                    }
                    total = resp.Content.Headers.ContentLength;

                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(tmpZip);
                    var buf = new byte[81920];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        got += n;
                        if (total is long t && t > 0)
                        {
                            progress?.Report(new InstallProgress
                            {
                                Phase = InstallPhase.Downloading,
                                Fraction = (double)got / t,
                                Message = $"Downloaded {got / (1024.0 * 1024.0):F1} / {t / (1024.0 * 1024.0):F1} MB",
                            });
                        }
                    }
                }

                // ── 3. Verify SHA-256 ─────────────────────────────────────
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Verifying,
                    Message = expectedSha256 == null
                        ? "Computing SHA-256 (no published hash to compare against)…"
                        : "Computing SHA-256 and comparing against published digest…",
                });

                string actualSha256 = await ComputeSha256Async(tmpZip, ct).ConfigureAwait(false);
                if (expectedSha256 == null)
                {
                    return new InstallResult
                    {
                        Outcome = InstallOutcome.HashUnavailable,
                        PreviousVersion = previousVersion,
                        ErrorDetail =
                            "GitHub release metadata did not publish a SHA-256 digest for "
                            + $"{TargetAssetName}. Computed digest: {actualSha256}. "
                            + "Proceed with caution.",
                    };
                }
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new InstallResult
                    {
                        Outcome = InstallOutcome.HashMismatch,
                        PreviousVersion = previousVersion,
                        ErrorDetail =
                            $"SHA-256 mismatch.\nExpected: {expectedSha256}\nActual:   {actualSha256}\n" +
                            "Aborting install — the download may be corrupt or tampered with.",
                    };
                }

                // ── 4. Extract bin/ffmpeg.exe ─────────────────────────────
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Extracting,
                    Message = "Extracting ffmpeg.exe from archive…",
                });

                string toolsDir = Path.GetDirectoryName(TargetPath)!;
                Directory.CreateDirectory(toolsDir);
                string stagedPath = TargetPath + ".new";
                if (File.Exists(stagedPath)) File.Delete(stagedPath);

                try
                {
                    await using var fs = File.OpenRead(tmpZip);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                    var entry = FindFfmpegEntry(zip);
                    if (entry == null)
                    {
                        return new InstallResult
                        {
                            Outcome = InstallOutcome.ExtractFailed,
                            PreviousVersion = previousVersion,
                            ErrorDetail = "Archive did not contain bin/ffmpeg.exe.",
                        };
                    }
                    await using var es = entry.Open();
                    await using var dst = File.Create(stagedPath);
                    await es.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
                    return new InstallResult
                    {
                        Outcome = InstallOutcome.ExtractFailed,
                        PreviousVersion = previousVersion,
                        ErrorDetail = $"Extraction failed: {ex.Message}",
                    };
                }

                // ── 5. Probe staged binary version ────────────────────────
                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.DetectingVersion,
                    Message = "Reading staged ffmpeg version…",
                });

                string? stagedVersion = TryReadVersionOf(stagedPath);

                // ── 6. Reject downgrade ───────────────────────────────────
                if (previousVersion != null && stagedVersion != null)
                {
                    int cmp = CompareVersions(stagedVersion, previousVersion);
                    if (cmp < 0)
                    {
                        try { File.Delete(stagedPath); } catch { }
                        return new InstallResult
                        {
                            Outcome = InstallOutcome.RejectedOlder,
                            PreviousVersion = previousVersion,
                            NewVersion = stagedVersion,
                            ErrorDetail =
                                "Downloaded build is older than the currently installed " +
                                "ffmpeg.exe — refusing to replace.",
                        };
                    }
                    if (cmp == 0)
                    {
                        try { File.Delete(stagedPath); } catch { }
                        return new InstallResult
                        {
                            Outcome = InstallOutcome.SkippedNotNewer,
                            PreviousVersion = previousVersion,
                            NewVersion = stagedVersion,
                        };
                    }
                }

                // ── 7. Atomic-ish swap into place ─────────────────────────
                try
                {
                    if (File.Exists(TargetPath))
                    {
                        string bak = TargetPath + ".bak";
                        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                        File.Replace(stagedPath, TargetPath, bak, ignoreMetadataErrors: true);
                        try { File.Delete(bak); } catch { }
                    }
                    else
                    {
                        File.Move(stagedPath, TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
                    return new InstallResult
                    {
                        Outcome = InstallOutcome.ExtractFailed,
                        PreviousVersion = previousVersion,
                        ErrorDetail = $"Failed to install ffmpeg.exe: {ex.Message}",
                    };
                }

                progress?.Report(new InstallProgress
                {
                    Phase = InstallPhase.Done,
                    Fraction = 1.0,
                    Message = "Install complete.",
                });

                return new InstallResult
                {
                    Outcome = previousVersion == null
                        ? InstallOutcome.Installed
                        : InstallOutcome.UpdatedFromOlder,
                    PreviousVersion = previousVersion,
                    NewVersion = stagedVersion ?? TryReadInstalledVersion(),
                };
            }
            catch (OperationCanceledException)
            {
                return new InstallResult
                {
                    Outcome = InstallOutcome.Cancelled,
                    PreviousVersion = previousVersion,
                };
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        private static string? TryReadVersionOf(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-hide_banner -version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                string stdout = p.StandardOutput.ReadToEnd();
                int nl = stdout.IndexOf('\n');
                string firstLine = (nl >= 0 ? stdout[..nl] : stdout).Trim();
                return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
            }
            catch { return null; }
        }

        /// <summary>
        /// Compares two raw "ffmpeg version …" first-lines. Returns &lt;0 when
        /// <paramref name="left"/> is older, 0 when equal, &gt;0 when newer.
        /// BtbN master builds carry an embedded build number (N-XXXXXX) and an
        /// embedded YYYYMMDD date; either is enough to order them. Falls
        /// through to ordinal-case-insensitive string compare when neither
        /// pattern matches (release builds like "ffmpeg version 7.1.2 …").
        /// </summary>
        public static int CompareVersions(string left, string right)
        {
            if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return 0;
            if (string.IsNullOrEmpty(left)) return -1;
            if (string.IsNullOrEmpty(right)) return 1;

            // 1. BtbN master build number ("N-118341-…").
            int lBuild = ExtractBuildNumber(left);
            int rBuild = ExtractBuildNumber(right);
            if (lBuild > 0 && rBuild > 0) return lBuild.CompareTo(rBuild);

            // 2. Embedded YYYYMMDD.
            int lDate = ExtractDate(left);
            int rDate = ExtractDate(right);
            if (lDate > 0 && rDate > 0) return lDate.CompareTo(rDate);

            // 3. Release tag (e.g., "7.1.2").
            var lSem = ExtractSemVer(left);
            var rSem = ExtractSemVer(right);
            if (lSem != null && rSem != null)
            {
                for (int i = 0; i < Math.Min(lSem.Length, rSem.Length); i++)
                {
                    int c = lSem[i].CompareTo(rSem[i]);
                    if (c != 0) return c;
                }
                return lSem.Length.CompareTo(rSem.Length);
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static int ExtractBuildNumber(string versionLine)
        {
            // Looks for "N-DDDDDD" — the BtbN master build number.
            int i = versionLine.IndexOf("N-", StringComparison.Ordinal);
            if (i < 0) return -1;
            i += 2;
            int j = i;
            while (j < versionLine.Length && char.IsDigit(versionLine[j])) j++;
            if (j == i) return -1;
            return int.TryParse(versionLine.AsSpan(i, j - i), out int n) ? n : -1;
        }

        private static int ExtractDate(string versionLine)
        {
            // Looks for a stand-alone YYYYMMDD (8 digits) in the line.
            for (int i = 0; i + 8 <= versionLine.Length; i++)
            {
                bool prevDigit = i > 0 && char.IsDigit(versionLine[i - 1]);
                if (prevDigit) continue;
                bool allDigits = true;
                for (int k = 0; k < 8; k++)
                {
                    if (!char.IsDigit(versionLine[i + k])) { allDigits = false; break; }
                }
                if (!allDigits) continue;
                bool nextDigit = i + 8 < versionLine.Length && char.IsDigit(versionLine[i + 8]);
                if (nextDigit) continue;
                int val = int.Parse(versionLine.AsSpan(i, 8));
                // Sanity check: 19000101 .. 21000101.
                if (val >= 19000101 && val < 21000101) return val;
            }
            return -1;
        }

        private static int[]? ExtractSemVer(string versionLine)
        {
            // Looks for "version X.Y[.Z]" after "ffmpeg version ".
            const string marker = "ffmpeg version ";
            int i = versionLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += marker.Length;
            int j = i;
            while (j < versionLine.Length && (char.IsDigit(versionLine[j]) || versionLine[j] == '.')) j++;
            if (j == i) return null;
            var parts = versionLine.AsSpan(i, j - i).ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var result = new int[parts.Length];
            for (int k = 0; k < parts.Length; k++)
                if (!int.TryParse(parts[k], out result[k])) return null;
            return result;
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            await using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static ZipArchiveEntry? FindFfmpegEntry(ZipArchive zip)
        {
            // BtbN GPL zip lays out as ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe.
            // Match any entry whose path ends with /bin/ffmpeg.exe (case-insensitive).
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            // Fallback: bare ffmpeg.exe at any depth.
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        private static (string? sha256, string? url) TryParseReleaseAsset(string json, string assetName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("assets", out var assets)) return (null, null);
                foreach (var a in assets.EnumerateArray())
                {
                    if (!a.TryGetProperty("name", out var nameEl)) continue;
                    string? name = nameEl.GetString();
                    if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) continue;

                    string? url = a.TryGetProperty("browser_download_url", out var urlEl)
                        ? urlEl.GetString()
                        : null;

                    string? sha = null;
                    if (a.TryGetProperty("digest", out var digestEl))
                    {
                        // Format: "sha256:<hex>". Older releases may omit this.
                        string? d = digestEl.GetString();
                        if (!string.IsNullOrEmpty(d) &&
                            d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha = d.Substring("sha256:".Length).Trim();
                        }
                    }
                    return (sha, url);
                }
            }
            catch { /* fall through */ }
            return (null, null);
        }
    }
}
