// Server/WorkDirSweeper.cs
// Deletes leftover job-* subdirs at server start. The normal FFServer
// HandleRender path cleans its own workdir under both success and failure
// branches, but a hard kill (Task Manager / SIGKILL / OOM / power loss)
// skips that cleanup. Sweeping on the next start keeps the work folder
// from accumulating GBs of orphan PNG frame sets over weeks.
//
// OPERATOR NOTE: the sweeper matches subdir name pattern "job-*" only.
// Do NOT name any folder you want to KEEP with that prefix — e.g. a
// keepsake archive like "job-2026Q1-final" would be deleted on the next
// server start if its mtime exceeds WorkDirStaleHours. Use a different
// prefix (e.g. "archive-...") or move keepers outside WorkDir.

using System;
using System.IO;

namespace FracturingFog.Server;

public static class WorkDirSweeper
{
    /// <summary>Delete every <c>job-*</c> subdir under <paramref name="workDir"/>
    /// older than <paramref name="staleAgeHours"/>. Errors are written to
    /// <paramref name="log"/> but never thrown — sweep is best-effort.</summary>
    public static int Sweep(string workDir, double staleAgeHours, Action<string>? log = null)
    {
        if (staleAgeHours <= 0) return 0;
        if (!Directory.Exists(workDir)) return 0;

        int deleted = 0;
        DateTime cutoff = DateTime.UtcNow.AddHours(-staleAgeHours);
        string[] entries;
        try { entries = Directory.GetDirectories(workDir, "job-*", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { log?.Invoke($"workdir sweep: enumerate failed: {ex.Message}"); return 0; }

        foreach (string dir in entries)
        {
            try
            {
                // LastWriteTime is the latest mutation of the folder OR any
                // top-level child. Frame-write loops touch the folder every
                // frame, so any in-flight render shows recent timestamps.
                DateTime ts = Directory.GetLastWriteTimeUtc(dir);
                if (ts > cutoff) continue;
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"workdir sweep: skip '{Path.GetFileName(dir)}': {ex.Message}");
            }
        }
        return deleted;
    }
}
