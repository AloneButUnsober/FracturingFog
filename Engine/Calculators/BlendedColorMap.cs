// BlendedColorMap.cs
//
// IColorMap wrapper that returns a per-pixel lerp between two underlying
// colour maps. Used by the video slideshow's in-leg theme fade so the
// transition runs concurrently with zoom advancement instead of pausing the
// view to cross-fade two static buffers.
//
// Lifetime: a fade window builds an instance with From=current palette, To=
// next palette, T=0, sets it as the active ColorMap on FractalRenderHost,
// then ticks T toward 1 across the fade duration. When T reaches 1 the
// caller replaces the wrapper with the plain To palette on the host so the
// per-pixel double lookup cost disappears outside the fade window.
//
// Cost: one extra Map() call per pixel during the fade window (plus an
// integer-channel lerp). Single Calculate() per frame is preserved, so the
// zoom keeps advancing at its normal rate.

using FracturingFog.Interefaces;

namespace FracturingFog.Calculators
{
    /// <summary>Per-pixel lerp of two colour maps. <see cref="T"/> in [0,1]
    /// chooses From..To linearly across each ARGB channel.</summary>
    public sealed class BlendedColorMap : IColorMap, IColorMapWithPixelScale, INamedColorMap
    {
        private readonly IColorMap _from;
        private readonly IColorMap _to;
        private int _maxIterations;
        private double _pixelScale;
        private float _t;

        public BlendedColorMap(IColorMap from, IColorMap to, float t = 0f)
        {
            _from = from ?? throw new System.ArgumentNullException(nameof(from));
            _to = to ?? throw new System.ArgumentNullException(nameof(to));
            _t = Clamp01(t);
        }

        /// <summary>Blend factor in [0,1]. 0 = From only, 1 = To only.</summary>
        public float T
        {
            get => _t;
            set => _t = Clamp01(value);
        }

        public IColorMap From => _from;
        public IColorMap To => _to;

        public ColorPaletteType Type => _from.Type;

        public int MaxIterations
        {
            get => _maxIterations;
            set
            {
                _maxIterations = value;
                _from.MaxIterations = value;
                _to.MaxIterations = value;
            }
        }

        public uint InSetColor => LerpArgb(_from.InSetColor, _to.InSetColor, _t);

        public double PixelScale
        {
            set
            {
                _pixelScale = value;
                if (_from is IColorMapWithPixelScale fa) fa.PixelScale = value;
                if (_to is IColorMapWithPixelScale tb) tb.PixelScale = value;
            }
        }

        public int Map(float smooth, float distance, int iterations)
        {
            int a = _from.Map(smooth, distance, iterations);
            int b = _to.Map(smooth, distance, iterations);
            return LerpArgbInt(a, b, _t);
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            int a = _from.Map(smooth, distance, iterations, nx, ny);
            int b = _to.Map(smooth, distance, iterations, nx, ny);
            return LerpArgbInt(a, b, _t);
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny,
                       float finalZr, float finalZi, float dzdcR, float dzdcI)
        {
            int a = _from.Map(smooth, distance, iterations, nx, ny, finalZr, finalZi, dzdcR, dzdcI);
            int b = _to.Map(smooth, distance, iterations, nx, ny, finalZr, finalZi, dzdcR, dzdcI);
            return LerpArgbInt(a, b, _t);
        }

        // INamedColorMap — display the blended state as "FromName→ToName".
        public string DisplayName =>
            $"{(_from as INamedColorMap)?.DisplayName ?? "?"}→{(_to as INamedColorMap)?.DisplayName ?? "?"}";
        public string DisplayCategory => "Blend";
        public string DisplayDescription => "Per-pixel lerp of two colour maps for slideshow theme fade.";

        // ── helpers ──────────────────────────────────────────────────────────

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static uint LerpArgb(uint a, uint b, float t)
        {
            float ia = 1f - t;
            byte aA = (byte)(((a >> 24) & 0xFF) * ia + ((b >> 24) & 0xFF) * t);
            byte aR = (byte)(((a >> 16) & 0xFF) * ia + ((b >> 16) & 0xFF) * t);
            byte aG = (byte)(((a >> 8) & 0xFF) * ia + ((b >> 8) & 0xFF) * t);
            byte aB = (byte)((a & 0xFF) * ia + (b & 0xFF) * t);
            return ((uint)aA << 24) | ((uint)aR << 16) | ((uint)aG << 8) | aB;
        }

        private static int LerpArgbInt(int a, int b, float t) =>
            unchecked((int)LerpArgb(unchecked((uint)a), unchecked((uint)b), t));
    }
}
