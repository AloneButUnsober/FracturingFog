// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/AudioCapabilityProbe.cs
//
// Static OS-shape detection mirroring the backend selection logic in
// AvaloniaShellBootstrap.CreateAudioBackend — Windows → NAudio (full caps),
// Linux/macOS with the OpenAL runtime → mic (+ Linux loopback) + file + synth,
// Linux/macOS without it → file + synth only. Used by the Audio Settings dialog
// opened before a driver exists, so the source picker can grey unsupported
// options without instantiating the backend.
//
// #271 — the OpenAL branch is a best-effort *advertisement*: loopback depends on
// a monitor capture device that only a live enumeration can confirm, so this
// promises SystemLoopback on Linux and the real backend (OpenAlAudioBackend)
// withholds it at Start if no ".monitor" device exists. macOS never advertises
// loopback (no native monitor source without a virtual device).
//
// Carved out of AvaloniaShellBootstrap (Wave 1.C1, 2026-06-22) so the
// cross-platform Hosting.AvaloniaDialogs can probe capabilities without
// pulling AvaloniaShellBootstrap (still WinExe-only).

using System;

namespace FracturingFog.Audio
{
    public static class AudioCapabilityProbe
    {
        public static AudioBackendCapabilities Detect()
        {
            if (OperatingSystem.IsWindows())
            {
                return AudioBackendCapabilities.SystemLoopback
                     | AudioBackendCapabilities.Microphone
                     | AudioBackendCapabilities.FilePlayback
                     | AudioBackendCapabilities.SynthPlayback;
            }

            var caps = AudioBackendCapabilities.FilePlayback
                     | AudioBackendCapabilities.SynthPlayback;

            if (OpenAlRuntime.IsAvailable())
            {
                caps |= AudioBackendCapabilities.Microphone;
                // Linux monitor sinks give system loopback; macOS has none.
                if (OperatingSystem.IsLinux())
                    caps |= AudioBackendCapabilities.SystemLoopback;
            }

            return caps;
        }
    }
}
