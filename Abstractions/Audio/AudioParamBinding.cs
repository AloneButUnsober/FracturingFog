// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Audio
{
    /// <summary>
    /// A persisted audio→param modulation entry (#268 / Audio-Reactive Phase 4
    /// fast-follow): one parameter driven by an <see cref="AudioModulationBinding"/>,
    /// plus whether that drive is currently enabled. Saved on a region so its audio
    /// reactivity comes back on recall — the in-session
    /// <c>AudioModulationManager</c> exports these on save and hydrates from them on
    /// region jump. Plain serializable DTO.
    /// </summary>
    public sealed class AudioParamBinding
    {
        /// <summary>Target parameter name (a <c>FractalParameters</c> property name,
        /// matching the animatable-param descriptors).</summary>
        public string ParamName { get; set; } = string.Empty;

        /// <summary>Whether audio drive is active for this param.</summary>
        public bool Enabled { get; set; }

        /// <summary>The signal → curve/gain/bias/range mapping.</summary>
        public AudioModulationBinding Binding { get; set; } = new();
    }
}
