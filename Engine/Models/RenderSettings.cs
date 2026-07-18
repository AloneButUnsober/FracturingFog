// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

//using FracturingFog.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class RenderSettings
    {

        #region Private Members

        private int _width = 800;

        private int _height = 600;

        private float _centerX = -0.75f;

        private float _centerY = 0.0f;

        private float _scale = 3.5f;

        private bool _autoIterations = true;

        private int _iterations = 250;

        private int _maxIterations = 2000;

        private int _minIterations = 50;

        private float _realMin = -2.5f;

        private float _realMax = 1.0f;

        private float _imagMin = -1.5f;

        private float _imagMax = 1.5f;

        private float _realStep;

        private float _imagStep;

        private bool _useDoublePrecision = false;

        #endregion Private Members

        #region Public Members

        public int Width { get { return _width; } set { _width = value; } }

        public int Height { get { return _height; } set { _height = value; } }

        public int HalfWidth { get { return (int)(_width * 0.5); } }

        public int HalfHeight { get { return (int)(_height * 0.5); } }

        public float CenterX { get { return _centerX; } set { _centerX = value; } }

        public float CenterY { get { return _centerY; } set { _centerY = value; } }

        public float Scale { get { return _scale; } set { _scale = value; } }

        public bool AutoIterations { get { return _autoIterations; } set { _autoIterations = value; } }

        public int Iterations { get { return _iterations; } set { _iterations = value; } }

        public int MaxIterations { get { return _maxIterations; } set { _maxIterations = value; } }

        public int MinIterations { get { return _minIterations; } set { _minIterations = value; } }

        public float RealMin { get { return _realMin; } set { _realMin = value; } }

        public float RealMax { get { return _realMax; } set { _realMax = value; } }

        public float ImagMin { get { return _imagMin; } set { _imagMin = value; } }

        public float ImagMax { get { return _imagMax; } set { _imagMax = value; } }

        public float RealStep { get { return _realStep; } set { _realStep = value; } }

        public float ImagStep { get { return _imagStep; } set { _imagStep = value; } }

        public bool UseDoublePrecision { get { return _useDoublePrecision; } set { _useDoublePrecision = value; } }

        public int Dimensions => _width * _height;

        public bool LowQuality
        {
            get { return !Fractals.AVX2 && Fractals.BATCHSIZE <= 4; }
        }

        #endregion Public Members

        #region Constructors

        public RenderSettings(int Width = 800, int Height = 600) => ResetAll(Width, Height);

        #endregion Constructors

        #region Public Methods

        public void Reset() => ResetAll(800, 600);

        public void Reset(int Width = 800, int Height = 600) => ResetAll(Width, Height);

        public void UpdateViewBounds() => UpdViewBounds();

        public void UpdateIterations(QualityLevel Quality) => UpdIterations(Quality);

        public void ApplyView(FractalView view)
        {
            _centerX = view.CenterX;
            _centerY = view.CenterY;
            _scale = view.Scale;
            UpdViewBounds();
        }

        public (int width, int height, int iterations) GetProfile(RenderProfile profile)
        {
            int baseIter = Iterations;

            return profile switch
            {
                RenderProfile.Preview => (
                    Width / 2,
                    Height / 2,
                    System.Math.Min(50, baseIter)
                ),
                RenderProfile.Final => (
                    Width,
                    Height,
                    baseIter
                ),
                _ => (Width, Height, baseIter)
            };
        }

        #endregion Public Methods

        #region Private Methods

        private void ResetAll(int Width = 800, int Height = 600)
        {
            _width = Width;
            _height = Height;
            _centerX = -0.75f;
            _centerY = 0.0f;
            _scale = 3.5f;
            _autoIterations = true;
            _iterations = 250;
            _maxIterations = 500;
            _minIterations = 50;
            _realMax = 1.0f;
            _realMin = -2.5f;
            _imagMax = 1.5f;
            _imagMin = -1.5f;
            _useDoublePrecision = false;
            UpdViewBounds();
        }

        private void UpdViewBounds()
        {
            float aspectRatio = (float)_height / _width;
            float halfWidth = _scale * 0.5f;
            float halfHeight = halfWidth * aspectRatio;
            _realMin = _centerX - halfWidth;
            _realMax = _centerX + halfWidth;
            _imagMin = _centerY - halfHeight;
            _imagMax = _centerY + halfHeight;

        }

        private void UpdIterations(QualityLevel quality)
        {
            if (!_autoIterations) return;

            float zoomFactor = 3.5f / _scale;

            // relative to initial view
            float raw = 50f * MathF.Log10(zoomFactor + 1f);

            float qualityMultiplier = quality switch
            {
                QualityLevel.Fast => 0.5f,
                QualityLevel.Normal => 1.0f,
                QualityLevel.High => 1.5f,
                QualityLevel.Ultra => 2.0f,
                _ => 1.0f,
            };

            _iterations = (int)System.Math.Clamp(raw * qualityMultiplier, MinIterations, MaxIterations);
        }

        #endregion Private Methods
    }
}
