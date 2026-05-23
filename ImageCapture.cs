using FracturingFog.Interefaces;
using FracturingFog.Models;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
//using System.Windows.Forms;

namespace FracturingFog
{
    public sealed partial class MainForm
    {
        #region Screen Capture

        private void OnScreenshotClick(object? sender, EventArgs e)
        {
            if (_calculator == null)
            {
                System.Windows.Forms.MessageBox.Show(
                    "No fractal data to save yet.", "Screenshot",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            string colorName = _calculator.ColorMap?.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Theme";
            string regionName = "";
            if (!string.IsNullOrEmpty(CurrentRegionName()))
                regionName = CurrentRegionName()?.Replace(" ", "") + "_" ?? "";

            IFractalCalculator? altForName = SelectAltCalculator(_currentFractalType);
            double fnCx = altForName?.CenterX ?? _calculator.CenterX;
            double fnCy = altForName?.CenterY ?? _calculator.CenterY;
            double fnZoom = altForName?.Zoom ?? _calculator.Zoom;
            int fnIter = altForName?.MaxIterations ?? _calculator.MaxIterations;
            int fnW = altForName?.Width ?? _calculator.Width;
            int fnH = altForName?.Height ?? _calculator.Height;

            Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            string sizeTag = _spanning
                ? $"{vs.Width}x{vs.Height}_wallpaper"
                : $"{fnW}x{fnH}";

            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = _spanning ? "Save Wallpaper Screenshot" : "Save Fractal Screenshot",
                Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|BMP Image (*.bmp)|*.bmp",
                FilterIndex = 1,
                DefaultExt = "png",
                FileName = $"{_programName.Replace(" ", "")}_{CurrentFractalTypeName()}_{colorName.Replace(" ", "")}_{regionName.Replace(" ", "")}" +
                             $"x{fnCx.ToString().Replace(".", "")}_" +
                             $"y{fnCy.ToString().Replace(".", "")}_" +
                             $"z{fnZoom.ToString().Replace(".", "")}_" +
                             $"i{fnIter.ToString().Replace(".", "")}_" +
                             sizeTag
            };
            if (dlg.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return;

            string path = dlg.FileName;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var format = ext switch { ".bmp" => ImageFormat.Bmp, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };
            string wm = $"{(!string.IsNullOrEmpty(CurrentRegionName()) ? CurrentRegionName() : "Fracturing Fog")}" +
                          $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";
            string subText = $"{_programName} v{_programVersion} {DateTime.Now.Year}";

            if (_spanning) TakeWallpaperScreenshot(path, format, wm, subText);
            else TakeNormalScreenshot(path, format, wm, subText);
        }

        private void TakeNormalScreenshot(string path, ImageFormat format, string waterMark, string subText)
        {
            int w, h;
            uint[] pixels;
            IFractalCalculator? alt = SelectAltCalculator(_currentFractalType);
            if (alt != null)
            {
                w = alt.Width;
                h = alt.Height;
                pixels = BuildProcessedBuffer(alt.ColorBuffer, w, h);
            }
            else
            {
                w = _calculator!.Width;
                h = _calculator!.Height;
                pixels = BuildProcessedBuffer(_calculator);
            }
            try
            {
                // Pixel-sampled contrast colour for the watermark.
                var fontColor = ComputeContrastColor(GetSwatchColor(),
                    watermark: true, pixels: pixels, imgW: w, imgH: h);
                SavePixelsToFile(pixels, w, h, path, format, waterMark, fontColor, subText);
                SetStatus($"Saved  {Path.GetFileName(path)}  ({w}×{h},  {new FileInfo(path).Length / 1024:N0} KB)");
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Save failed:\n{ex.Message}", "Screenshot Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Returns a BGRA buffer with brightness/contrast applied (and grid overlay
        /// if visible).  Returns the original ColorBuffer reference if no adjustments
        /// are active (avoids unnecessary allocation).
        /// </summary>
        private uint[] BuildProcessedBuffer(MandelbrotCalculator calc)
            => BuildProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height);

        private uint[] BuildProcessedBuffer(uint[] src, int width, int height)
        {
            bool needsProcess = _brightness != 0 || _contrast != 0 || _gridVisible;
            if (!needsProcess) return src;

            int n = width * height;
            var dst = new uint[n];
            float cf = 1.0f + _contrast / 100.0f;
            float bo = _brightness / 100.0f;

            if (_brightness != 0 || _contrast != 0)
            {
                for (int i = 0; i < n; i++)
                {
                    uint p = src[i];
                    float r = ((p >> 16) & 0xFF) / 255f;
                    float g = ((p >> 8) & 0xFF) / 255f;
                    float b = (p & 0xFF) / 255f;
                    r = (r - 0.5f) * cf + 0.5f + bo;
                    g = (g - 0.5f) * cf + 0.5f + bo;
                    b = (b - 0.5f) * cf + 0.5f + bo;
                    byte R = (byte)(System.Math.Clamp(r, 0f, 1f) * 255f);
                    byte G = (byte)(System.Math.Clamp(g, 0f, 1f) * 255f);
                    byte B = (byte)(System.Math.Clamp(b, 0f, 1f) * 255f);
                    dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                }
            }
            else
            {
                Array.Copy(src, dst, n);
            }
            if (_gridVisible) BlendGridOverlay(dst, width, height);
            return dst;
        }

        /// <summary>
        /// Builds a fresh calculator at the requested size matching the current
        /// fractal type and parameters. Returns null for Mandelbrot (caller
        /// must use the MandelbrotCalculator branch to preserve QD-limb state).
        /// </summary>
        private IFractalCalculator? BuildAltCalculatorForCapture(FractalType type, int w, int h)
        {
            IFractalCalculator? c = type switch
            {
                FractalType.Mandelbrot       => null,
                FractalType.Julia            => new EscapeTimeCalculator(w, h),
                FractalType.BurningShip      => new EscapeTimeCalculator(w, h),
                FractalType.Tricorn          => new EscapeTimeCalculator(w, h),
                FractalType.Multibrot        => new EscapeTimeCalculator(w, h),
                FractalType.Phoenix          => new EscapeTimeCalculator(w, h),
                FractalType.IFS              => new IFSCalculator(w, h),
                FractalType.LSystem          => new LSystemCalculator(w, h),
                FractalType.StrangeAttractor => new AttractorCalculator(w, h),
                FractalType.BuddhaBrot       => new BuddhabrotCalculator(w, h),
                FractalType.Newton           => new NewtonCalculator(w, h),
                FractalType.Nova             => new NewtonCalculator(w, h),
                FractalType.UserEquation     => new UserEquationCalculator(w, h),
                FractalType.Mandelbulb       => new MandelbulbCalculator(w, h),
                FractalType.Sandbox          => new SandboxCalculator(w, h),
                FractalType.UserBulb         => new UserBulbCalculator(w, h),
                _                            => null
            };
            if (c == null) return null;
            c.CenterX = _calculator!.CenterX;
            c.CenterY = _calculator!.CenterY;
            c.Zoom = _calculator!.Zoom;
            c.MaxIterations = _calculator!.MaxIterations;
            c.Quality = _quality;
            c.ColorMap = _calculator!.ColorMap;
            switch (c)
            {
                case EscapeTimeCalculator e:
                    e.FractalType = type;
                    e.FractalParameters = _fractalParams;
                    break;
                case IFSCalculator ifs:        ifs.FractalParameters = _fractalParams; break;
                case LSystemCalculator ls:     ls.FractalParameters = _fractalParams; break;
                case AttractorCalculator a:    a.FractalParameters = _fractalParams; break;
                case BuddhabrotCalculator b:   b.FractalParameters = _fractalParams; break;
                case NewtonCalculator n:       n.FractalParameters = _fractalParams; break;
                case UserEquationCalculator u: u.FractalParameters = _fractalParams; break;
                case MandelbulbCalculator m:   m.FractalParameters = _fractalParams; break;
                case SandboxCalculator sb:     sb.FractalParameters = _fractalParams; break;
                case UserBulbCalculator ub:    ub.FractalParameters = _fractalParams; break;
            }
            return c;
        }

        private void TakeWallpaperScreenshot(string path, ImageFormat format, string waterMark, string subText)
        {
            Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            int fullW = vs.Width;
            int fullH = vs.Height;

            int toolbarH = 0;
            foreach (System.Windows.Forms.Control c in Controls)
                if (c.Dock == System.Windows.Forms.DockStyle.Top) toolbarH += c.Height;

            FractalType type = _currentFractalType;
            IFractalCalculator? altCalc = BuildAltCalculatorForCapture(type, fullW, fullH);

            double cx = _calculator!.CenterX, cxLo = _calculator!.CenterXLo;
            double cx2 = _calculator!.CenterX2, cx3 = _calculator!.CenterX3;
            double cy = _calculator!.CenterY, cyLo = _calculator!.CenterYLo;
            double cy2 = _calculator!.CenterY2, cy3 = _calculator!.CenterY3;
            double zoom = _calculator!.Zoom;
            int maxIter = _calculator!.MaxIterations;
            IColorMap map = _calculator!.ColorMap;
            QualityPreset q = _quality;

            long mpix = (long)fullW * fullH / 1_000_000;
            _screenshotButton.Enabled = false;
            _screenshotButton.Text = "Rendering…";
            SetStatus($"Rendering wallpaper  {fullW}×{fullH}  ({mpix} MP, +{toolbarH} px over render panel)  …");

            CancellationToken token;
            lock (_wallpaperLock)
            {
                _wallpaperCts?.Cancel();
                _wallpaperCts = new CancellationTokenSource();
                token = _wallpaperCts.Token;
            }

            var sw = Stopwatch.StartNew();

            Task.Run<(uint[] Buffer, int Width, int Height)>(() =>
            {
                if (altCalc != null)
                {
                    altCalc.Calculate(token);
                    token.ThrowIfCancellationRequested();
                    return (altCalc.ColorBuffer, altCalc.Width, altCalc.Height);
                }
                var tempCalc = new MandelbrotCalculator(fullW, fullH)
                {
                    CenterX = cx,
                    CenterXLo = cxLo,
                    CenterX2 = cx2,
                    CenterX3 = cx3,
                    CenterY = cy,
                    CenterYLo = cyLo,
                    CenterY2 = cy2,
                    CenterY3 = cy3,
                    Zoom = zoom,
                    MaxIterations = maxIter,
                    ColorMap = map,
                    Quality = q
                };
                tempCalc.Calculate(token);
                token.ThrowIfCancellationRequested();
                return (tempCalc.ColorBuffer, tempCalc.Width, tempCalc.Height);
            }, token)
            .ContinueWith(t =>
            {
                if (!IsHandleCreated || _disposed) return;
                Invoke(() =>
                {
                    _screenshotButton.Enabled = true;
                    _screenshotButton.Text = "Image";

                    if (t.IsCanceled) { SetStatus("Wallpaper render cancelled."); return; }
                    if (t.IsFaulted)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"Wallpaper render failed:\n\n{t.Exception?.InnerException?.Message}",
                            "Screenshot Error",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error);
                        return;
                    }

                    sw.Stop();
                    var result = t.Result;
                    try
                    {
                        var fontColor = ComputeContrastColor(GetSwatchColor(),
                            watermark: true, pixels: result.Buffer,
                            imgW: result.Width, imgH: result.Height);
                        SavePixelsToFile(result.Buffer, result.Width, result.Height, path, format, waterMark, fontColor, subText);
                        SetStatus($"Wallpaper saved  →  {Path.GetFileName(path)}  ({result.Width}×{result.Height} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"Failed to save wallpaper:\n\n{ex.Message}",
                            "Screenshot Error",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error); }
                });
            }, TaskScheduler.Default);
        }

        private void TakePosterScreenshot(int width, int height, bool isPortrait, bool rotateImage, string path, ImageFormat format, string waterMark, string subText)
        {
            int fullW = width;
            int fullH = height;

            int toolbarH = 0;
            foreach (System.Windows.Forms.Control c in Controls)
                if (c.Dock == System.Windows.Forms.DockStyle.Top) toolbarH += c.Height;

            FractalType type = _currentFractalType;
            IFractalCalculator? altCalc = BuildAltCalculatorForCapture(type, fullW, fullH);

            double cx = _calculator!.CenterX, cxLo = _calculator!.CenterXLo;
            double cx2 = _calculator!.CenterX2, cx3 = _calculator!.CenterX3;
            double cy = _calculator!.CenterY, cyLo = _calculator!.CenterYLo;
            double cy2 = _calculator!.CenterY2, cy3 = _calculator!.CenterY3;
            double zoom = _calculator!.Zoom;
            int maxIter = _calculator!.MaxIterations;
            IColorMap map = _calculator!.ColorMap;
            QualityPreset q = _quality;

            long mpix = (long)fullW * fullH / 1_000_000;
            _screenshotButton.Enabled = false;
            _posterButton.Enabled = false;
            _posterButton.Text = "Rendering…";
            SetStatus($"Rendering poster  {fullW}×{fullH}  ({mpix} MP, +{toolbarH} px over render panel)  …");

            CancellationToken token;
            lock (_wallpaperLock)
            {
                _wallpaperCts?.Cancel();
                _wallpaperCts = new CancellationTokenSource();
                token = _wallpaperCts.Token;
            }

            var sw = Stopwatch.StartNew();

            Task.Run<(uint[] Buffer, int Width, int Height)>(() =>
            {
                if (altCalc != null)
                {
                    altCalc.Calculate(token);
                    token.ThrowIfCancellationRequested();
                    return (altCalc.ColorBuffer, altCalc.Width, altCalc.Height);
                }
                var tempCalc = new MandelbrotCalculator(fullW, fullH)
                {
                    CenterX = cx,
                    CenterXLo = cxLo,
                    CenterX2 = cx2,
                    CenterX3 = cx3,
                    CenterY = cy,
                    CenterYLo = cyLo,
                    CenterY2 = cy2,
                    CenterY3 = cy3,
                    Zoom = zoom,
                    MaxIterations = maxIter,
                    ColorMap = map,
                    Quality = q
                };
                tempCalc.Calculate(token);
                token.ThrowIfCancellationRequested();
                return (tempCalc.ColorBuffer, tempCalc.Width, tempCalc.Height);
            }, token)
            .ContinueWith(t =>
            {
                if (!IsHandleCreated || _disposed) return;
                Invoke(() =>
                {
                    _screenshotButton.Enabled = true;
                    _screenshotButton.Text = "Image";
                    _posterButton.Enabled = true;
                    _posterButton.Text = "Poster";

                    if (t.IsCanceled) { SetStatus("Poster render cancelled."); return; }
                    if (t.IsFaulted)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"Poster render failed:\n\n{t.Exception?.InnerException?.Message}",
                            "Screenshot Error",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error);
                        return;
                    }

                    sw.Stop();
                    var result = t.Result;
                    try
                    {
                        // Rotate 90° clockwise when portrait or rotateImage is requested.
                        // The landscape render (width × height) becomes portrait (height × width).
                        if (isPortrait || rotateImage)
                        {
                            var rotated = new uint[result.Buffer.Length];
                            for (int y = 0; y < result.Height; y++)
                                for (int x = 0; x < result.Width; x++)
                                    rotated[x * result.Height + (result.Height - 1 - y)] = result.Buffer[y * result.Width + x];
                            // After 90° CW rotation the saved dimensions are result.Height × result.Width.
                            var fontColor = ComputeContrastColor(GetSwatchColor(),
                                watermark: true, pixels: rotated, imgW: result.Height, imgH: result.Width);
                            SavePixelsToFile(
                                rotated,
                                result.Height,
                                result.Width,
                                path,
                                format,
                                waterMark,
                                fontColor,
                                subText,
                                true);
                            SetStatus($"Poster saved  →  {Path.GetFileName(path)}  ({result.Height}×{result.Width} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                        }
                        else
                        {
                            var fontColor = ComputeContrastColor(GetSwatchColor(),
                                watermark: true, pixels: result.Buffer,
                                imgW: result.Width, imgH: result.Height);
                            SavePixelsToFile(
                                result.Buffer,
                                result.Width,
                                result.Height,
                                path,
                                format,
                                waterMark,
                                fontColor,
                                subText,
                                true);
                            SetStatus($"Poster saved  →  {Path.GetFileName(path)}  ({result.Width}×{result.Height} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"Failed to save poster:\n\n{ex.Message}",
                            "Screenshot Error",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error); }
                });
            }, TaskScheduler.Default);
        }

        private static unsafe void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* src = pixels)
                {
                    if (bmpData.Stride == w * 4)
                        Buffer.MemoryCopy(src, (void*)bmpData.Scan0, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* dst = (byte*)bmpData.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                              dst + (long)row * bmpData.Stride,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(bmpData); }

            if (format == ImageFormat.Tiff)
            {
                ImageCodecInfo? codec = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.MimeType == "image/tiff") { codec = c; break; }
                if (codec != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)EncoderValue.CompressionLZW);
                    bmp.Save(path, codec, ep);
                }
                else bmp.Save(path, format);
            }
            else bmp.Save(path, format);

            Debug.WriteLine($"Watermark text: '{watermarkText}'");
            if (!string.IsNullOrEmpty(watermarkText))
            {
                using var g = Graphics.FromImage(bmp);
                AddWaterMark(g, watermarkText, w, h, fontColor, subText, poster);
                bmp.Save(path, format);
            }
        }

        private static void AddWaterMark(
            Graphics g,
            string text,
            int width,
            int height,
            Color fontColor,
            string subText = "",
            bool poster = false)
        {
            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            int yOffset = poster ? System.Math.Min(width, height) / 150 : 12;
            var pos = new PointF(width - sz.Width - 20, height - sz.Height - yOffset);

            // Outline colour: opposite luminance of fill, ~75% opacity.
            float lum = (fontColor.R * 0.299f + fontColor.G * 0.587f + fontColor.B * 0.114f) / 255f;
            Color outlineColor = lum < 0.5f
                ? Color.FromArgb(190, 255, 255, 255)
                : Color.FromArgb(190, 0, 0, 0);

            float mainStroke = poster ? System.Math.Max(2f, fontSize / 10f) : 2f;
            float subStroke = poster ? System.Math.Max(1.5f, fontSize / 16f) : 1.5f;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            DrawOutlinedString(g, text, font, pos, fontColor, outlineColor, mainStroke);

            if (!string.IsNullOrEmpty(subText))
            {
                using var fontSmall = new Font("Segoe UI", fontSize / 2, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz2 = g.MeasureString(subText, fontSmall);
                int subTextOffset = poster ? 0 : 2;
                var subPos = new PointF(width - sz2.Width - 55, height - sz2.Height - subTextOffset);
                DrawOutlinedString(g, subText, fontSmall, subPos, fontColor, outlineColor, subStroke);
            }
        }

        private static void DrawOutlinedString(
            Graphics g, string text, Font font, PointF pos,
            Color fill, Color outline, float strokeWidth)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, pos,
                System.Drawing.StringFormat.GenericDefault);
            using var pen = new Pen(outline, strokeWidth)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                MiterLimit = 2f
            };
            g.DrawPath(pen, path);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        /// <summary>
        /// Computes the on-image bounding box the watermark will occupy.
        /// Used by the slideshow overlay to allocate only a small bitmap
        /// instead of a full-frame one.
        /// </summary>
        private static Rectangle MeasureWatermarkBBox(
            string text, string subText, int width, int height, bool poster = false)
        {
            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var dummy = new Bitmap(1, 1);
            using var g = Graphics.FromImage(dummy);

            var sz = g.MeasureString(text, font);
            int yOffset = poster ? System.Math.Min(width, height) / 150 : 12;
            float left = width - sz.Width - 20;
            float top = height - sz.Height - yOffset;
            float right = left + sz.Width;
            float bottom = top + sz.Height;

            if (!string.IsNullOrEmpty(subText))
            {
                using var fontSmall = new Font("Segoe UI", fontSize / 2, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz2 = g.MeasureString(subText, fontSmall);
                int subTextOffset = poster ? 0 : 2;
                float sLeft = width - sz2.Width - 55;
                float sTop = height - sz2.Height - subTextOffset;
                left = System.Math.Min(left, sLeft);
                top = System.Math.Min(top, sTop);
                right = System.Math.Max(right, sLeft + sz2.Width);
                bottom = System.Math.Max(bottom, sTop + sz2.Height);
            }

            // Pad for outline stroke + AA fringe.
            const int pad = 6;
            int x0 = System.Math.Max(0, (int)System.Math.Floor(left) - pad);
            int y0 = System.Math.Max(0, (int)System.Math.Floor(top) - pad);
            int x1 = System.Math.Min(width, (int)System.Math.Ceiling(right) + pad);
            int y1 = System.Math.Min(height, (int)System.Math.Ceiling(bottom) + pad);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        #endregion Screen Capture
    }
}
