using FracturingFog.Imaging;
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
            => PosterRenderer.BuildCaptureCalculator(new PosterRequest
            {
                FractalType = type,
                Width = w,
                Height = h,
                CenterX = _calculator!.CenterX,
                CenterY = _calculator!.CenterY,
                Zoom = _calculator!.Zoom,
                MaxIterations = _calculator!.MaxIterations,
                Quality = _quality,
                ColorMap = _calculator!.ColorMap,
                FractalParameters = _fractalParams,
            });

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

            var req = new PosterRequest
            {
                FractalType = _currentFractalType,
                Width = fullW,
                Height = fullH,
                CenterX = _calculator!.CenterX, CenterXLo = _calculator!.CenterXLo,
                CenterX2 = _calculator!.CenterX2, CenterX3 = _calculator!.CenterX3,
                CenterY = _calculator!.CenterY, CenterYLo = _calculator!.CenterYLo,
                CenterY2 = _calculator!.CenterY2, CenterY3 = _calculator!.CenterY3,
                Zoom = _calculator!.Zoom,
                MaxIterations = _calculator!.MaxIterations,
                ColorMap = _calculator!.ColorMap,
                Quality = _quality,
                FractalParameters = _fractalParams,
                Rotate = isPortrait || rotateImage,
                Path = path,
                Format = format,
                Watermark = waterMark,
                SubText = subText,
            };

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

            Task.Run(() => PosterRenderer.RenderToFile(req, token), token)
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

                    var r = t.Result;
                    SetStatus($"Poster saved  →  {Path.GetFileName(path)}  ({r.SavedWidth}×{r.SavedHeight} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{r.ElapsedMs} ms]");
                });
            }, TaskScheduler.Default);
        }

        // ── Image-IO helpers now live in FracturingFog.Imaging.ImageExport ──
        // (shared with the Avalonia shell). These thin private forwarders keep
        // the existing MainForm/Slideshow call sites compiling unchanged.

        private static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false)
            => ImageExport.SavePixelsToFile(pixels, w, h, path, format, watermarkText, fontColor, subText, poster);

        private static void AddWaterMark(
            Graphics g, string text, int width, int height,
            Color fontColor, string subText = "", bool poster = false)
            => ImageExport.AddWaterMark(g, text, width, height, fontColor, subText, poster);

        private static Rectangle MeasureWatermarkBBox(
            string text, string subText, int width, int height, bool poster = false)
            => ImageExport.MeasureWatermarkBBox(text, subText, width, height, poster);

        #endregion Screen Capture
    }
}
