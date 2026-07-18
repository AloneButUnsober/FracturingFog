// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ChainCtx.cs
//
// Per-iteration context passed to each chain step. Holds named intermediate
// outputs from earlier steps in the same iteration.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    public sealed class ChainCtx
    {
        private readonly Dictionary<string, Vec3> _vec3 = new();
        private readonly Dictionary<string, Quat> _quat = new();

        public Vec3 Get(string name) => _vec3.TryGetValue(name, out var v) ? v : Vec3.Zero;
        public Quat GetQ(string name) => _quat.TryGetValue(name, out var q) ? q : Quat.Zero;
        public void Set(string name, Vec3 v) => _vec3[name] = v;
        public void SetQ(string name, Quat q) => _quat[name] = q;
        public void Clear() { _vec3.Clear(); _quat.Clear(); }
    }
}
