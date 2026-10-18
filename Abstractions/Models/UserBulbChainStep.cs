// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserBulbChainStep.cs
//
// One named step in a chain. Source is the body of:
//   Vec3 Step(Vec3 z, Vec3 c, int n, double[] __p, ChainCtx ctx)
// or Quat equivalent. The returned Vec3/Quat is stored in ctx under OutputName
// so later steps can reference it as `ctx.Get("name")`.
//
// #1365 — raises PropertyChanged so the User Bulb editor can recompile (and
// re-validate, for the step's error underline) when a step box is edited; the
// editor binds straight to these objects, so without it a step edit never
// reached the compiler.

using System.ComponentModel;

namespace FracturingFog.Models
{
    public sealed class UserBulbChainStep : INotifyPropertyChanged
    {
        private string _outputName = "out";
        private string _source = "return z * z + c;";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string OutputName
        {
            get => _outputName;
            set
            {
                if (_outputName == value) return;
                _outputName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputName)));
            }
        }

        public string Source
        {
            get => _source;
            set
            {
                if (_source == value) return;
                _source = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
            }
        }

        public UserBulbChainStep Clone() => new() { OutputName = OutputName, Source = Source };
    }
}
