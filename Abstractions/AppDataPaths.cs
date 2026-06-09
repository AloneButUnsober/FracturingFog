// Abstractions/AppDataPaths.cs
//
// Central resolver for FracturingFog's per-user data root. Every store
// (slideshow configs, color themes, user equations, audio settings, etc.)
// asks AppDataPaths.Root for its base directory instead of computing
// %APPDATA%\FracturingFog itself. That gives us a single point to honour
// a user-selected override path.
//
// Override mechanism: an anchor file lives at the fixed default location
// (%APPDATA%\FracturingFog\app-data-root.txt). Its single non-blank line
// is interpreted as the absolute path to the actual data root. We have to
// keep the anchor at the fixed default location to solve the chicken-and-
// egg problem — we need to find the override before any store loads, and
// stores don't know where to look without it.
//
// Resolution order:
//   1. In-process override set via SetRoot (used by the picker UI).
//   2. Anchor file at default location, if it points to an existing dir.
//   3. Default %APPDATA%\FracturingFog.
//
// I/O failures are non-fatal: we always fall back to the default root
// rather than crashing, mirroring the rest of the store layer.

using System;
using System.IO;

namespace FracturingFog.Abstractions
{
    public static class AppDataPaths
    {
        private const string AnchorFileName = "app-data-root.txt";
        private const string AppFolderName = "FracturingFog";

        private static string? _cachedRoot;
        private static readonly object _gate = new();

        /// <summary>Default root — %APPDATA%\FracturingFog. Always the same
        /// path regardless of override; used to locate the anchor file.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);

        /// <summary>Active data root. Honours an override anchor file at the
        /// default location, otherwise returns the default. Result is cached
        /// for the process lifetime once first read; <see cref="SetRoot"/>
        /// updates the cache.</summary>
        public static string Root
        {
            get
            {
                if (_cachedRoot != null) return _cachedRoot;
                lock (_gate)
                {
                    if (_cachedRoot != null) return _cachedRoot;
                    _cachedRoot = ResolveRoot();
                    return _cachedRoot;
                }
            }
        }

        /// <summary>Convenience: <see cref="Root"/> joined with a relative
        /// filename. Equivalent to Path.Combine(Root, fileName).</summary>
        public static string Combine(string fileName) => Path.Combine(Root, fileName);

        /// <summary>Path to the anchor file at the fixed default location.</summary>
        public static string AnchorPath => Path.Combine(DefaultRoot, AnchorFileName);

        /// <summary>Update the active data root and persist the choice.
        /// Optionally copy existing files from the previous root so the
        /// user sees their data at the new location.
        ///
        /// Throws on I/O failures so the caller (picker UI) can surface
        /// the error to the user — unlike Root resolution, this is an
        /// explicit user action and silent failure would be confusing.</summary>
        public static void SetRoot(string newRoot, bool migrateFiles)
        {
            if (string.IsNullOrWhiteSpace(newRoot))
                throw new ArgumentException("Root path must not be blank.", nameof(newRoot));

            string normalized = Path.GetFullPath(newRoot);
            Directory.CreateDirectory(normalized);

            string previous = Root;
            if (migrateFiles && !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
                CopyFolder(previous, normalized);

            Directory.CreateDirectory(DefaultRoot);
            File.WriteAllText(AnchorPath, normalized);

            lock (_gate) { _cachedRoot = normalized; }
        }

        /// <summary>Remove the override and revert to <see cref="DefaultRoot"/>.
        /// Does not delete or move user data — only erases the anchor.</summary>
        public static void ClearOverride()
        {
            try { if (File.Exists(AnchorPath)) File.Delete(AnchorPath); }
            catch { /* non-fatal */ }
            lock (_gate) { _cachedRoot = DefaultRoot; }
        }

        private static string ResolveRoot()
        {
            try
            {
                if (File.Exists(AnchorPath))
                {
                    string raw = File.ReadAllText(AnchorPath).Trim();
                    if (!string.IsNullOrEmpty(raw) && Directory.Exists(raw))
                        return Path.GetFullPath(raw);
                }
            }
            catch { /* fall through to default */ }
            return DefaultRoot;
        }

        private static void CopyFolder(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);

            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                // Skip the anchor itself — it always lives at the default
                // location and must not propagate to override roots.
                if (string.Equals(name, AnchorFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string target = Path.Combine(dst, name);
                if (File.Exists(target)) continue; // never clobber existing
                File.Copy(file, target);
            }

            foreach (string sub in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(sub);
                CopyFolder(sub, Path.Combine(dst, name));
            }
        }
    }
}
