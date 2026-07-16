// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserBulbParam.cs
//
// Named scalar exposed to user step source as a local variable. Lets the
// user tweak formula constants live without recompiling. Name must be a
// valid C# identifier; duplicates rejected at save time.

namespace FracturingFog.Models
{
    public sealed class UserBulbParam
    {
        public string Name { get; set; } = "a";
        public double Value { get; set; } = 0.0;
        public double Min { get; set; } = -2.0;
        public double Max { get; set; } = 2.0;

        public UserBulbParam Clone() => new()
        {
            Name = Name, Value = Value, Min = Min, Max = Max
        };
    }
}
