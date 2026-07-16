// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/AudioCapabilityProbe.cs
//
// Static OS-shape detection mirroring the backend selection logic in
// AvaloniaShellBootstrap.CreateAudioBackend — Windows → NAudio (full caps),
// Linux/macOS → noop backend (file + synth only). Used by Audio Settings
// dialog opened before a driver exists, so the source picker can grey
// unsupported options without instantiating the backend.
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
            return AudioBackendCapabilities.FilePlayback
                 | AudioBackendCapabilities.SynthPlayback;
        }
    }
}
