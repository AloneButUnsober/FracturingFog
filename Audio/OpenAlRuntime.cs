// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/OpenAlRuntime.cs
//
// #271 (parent #58) — cheap presence check for the OpenAL native runtime.
// Used two ways:
//   1. AvaloniaShellBootstrap.CreateAudioBackend picks OpenAlAudioBackend
//      only when the library actually loads, else falls back to NoopAudioBackend
//      (file + synth still work — no live mic/loopback).
//   2. AudioCapabilityProbe / the lazy setup prompt query this so the source
//      picker greys mic/loopback (and Tier B offers a package-manager install)
//      when the runtime is absent.
//
// Detection is via NativeLibrary.TryLoad, which honours the default probe path
// (app-local native dir first — so the bundled Silk.NET.OpenAL.Soft.Native RID
// asset counts — then the OS loader search). TryLoad never throws and the
// handle is freed immediately, so this does NOT open an audio device or touch
// ALSA/PulseAudio; it only confirms the shared object is resolvable + mappable.

using System;
using System.Runtime.InteropServices;

namespace FracturingFog.Audio
{
    public static class OpenAlRuntime
    {
        private static bool? s_cached;

        /// <summary>
        /// Candidate library names by platform. The bundled OpenAL Soft asset
        /// exports "soft_oal" (Windows) / "libopenal" (Unix); a system install
        /// exposes the versioned soname. Silk.NET's own resolver tries the same
        /// set, so a positive result here means Silk can bind at Start-time too.
        /// </summary>
        private static string[] CandidateNames()
        {
            if (OperatingSystem.IsWindows())
                return new[] { "soft_oal.dll", "OpenAL32.dll", "openal.dll" };
            if (OperatingSystem.IsMacOS())
                return new[] { "libopenal.1.dylib", "libopenal.dylib", "soft_oal.dylib" };
            // Linux / other Unix.
            return new[] { "libopenal.so.1", "libopenal.so", "libsoft_oal.so", "soft_oal.so" };
        }

        /// <summary>
        /// True when the OpenAL native runtime can be loaded. Result is cached
        /// after the first successful probe (a runtime installed mid-session is
        /// re-detected via <see cref="Refresh"/>, which the Tier B "Rescan"
        /// button calls).
        /// </summary>
        public static bool IsAvailable()
        {
            if (s_cached is bool cached) return cached;
            bool ok = Probe();
            // Only cache a positive result. A negative may flip to positive after
            // the user installs the package and clicks Rescan; caching false would
            // wrongly pin the "unavailable" banner for the rest of the session.
            if (ok) s_cached = true;
            return ok;
        }

        /// <summary>Force a re-probe (Tier B rescan after a manual install).</summary>
        public static bool Refresh()
        {
            s_cached = null;
            return IsAvailable();
        }

        private static bool Probe()
        {
            foreach (var name in CandidateNames())
            {
                try
                {
                    if (NativeLibrary.TryLoad(name, out IntPtr handle))
                    {
                        NativeLibrary.Free(handle);
                        return true;
                    }
                }
                catch
                {
                    // Any load fault (bad arch, missing transitive dep) → treat
                    // as "this candidate unusable", try the next name.
                }
            }
            return false;
        }
    }
}
