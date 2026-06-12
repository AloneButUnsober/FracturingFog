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
