namespace FracturingFog.Audio
{
    public enum AudioSourceKind
    {
        /// <summary>Captures whatever the system is currently playing (Spotify, browser, etc.).</summary>
        SystemLoopback,
        /// <summary>Plays a local audio file (MP3/WAV/FLAC/OGG) and analyzes it.</summary>
        File,
        /// <summary>Microphone input.</summary>
        Microphone,
        /// <summary>Internally generated fractal audio (closed-loop showcase mode).</summary>
        FractalSynth,
    }

    public sealed class AudioSettings
    {
        /// <summary>Master enable. When false, slideshow falls back to fixed-duration timing.</summary>
        public bool Enabled { get; set; }

        public AudioSourceKind Source { get; set; } = AudioSourceKind.SystemLoopback;

        /// <summary>Path to file when <see cref="Source"/> = <see cref="AudioSourceKind.File"/>.</summary>
        public string? FilePath { get; set; }

        /// <summary>Onset detector sensitivity (0..1). Higher = more beats reported. Default 0.5.</summary>
        public float Sensitivity { get; set; } = 0.5f;

        /// <summary>Number of beats per color-theme change. Default 8 (~2 bars at 4/4).</summary>
        public int BeatsPerTheme { get; set; } = 8;

        /// <summary>Number of beats per region change. Default 32 (~8 bars).</summary>
        public int BeatsPerRegion { get; set; } = 32;

        /// <summary>If true, route synth output through the analyzer for closed-loop sync.</summary>
        public bool RouteSynthThroughAnalyzer { get; set; }

        /// <summary>Render the generated fractal audio to the speakers.</summary>
        public bool PlaySynthOutput { get; set; } = true;

        /// <summary>BPM of the fractal synth arpeggio. Default 120.</summary>
        public double SynthBpm { get; set; } = 120;
    }
}
