// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/UserDataBackup.cs
//
// #27 Phase 5a — a one-shot, timestamped snapshot of a user JSON file taken
// immediately BEFORE a store migration rewrites it in place. This is distinct
// from AtomicFile's rolling "<name>.bak" (which is overwritten on every save):
// a migration that converts a user's saved sources (e.g. raw C# -> DSL) is a
// destructive edit to text the user authored, so we keep a recoverable original
// that later ordinary saves cannot clobber. See [[feedback_no_save_over_examples]].

using System;
using System.IO;

namespace FracturingFog.Abstractions
{
    /// <summary>Timestamped pre-migration snapshots of user data files.</summary>
    public static class UserDataBackup
    {
        /// <summary>
        /// Copy <paramref name="path"/> to a timestamped sibling snapshot
        /// (<c>&lt;name&gt;.&lt;yyyyMMdd-HHmmss&gt;.&lt;reason&gt;.bak</c>) before a
        /// destructive in-place rewrite. No-op (returns null) when the file does
        /// not exist. Best-effort: any I/O failure is swallowed and returns null
        /// so a backup problem never blocks or crashes the migration's caller —
        /// but callers should only proceed with the destructive write when they
        /// are willing to run without the snapshot. Never overwrites an existing
        /// snapshot from the same second.
        /// </summary>
        /// <param name="path">The user JSON file about to be rewritten.</param>
        /// <param name="reason">Short filename-safe tag for the snapshot
        /// (e.g. "dslmigration"). Sanitised to <c>[A-Za-z0-9_-]</c>.</param>
        /// <returns>The backup file path, or null when nothing was backed up.</returns>
        public static string? SnapshotBeforeMigration(string path, string reason = "migration")
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                string dir = Path.GetDirectoryName(path) ?? ".";
                string name = Path.GetFileName(path);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string safeReason = Sanitize(reason);

                string backup = Path.Combine(dir, $"{name}.{stamp}.{safeReason}.bak");
                // A second migration within the same second must not clobber the
                // first snapshot; disambiguate with a short counter.
                for (int i = 1; File.Exists(backup) && i < 1000; i++)
                    backup = Path.Combine(dir, $"{name}.{stamp}.{safeReason}.{i}.bak");

                if (!File.Exists(backup)) File.Copy(path, backup);
                return backup;
            }
            catch
            {
                return null; // best-effort — never block the caller
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "migration";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
