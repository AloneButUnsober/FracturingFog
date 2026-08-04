// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/AsciiFxSpec.cs
//
// Shell-neutral request for the ASCII-native FX chain (#229). The UI builds it
// (it doesn't reference Engine) and the render host maps it to the Engine-side
// AsciiFxSettings. Default = no effects.

namespace FracturingFog.Render
{
    /// <summary>Which ASCII-native effects to apply, plus the animation clock.
    /// Mirrors the enable flags of the Engine's <c>AsciiFxSettings</c>.</summary>
    public readonly struct AsciiFxSpec
    {
        public bool HueCycle { get; }
        public bool Crt { get; }
        public bool Breathe { get; }
        /// <summary>Animation time in seconds for the time-varying effects.</summary>
        public double TimeSeconds { get; }

        public AsciiFxSpec(bool hueCycle, bool crt, bool breathe, double timeSeconds)
        {
            HueCycle = hueCycle; Crt = crt; Breathe = breathe; TimeSeconds = timeSeconds;
        }

        public bool AnyEnabled => HueCycle || Crt || Breathe;
        public bool AnyAnimated => HueCycle || Breathe;
    }
}
