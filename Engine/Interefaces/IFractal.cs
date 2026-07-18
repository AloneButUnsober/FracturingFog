// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Interefaces
{
    public interface IFractal : IRenderable
    {
        public FractalType FractalType { get; }

        public FractalView FractalView { get; set; }

        public bool PreviewMode { get; set; }

        Task<int[]> RenderAsync(CancellationToken token);
    }
}
