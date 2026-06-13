using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using SkiaSharp;
//using FracturingFog.Enums;

namespace FracturingFog.Models
{
    public abstract class FractalBase : IFractal
    {

        #region Constuctors

        public FractalBase(int Width = 800, int Height = 600)
        {
            renderSettings = new RenderSettings(Width, Height);
            renderHeight = renderSettings.Height;
            renderWidth = renderSettings.Width;

            InitializeBuffers();
            InitializeLaneOffsets();
        }

        #endregion Constuctors

        #region Protected Members

        protected readonly FractalType fractalType = FractalType.Mandelbrot;

        protected int[] colorBuffer = null!;

        protected IColorMap? colorMap = null;

        protected CancellationTokenSource? _cancelTokenSource;

        protected float[] distanceBuffer = null!;

        protected FractalView fractalView = FractalViews.Classic;

        protected int[] outputBuffer = null!;

        protected Vector<float> _laneOffsets;

        protected bool previewMode = false;

        protected QualityLevel qualityLevel = QualityLevel.Normal;

        protected int renderHeight;

        protected RenderSettings renderSettings = new RenderSettings();

        protected int renderWidth;

        protected float[] smoothBuffer = null!;

        protected bool viewChanged = true;

        protected void InitializeBuffers()
        {
            outputBuffer = new int[renderSettings.Dimensions];
            smoothBuffer = new float[renderSettings.Dimensions];
            colorBuffer = new int[renderSettings.Dimensions];
            distanceBuffer = new float[renderSettings.Dimensions];
        }

        protected IEnumerable<(int startX, int startY, int endX, int endY)> GetTiles()
        {
            for (int y = 0; y < renderSettings.Height; y += Fractals.TILESIZE)
            {
                for (int x = 0; x < renderSettings.Width; x += Fractals.TILESIZE)
                {
                    int endX = System.Math.Min(x + Fractals.TILESIZE, renderSettings.Width);
                    int endY = System.Math.Min(y + Fractals.TILESIZE, renderSettings.Height);
                    yield return (x, y, endX, endY);
                }
            }
        }

        protected void UpdateViewBounds() => renderSettings.UpdateViewBounds();

        protected void UpdateIterations() => renderSettings.UpdateIterations(qualityLevel);

        #endregion Protected Members 

        #region Events

        public event Action? RenderCompleted;

        #endregion Events

        #region Public Members

        /// <summary>
        /// Fractal Type
        /// </summary>
        public FractalType FractalType { get { return fractalType; } }

        public QualityLevel QualityLevel { get { return qualityLevel; } set { qualityLevel = value; } }

        public IColorMap? ColorMap { get { return colorMap; } set { colorMap = value; } }

        public FractalView FractalView { get { return fractalView; } set { fractalView = value; ResetView(); } }

        public int Width
        {
            get { return renderSettings.Width; }
            set
            {
                renderSettings.Width = value;
                viewChanged = true;
                UpdateViewBounds();
            }
        }

        public int Height
        {
            get { return renderSettings.Height; }
            set
            {
                renderSettings.Height = value;
                viewChanged = true;
                UpdateViewBounds();
            }
        }

        public bool PreviewMode { get { return previewMode; } set { previewMode = value; } }

        public bool UseDoublePrecision { get { return renderSettings.UseDoublePrecision; } set { renderSettings.UseDoublePrecision = value; } }

        public RenderSettings RenderSettings { get { return renderSettings; } set { renderSettings = value; } }

        public int Iterations { get { return renderSettings.Iterations; } set { renderSettings.Iterations = value; } }

        public float RealMin { get { return renderSettings.RealMin; } set { renderSettings.RealMin = value; } }

        public float RealMax { get { return renderSettings.RealMax; } set { renderSettings.RealMax = value; } }

        public float ImagMin { get { return renderSettings.ImagMin; } set { renderSettings.ImagMin = value; } }

        public float ImagMax { get { return renderSettings.ImagMax; } set { renderSettings.ImagMax = value; } }

        public float CenterX
        {
            get { return renderSettings.CenterX; }
            set
            {
                renderSettings.CenterX = value;
                viewChanged = true;
            }
        }

        public float CenterY
        {
            get { return renderSettings.CenterY; }
            set
            {
                renderSettings.CenterY = value;
                viewChanged = true;
            }
        }

        public float Scale
        {
            get { return renderSettings.Scale; }
            set
            {
                renderSettings.Scale = value;
                viewChanged = true;
            }
        }

        public bool AutoIterations { get { return renderSettings.AutoIterations; } set { renderSettings.AutoIterations = value; } }

        public int MaxIterations { get { return renderSettings.MaxIterations; } set { renderSettings.MaxIterations = value; } }

        public int MinIterations { get { return renderSettings.MinIterations; } set { renderSettings.MinIterations = value; } }

        public int[] OutputBuffer { get { return outputBuffer; } }

        public int[] ColorBuffer => colorBuffer;

        #endregion Public Members

        #region Public Methods

        public void Render(CancellationToken token)
        {
            if (viewChanged)
            {
                UpdateIterations();
                UpdateViewBounds();
                viewChanged = false;
            }

            int effectiveIterations = previewMode ?
                System.Math.Min(20, renderSettings.Iterations) :
                renderSettings.Iterations;

            renderWidth = previewMode && renderSettings.LowQuality ? renderSettings.HalfWidth : renderSettings.Width;
            renderHeight = previewMode && renderSettings.LowQuality ? renderSettings.HalfHeight : renderSettings.Height;

            Build(effectiveIterations, token);
            RenderCompleted?.Invoke();
        }

        public async Task<int[]> RenderAsync(CancellationToken token)
        {
            //_cts = new CancellationTokenSource();

            return await Task.Run(() =>
            {
                Render(token);
                return colorBuffer;
            }, token);
        }

        public void CancelRender()
        {
            _cancelTokenSource?.Cancel();
        }

        public void ResetView(int Width = 800, int Height = 600) => renderSettings.Reset(Width, Height);

        public void ApplyView(FractalView view) => renderSettings.ApplyView(view);

        public bool SaveImage(string path)
        {
            // ToDo: Use quality to determine which save method to use
            return SaveImageFast(path);
        }

        #endregion Public Methods

        #region Private Methods

        private bool SaveImageFast(string path)
        {
            if (File.Exists(path)) File.Delete(path);

            int w = renderSettings.Width;
            int h = renderSettings.Height;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            unsafe
            {
                fixed (int* src = colorBuffer)
                {
                    using var bmp = new SKBitmap();
                    bmp.InstallPixels(info, (IntPtr)src, info.RowBytes);
                    using var image = SKImage.FromBitmap(bmp);
                    using var data = image.Encode(SKEncodedImageFormat.Bmp, 100);
                    using var fs = File.OpenWrite(path);
                    data.SaveTo(fs);
                }
            }

            return File.Exists(path);
        }

        #endregion Private Methods

        #region Abstract Methods

        public abstract void Build(int iterations, CancellationToken token);

        #endregion Abstract Methods

        #region Protected Methods

        protected void InitializeLaneOffsets()
        {
            int _lanes = Vector<float>.Count;
            float[] _offsets = new float[_lanes];
            for (int i = 0; i < _lanes; i++)
            {
                _offsets[i] = i;
            }

            _laneOffsets = new Vector<float>(_offsets);
        }

        protected void ComputeColorBuffers()
        {
            int total = renderWidth * renderHeight;

            if (previewMode)
            {
                for (int i = 0; i < total; i++)
                {
                    int iter = outputBuffer[i];
                    byte v = (byte)(iter % 256);
                    colorBuffer[i] = unchecked((int)0xFF000000 | (v << 16) | (v << 8) | v);
                    //continue;
                    //float t = iter / (float)_renderSettings.Iterations;
                    //_colorBuffer[i] = _colorMap != null ?
                    //    _colorMap.Map(iter, 0f, _renderSettings.Iterations) :
                    //    Fractals.HsvToRgb(t, 1f, iter == _renderSettings.Iterations ? 0f : 1f);
                }
            }
            else
            {
                for (int i = 0; i < total; i++)
                {
                    float smooth = smoothBuffer[i];
                    float hue = (smooth * 0.02f) % 1.0f; // scale factor controls color cycling
                    hue -= MathF.Floor(hue);

                    float distance = distanceBuffer[i];
                    float saturation = 1.0f;
                    float baseValue = smooth < renderSettings.Iterations ? 1.0f : 0.0f; // interior = black
                    float lightness = 1.0f - MathF.Min(distance * 0.05f, 1.0f); // scale factor controls brightness falloff
                    float value = baseValue * lightness;

                    colorBuffer[i] = colorMap != null ?
                        colorMap.Map(smooth, distance, renderSettings.Iterations) :
                        Fractals.HsvToRgb(hue, saturation, value);
                }
            }
        }

        protected void ClearBuffers()
        {
            Array.Clear(outputBuffer);
            Array.Clear(colorBuffer);
            Array.Clear(distanceBuffer);
            Array.Clear(smoothBuffer);
        }

        //public abstract Task<int[]> RenderAsync(CancellationToken token);
        //public abstract void Render(CancellationToken token);

        #endregion Protected Methods
    }
}