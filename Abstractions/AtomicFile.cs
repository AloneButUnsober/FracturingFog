// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/AtomicFile.cs
//
// Crash-safe file writes for the store layer. Every auto-persisted user store
// (regions, colour themes, animations, slideshow configs, equations, settings,
// …) overwrites a single JSON file in place. A plain File.WriteAllText leaves a
// reader able to observe a half-written file, and — worse — a single bad or
// empty serialization destroys the only copy (this is how regions.json got
// wiped to "[]").
//
// AtomicFile.WriteAllText serializes to a sibling ".tmp" file and swaps it into
// place with File.Replace, which atomically renames the temp in and moves the
// previous good file aside to "<name>.bak". A crash mid-write leaves the old
// file intact; one bad save is recoverable in-place from the .bak.

using System;
using System.IO;

namespace FracturingFog.Abstractions
{
    /// <summary>Crash-safe, last-known-good-preserving file writes for the
    /// auto-persisted store layer.</summary>
    public static class AtomicFile
    {
        /// <summary>
        /// Write <paramref name="contents"/> to <paramref name="path"/>
        /// atomically. The previous good file (if any) is preserved as
        /// <c>path + ".bak"</c>. Uses default UTF-8 (no BOM) encoding, matching
        /// <see cref="File.WriteAllText(string,string)"/>. Throws on hard I/O
        /// failure — callers that persist stores already wrap Save in try/catch;
        /// the temp file is cleaned up on any failure so nothing lingers.
        /// </summary>
        public static void WriteAllText(string path, string contents)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, contents);

                if (File.Exists(path))
                {
                    try
                    {
                        // Atomic swap + one-level rollback copy.
                        File.Replace(tmp, path, path + ".bak");
                    }
                    catch (Exception ex) when (ex is IOException
                                            || ex is UnauthorizedAccessException
                                            || ex is PlatformNotSupportedException)
                    {
                        // File.Replace can fail on some filesystems (e.g. across
                        // certain network/temp mounts, or when the .bak is
                        // locked). Fall back to an overwrite move so the write
                        // still lands; we lose the .bak on this pass only.
                        File.Move(tmp, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }
}
