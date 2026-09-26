// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/FractalRenderHost.LiveRecord.cs
//
// Instant record (#944) — partial of FractalRenderHost implementing
// ILiveRecordingController. Records the render window as-is by sampling the
// presented buffer: every full present (calculations, recolors, post-FX
// repaints, video-zoom / slideshow / animation frames, external PresentBuffer
// cross-fades) raises FrameBufferChanged, which marks the recorder dirty; the
// recorder's own pump pulls a coherent copy through SnapshotFrame. Nothing in
// the render pipeline changes, so recording is zero-cost when off.
//
// Low-res progressive previews shown mid-drag (stale uploads) bypass
// _lastUploadedBuffer and are therefore not captured — the recording shows
// the settled frames.

using System;
using System.IO;

using FracturingFog.Imaging;
using FracturingFog.Render;

namespace FracturingFog.Rendering
{
    public sealed partial class FractalRenderHost : ILiveRecordingController
    {
        private readonly object _liveRecordLock = new();
        private LiveFrameRecorder? _liveRecorder;

        /// <inheritdoc/>
        public event EventHandler? LiveRecordingStateChanged;

        /// <inheritdoc/>
        public bool IsLiveRecording { get { lock (_liveRecordLock) return _liveRecorder != null; } }

        /// <inheritdoc/>
        public TimeSpan LiveRecordingElapsed { get { lock (_liveRecordLock) return _liveRecorder?.Elapsed ?? TimeSpan.Zero; } }

        /// <inheritdoc/>
        public int LiveRecordingFrameCount { get { lock (_liveRecordLock) return _liveRecorder?.FrameCount ?? 0; } }

        /// <inheritdoc/>
        public bool StartLiveRecording(int captureFps)
        {
            if (_disposed) return false;
            lock (_liveRecordLock)
            {
                if (_liveRecorder != null) return false;
                SnapshotFrame(out int w, out int h);
                if (w < 16 || h < 16)
                {
                    RaiseStatus("Recording not started — nothing is displayed yet.");
                    return false;
                }
                string folder = Path.Combine(Path.GetTempPath(), $"fracturingfog_live_{Guid.NewGuid():N}");
                try
                {
                    var rec = new LiveFrameRecorder(folder, captureFps, SnapshotLiveFrame);
                    FrameBufferChanged += OnLiveRecordFrameChanged;
                    rec.Start();
                    _liveRecorder = rec;
                }
                catch (Exception ex)
                {
                    FrameBufferChanged -= OnLiveRecordFrameChanged;
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
                    RaiseStatus($"Recording failed to start: {ex.Message}");
                    return false;
                }
            }
            RaiseStatus($"● Recording ({Math.Clamp(captureFps, LiveFrameRecorder.MinFps, LiveFrameRecorder.MaxFps)} fps)…");
            LiveRecordingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <inheritdoc/>
        public LiveRecordingResult? StopLiveRecording()
        {
            LiveFrameRecorder? rec;
            lock (_liveRecordLock)
            {
                rec = _liveRecorder;
                _liveRecorder = null;
                FrameBufferChanged -= OnLiveRecordFrameChanged;
            }
            if (rec == null) return null;

            LiveRecordingResult? result = null;
            try { result = rec.Stop(); }
            catch (Exception ex) { RaiseStatus($"Recording failed: {ex.Message}"); }
            finally { rec.Dispose(); }

            RaiseStatus(result == null
                ? "Recording stopped — nothing captured."
                : $"Recording stopped — {result.DurationSeconds:0.0}s, {result.Frames.Count} unique frames.");
            LiveRecordingStateChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        private void OnLiveRecordFrameChanged(object? sender, EventArgs e)
        {
            // Read without the lock — a stale recorder ref just marks a
            // recorder that is already stopping, which is harmless.
            _liveRecorder?.NotifyFrameChanged();
        }

        private uint[]? SnapshotLiveFrame(out int width, out int height)
        {
            if (_disposed) { width = height = 0; return null; }
            return SnapshotFrame(out width, out height);
        }

        // Host teardown: abandon an in-flight recording and its temp folder.
        private void DisposeLiveRecording()
        {
            LiveFrameRecorder? rec;
            lock (_liveRecordLock)
            {
                rec = _liveRecorder;
                _liveRecorder = null;
                FrameBufferChanged -= OnLiveRecordFrameChanged;
            }
            try { rec?.Dispose(); } catch { }
        }
    }
}
