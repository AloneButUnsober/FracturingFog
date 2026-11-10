// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Animation/BatchAudioDrive.cs (#1432)
//
// Audio→param bindings (#268) played headless. The live view runs one
// AudioModulatorAnimator per enabled binding, ticked by the animation bus with
// the wall-clock dt and fed by the capture device. Here the same animators run
// on a baked, seekable source (OfflineAudioAnalysis): at output frame f the
// source is read at t = f / fps and each animator ticks with dt = 1 / fps, so
// attack / release smoothing and Rate mode (#1431, #1436) behave as live, and
// the render is reproducible from the file alone.
//
// Only animatable, non-complex settings of the rendered family are driven (the
// same filter as the live AudioModulationManager); others are reported. The
// drive runs after a timeline animation's pose, so audio wins on a shared
// setting. Calling ApplyFrame again for the same frame (motion-blur sub-frames)
// re-writes the frame's values without advancing the smoothing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FracturingFog.Audio;
using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation
{
    public sealed class BatchAudioDrive
    {
        private sealed class Clocked : IAudioModulationSource
        {
            private readonly IAudioModulationSource _inner;
            public double Time;
            public Clocked(IAudioModulationSource inner) => _inner = inner;
            public bool IsActive => _inner.IsActive;
            public long BeatCount => _inner.BeatCount;
            public long DownbeatCount => _inner.DownbeatCount;
            public AudioModulationFrame Sample() => _inner.SampleAt(Time);
            public AudioModulationFrame SampleAt(double seconds) => _inner.SampleAt(seconds);
        }

        private readonly Clocked _clock;
        private readonly List<(AudioModulatorAnimator Anim, PropertyInfo Prop)> _drives = new();
        private readonly object?[] _values;
        private readonly FractalParameters _target;
        private readonly double _fps;
        private int _lastFrame = int.MinValue;

        private BatchAudioDrive(FractalParameters target, Clocked clock, double fps,
            List<(AudioModulatorAnimator, PropertyInfo)> drives)
        {
            _target = target; _clock = clock; _fps = fps; _drives = drives;
            _values = new object?[drives.Count];
        }

        /// <summary>The settings being driven, in binding order.</summary>
        public IReadOnlyList<string> Driven => _drives.Select(d => d.Anim.Name).ToList();

        /// <summary>The region's enabled bindings with the command-line ones laid
        /// over them: a command-line binding replaces the region's for the same setting.</summary>
        public static List<AudioParamBinding> Merge(IEnumerable<AudioParamBinding>? region, IEnumerable<AudioParamBinding>? flags)
        {
            var list = new List<AudioParamBinding>();
            void Put(AudioParamBinding b)
            {
                if (b == null || !b.Enabled || string.IsNullOrWhiteSpace(b.ParamName)) return;
                list.RemoveAll(x => string.Equals(x.ParamName, b.ParamName, StringComparison.Ordinal));
                list.Add(b);
            }
            foreach (var b in region ?? Array.Empty<AudioParamBinding>()) Put(b);
            foreach (var b in flags ?? Array.Empty<AudioParamBinding>()) Put(b);
            return list;
        }

        /// <summary>Build the drive for <paramref name="target"/> (a <paramref name="type"/>
        /// render). Bindings that do not name an animatable scalar setting of the type are
        /// added to <paramref name="skipped"/> with the reason.</summary>
        public static BatchAudioDrive Create(FractalParameters target, FractalType type, IAudioModulationSource source,
            IEnumerable<AudioParamBinding> bindings, double fps, ICollection<string>? skipped = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(source);
            var clock = new Clocked(source);
            var byName = FractalAnimatableParamsMap.For(type)
                .Where(d => d.Kind != AnimatableParamKind.Complex)
                .ToDictionary(d => d.ParamName, StringComparer.Ordinal);
            var drives = new List<(AudioModulatorAnimator, PropertyInfo)>();
            foreach (var b in bindings)
            {
                if (b == null || !b.Enabled) continue;
                if (!byName.TryGetValue(b.ParamName, out var d))
                { skipped?.Add($"{b.ParamName}: not an audio-drivable setting of {type}"); continue; }
                var anim = AudioModulatorAnimator.TryCreate(target, b.ParamName, clock, b.Binding, d.Cost, d.Min, d.Max);
                var prop = typeof(FractalParameters).GetProperty(b.ParamName, BindingFlags.Public | BindingFlags.Instance);
                if (anim == null || prop == null) { skipped?.Add($"{b.ParamName}: not a number setting"); continue; }
                anim.IsEnabled = true;
                drives.Add((anim, prop));
            }
            return new BatchAudioDrive(target, clock, fps > 0 ? fps : 30, drives);
        }

        /// <summary>Drive the settings for output frame <paramref name="frame"/> (time
        /// frame / fps). Advances the smoothing once per new frame; a repeat call for the
        /// same frame re-writes that frame's values.</summary>
        public void ApplyFrame(int frame)
        {
            if (frame == _lastFrame)
            {
                for (int i = 0; i < _drives.Count; i++)
                    if (_values[i] != null) _drives[i].Prop.SetValue(_target, _values[i]);
                return;
            }
            _lastFrame = frame;
            _clock.Time = frame / _fps;
            for (int i = 0; i < _drives.Count; i++)
            {
                var (anim, prop) = _drives[i];
                anim.Tick(1.0 / _fps);   // an inactive source leaves the setting alone (the animator checks)
                _values[i] = prop.GetValue(_target);
            }
        }
    }
}
