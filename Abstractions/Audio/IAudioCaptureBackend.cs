using System;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Platform capture backend abstraction. Hosts (AudioCaptureDriver) treat
    /// every source uniformly through this interface; the backend hides the
    /// platform-specific bits (NAudio + WASAPI on Windows, file decode +
    /// silent synth pump on Linux/macOS, …).
    ///
    /// Threading: Start/Stop are called from the UI thread. DataAvailable and
    /// Failed fire from capture / decode threads — subscribers must marshal.
    /// </summary>
    public interface IAudioCaptureBackend : IDisposable
    {
        /// <summary>Sources this backend can drive. Unsupported flags are greyed in the picker UI.</summary>
        AudioBackendCapabilities Capabilities { get; }

        /// <summary>True between a successful Start and the next Stop (or Failed event).</summary>
        bool IsRunning { get; }

        /// <summary>
        /// Begin capturing from <paramref name="source"/>. <paramref name="filePath"/>
        /// is required when <paramref name="source"/> is <see cref="AudioSourceKind.File"/>.
        /// <paramref name="preferredFormat"/> is a hint; the backend may negotiate a
        /// different format and reports the actual format on each <see cref="DataAvailable"/>.
        /// Throws <see cref="NotSupportedException"/> if <paramref name="source"/> is
        /// not in <see cref="Capabilities"/>.
        /// </summary>
        void Start(AudioSourceKind source, AudioFormat preferredFormat, string? filePath);

        /// <summary>Stop capture. Safe to call when not running.</summary>
        void Stop();

        /// <summary>
        /// Interleaved float32 samples in [-1, 1] with the actual <see cref="AudioFormat"/>
        /// the backend is delivering. Fires on the capture / decode thread.
        /// </summary>
        event Action<ReadOnlyMemory<float>, AudioFormat>? DataAvailable;

        /// <summary>Backend hit a non-recoverable error and has stopped itself.</summary>
        event Action<Exception>? Failed;

        /// <summary>
        /// Backend reached its own end-of-stream (e.g. file playback finished). Driver
        /// transitions to Stopped and fires its own Stopped event after this.
        /// </summary>
        event Action? EndOfStream;
    }

    [Flags]
    public enum AudioBackendCapabilities
    {
        None = 0,
        SystemLoopback = 1 << 0,
        Microphone = 1 << 1,
        FilePlayback = 1 << 2,
        SynthPlayback = 1 << 3,
    }
}
