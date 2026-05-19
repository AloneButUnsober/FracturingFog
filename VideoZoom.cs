using FracturingFog.Models;
using FracturingFog.Views;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FracturingFog
{
    // Video Zoom: smoothly animates the view from the current center/zoom to a
    // user-supplied target (region pick or manual coords) over a chosen duration.
    //
    //   • Two-phase motion: first pan to the target CX/CY at the *current* zoom,
    //     then zoom in to the target depth with the center fixed. This avoids
    //     the "zoom-and-drift" feel where the target slides off-screen as the
    //     view tightens.  Split is PanFraction / (1 - PanFraction) of the total
    //     duration.
    //   • Smoothstep easing inside each phase for soft start/stop.
    //   • log-Zoom interpolation in the zoom phase — perceptually even,
    //     exponential approach.
    //   • Each animation frame triggers a full Calculate() on the background
    //     thread. Frame rate is therefore calculation-bound, not wall-clock-bound;
    //     the loop advances by elapsed wall-clock time so the total duration is
    //     honoured even when individual frames are slow.
    //   • Target zoom is capped at Ultra (5e27) to avoid the extreme-zoom
    //     pixelation regime where Bla/QD math becomes unreliable.

    public sealed partial class MainForm
    {
        private bool _videoRunning;
        private CancellationTokenSource? _videoCts;
        private readonly object _videoLock = new();

        // Video slideshow state — independent from single-shot Video to keep
        // each feature stoppable on its own.
        private bool _videoSlideshowRunning;
        private CancellationTokenSource? _videoSlideshowCts;
        private readonly object _videoSlideshowLock = new();

        // Per-leg duration of the video slideshow zoom (seconds).
        private const double VideoSlideshowSeconds = 30.0;
        // Pause between successive videos in the slideshow (milliseconds).
        private const int VideoSlideshowPauseMs = 7_000;

        // Fraction of total video duration spent on pan phase. Remaining
        // duration is the zoom-in phase. Skipped entirely if start == target
        // CX/CY (no pan needed).
        private const double VideoPanFraction = 0.05;

        // ── Save-Video state (single-shot Video only; slideshow ignores) ──
        // _videoMp4Writer is non-null between StartVideo and the post-zoom
        // SaveFileDialog prompt; each rendered frame is fed to it on the UI
        // thread immediately after UploadProcessedBuffer.
        private Mp4Writer? _videoMp4Writer;
        private string? _videoMp4TempPath;
        private Stopwatch? _videoMp4Sw;

        // Lossless PNG-sequence recorder state. Parallel to the MP4 writer —
        // both can be active simultaneously. _videoPngFolder is the temp
        // folder the writer is dumping frames into.
        private PngSequenceWriter? _videoPngWriter;
        private string? _videoPngFolder;
        private Views.VideoDialog.LosslessEncodeChoice _videoLosslessEncode =
            Views.VideoDialog.LosslessEncodeChoice.None;

        // Wait time (ms) between zoom completion and the Save File prompt.
        private const int VideoSavePromptDelayMs = 2_000;

        // Per-region iteration override carried across the current zoom (or
        // current slideshow leg). Zero means "use the quality preset's
        // computed iteration count only". When non-zero, ApplyVideoFrameState
        // raises MaxIterations to at least this value so deep regions don't
        // render as in-set black just because the preset's iter formula
        // produces fewer iterations than the region was authored for.
        private int _videoTargetIterations;

        private readonly record struct QDCoord(double Hi, double Lo, double X2, double X3);

        private void OnVideoClick(object? sender, EventArgs e)
        {
            if (_videoRunning)
            {
                StopVideo();
                return;
            }
            if (_videoSlideshowRunning)
            {
                StopVideoSlideshow();
                return;
            }

            if (_calculator == null || _renderer == null) return;

            using var dlg = new VideoDialog(_centerX, _centerY, _zoom);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (dlg.IsSlideshow)
            {
                StartVideoSlideshow(dlg.SlideshowSecondsOverride, dlg.IsConstantRate, dlg.IsReverse);
                return;
            }

            if (!dlg.TryGetTargetQD(
                    out double txHi, out double txLo, out double txX2, out double txX3,
                    out double tyHi, out double tyLo, out double tyX2, out double tyX3,
                    out double tz, out double seconds))
            {
                MessageBox.Show(
                    "Invalid target values.\n\n" +
                    "Target CX / CY must be valid decimals.\n" +
                    "Target Zoom must be a positive number.\n" +
                    "Custom duration must be between 0.5 and 300 seconds.",
                    "Video Zoom",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            double ultraMax = QualityPreset.Ultra.ZoomMax;
            if (tz > ultraMax)
            {
                MessageBox.Show(
                    $"Target zoom exceeds Ultra ({ultraMax:G3}).\n" +
                    "Video zoom is capped at Ultra precision due to pixelation\n" +
                    "in the Extreme regime. Clamping target to Ultra max.",
                    "Video Zoom",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                tz = ultraMax;
            }
            if (tz < QualityPreset.Draft.ZoomMin) tz = QualityPreset.Draft.ZoomMin;

            QDCoord startCX, startCY, targetCX, targetCY;
            double startZoom, targetZoom;
            QualityPreset startQuality;

            if (dlg.IsReverse)
            {
                // Reverse: begin AT the user-supplied target/zoom, animate back
                // to the classic full-set view. Quality is set to match the
                // deep starting zoom (ApplyVideoFrameState only auto-promotes
                // upward, never downward; staying on a high tier as we zoom
                // out is safe — just over-iterates the shallow frames).
                startCX = new QDCoord(txHi, txLo, txX2, txX3);
                startCY = new QDCoord(tyHi, tyLo, tyX2, tyX3);
                startZoom = tz;
                targetCX = new QDCoord(DefaultCenterX, 0.0, 0.0, 0.0);
                targetCY = new QDCoord(DefaultCenterY, 0.0, 0.0, 0.0);
                targetZoom = DefaultZoom;

                startQuality = QualityPreset.Standard;
                foreach (var p in QualityPreset.All)
                {
                    if (p.Tier == QualityTier.Extreme) continue;
                    if (p.ZoomMax >= startZoom) { startQuality = p; break; }
                }

                _centerX = startCX.Hi; _centerXLo = startCX.Lo; _centerX2 = startCX.X2; _centerX3 = startCX.X3;
                _centerY = startCY.Hi; _centerYLo = startCY.Lo; _centerY2 = startCY.X2; _centerY3 = startCY.X3;
                _zoom = startZoom;
            }
            else
            {
                // Forward: reset view to classic and force Standard quality.
                // ApplyVideoFrameState auto-promotes through tiers as the zoom
                // phase crosses each ZoomMax boundary.
                startCX = new QDCoord(DefaultCenterX, 0.0, 0.0, 0.0);
                startCY = new QDCoord(DefaultCenterY, 0.0, 0.0, 0.0);
                startZoom = DefaultZoom;
                targetCX = new QDCoord(txHi, txLo, txX2, txX3);
                targetCY = new QDCoord(tyHi, tyLo, tyX2, tyX3);
                targetZoom = tz;

                startQuality = QualityPreset.Standard;

                _centerX = DefaultCenterX; _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
                _centerY = DefaultCenterY; _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
                _zoom = DefaultZoom;
            }

            _quality = startQuality;
            _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
            _qualityCombo.Text = _quality.Name;
            _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
            //if (_qualityCombo2 != null) _qualityCombo2.Text = _quality.Name;

            // Region's authored iter count when a region was picked; zero for
            // manual entry. ApplyVideoFrameState uses it as a floor on the
            // computed MaxIterations so deep targets render with the iter
            // budget they were authored for.
            _videoTargetIterations = dlg.TargetIterations;

            // Initialise MP4 / lossless recorders before the zoom starts so
            // the first frame is captured. Failure here disables that recorder
            // but lets the zoom proceed.
            if (dlg.IsSaveVideo)
                TryStartVideoRecording();
            if (dlg.IsSaveLossless)
            {
                _videoLosslessEncode = dlg.LosslessEncode;
                TryStartLosslessRecording();
            }

            StartVideo(startCX, startCY, startZoom, targetCX, targetCY, targetZoom, seconds, dlg.IsReverse);
        }

        private void TryStartVideoRecording()
        {
            if (_calculator == null) return;
            int w = _calculator.Width;
            int h = _calculator.Height;
            if (w < 16 || h < 16) return;
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(),
                    $"fracturingfog_{Guid.NewGuid():N}.mp4");
                _videoMp4Writer = new Mp4Writer(tempPath, w, h);
                _videoMp4TempPath = tempPath;
                _videoMp4Sw = Stopwatch.StartNew();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Video recording disabled — encoder init failed:\n{ex.Message}",
                    "Save Video", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClearVideoRecordingState(deleteTempFile: true);
            }
        }

        private void TryStartLosslessRecording()
        {
            if (_calculator == null) return;
            int w = _calculator.Width;
            int h = _calculator.Height;
            if (w < 16 || h < 16) return;
            try
            {
                string folder = Path.Combine(Path.GetTempPath(),
                    $"fracturingfog_pngseq_{Guid.NewGuid():N}");
                _videoPngWriter = new PngSequenceWriter(folder, w, h);
                _videoPngFolder = folder;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Lossless recording disabled — init failed:\n{ex.Message}",
                    "Save Video", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClearLosslessRecordingState(deleteFolder: true);
            }
        }

        private (PngSequenceWriter? Writer, string? Folder) TakeLosslessRecordingState()
        {
            var w = _videoPngWriter;
            var f = _videoPngFolder;
            _videoPngWriter = null;
            _videoPngFolder = null;
            return (w, f);
        }

        private void ClearLosslessRecordingState(bool deleteFolder)
        {
            var (w, f) = TakeLosslessRecordingState();
            try { w?.Dispose(); } catch { }
            if (deleteFolder && f != null && Directory.Exists(f))
                try { Directory.Delete(f, recursive: true); } catch { }
        }

        // Reads + clears recorder state in one shot so the caller can finalize
        // and decide what to do with the temp file.
        private (Mp4Writer? Writer, string? TempPath) TakeVideoRecordingState()
        {
            var w = _videoMp4Writer;
            var p = _videoMp4TempPath;
            _videoMp4Writer = null;
            _videoMp4TempPath = null;
            _videoMp4Sw = null;
            return (w, p);
        }

        private void ClearVideoRecordingState(bool deleteTempFile)
        {
            var (w, p) = TakeVideoRecordingState();
            try { w?.Dispose(); } catch { }
            if (deleteTempFile && p != null && File.Exists(p))
                try { File.Delete(p); } catch { }
        }

        private void StartVideo(
            QDCoord cx0, QDCoord cy0, double z0,
            QDCoord cx1, QDCoord cy1, double z1,
            double seconds,
            bool reverse = false)
        {
            _videoRunning = true;
            _videoButton.Text = "■ Stop";
            _videoButton.BackColor = Color.FromArgb(70, 30, 30);
            _videoButton.FlatAppearance.BorderColor = Color.FromArgb(120, 50, 50);
            SetStatus(reverse
                ? $"Video reverse zoom → classic from cx={cx0.Hi:G6} cy={cy0.Hi:G6} zoom={z0:G4} over {seconds:F1}s"
                : $"Video zoom → cx={cx1.Hi:G6} cy={cy1.Hi:G6} zoom={z1:G4} over {seconds:F1}s");

            CancellationTokenSource cts;
            lock (_videoLock)
            {
                _videoCts?.Cancel();
                _videoCts = new CancellationTokenSource();
                cts = _videoCts;
            }

            Task.Run(() => VideoLoop(cx0, cy0, z0, cx1, cy1, z1, seconds, ct: cts.Token, reverse: reverse), cts.Token)
                .ContinueWith(t =>
                {
                    if (!IsHandleCreated || _disposed) return;
                    Invoke(() =>
                    {
                        _videoRunning = false;
                        _videoButton.Text = "Video";
                        _videoButton.BackColor = Color.FromArgb(55, 40, 70);
                        _videoButton.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 130);

                        // Clear the per-region iter override so any subsequent
                        // interactive calculation falls back to the preset.
                        _videoTargetIterations = 0;

                        // Finalize both encoders before doing anything else so
                        // the temp artefacts are fully written by the time we
                        // decide whether to keep or delete them.
                        var (writer, tempPath) = TakeVideoRecordingState();
                        try { writer?.Dispose(); } catch { }
                        var (pngWriter, pngFolder) = TakeLosslessRecordingState();
                        try { pngWriter?.Dispose(); } catch { }
                        var encodeChoice = _videoLosslessEncode;
                        _videoLosslessEncode = Views.VideoDialog.LosslessEncodeChoice.None;

                        if (t.IsCanceled)
                        {
                            if (tempPath != null && File.Exists(tempPath))
                                try { File.Delete(tempPath); } catch { }
                            if (pngFolder != null && Directory.Exists(pngFolder))
                                try { Directory.Delete(pngFolder, recursive: true); } catch { }
                            SetStatus("Video zoom cancelled.");
                        }
                        else if (t.IsFaulted)
                        {
                            if (tempPath != null && File.Exists(tempPath))
                                try { File.Delete(tempPath); } catch { }
                            if (pngFolder != null && Directory.Exists(pngFolder))
                                try { Directory.Delete(pngFolder, recursive: true); } catch { }
                            SetStatus($"Video zoom error: {t.Exception?.InnerException?.Message}");
                        }
                        else
                        {
                            SetStatus($"Video zoom complete. cx={_centerX:G12} cy={_centerY:G12} zoom={_zoom:G6}");
                            if (tempPath != null && File.Exists(tempPath))
                                ScheduleSaveVideoPrompt(tempPath, VideoSavePromptDelayMs);
                            if (pngFolder != null && Directory.Exists(pngFolder))
                                ScheduleSaveLosslessPrompt(pngFolder, encodeChoice, VideoSavePromptDelayMs);
                        }
                    });
                }, TaskScheduler.Default);
        }

        // Wait a moment after the zoom ends, then prompt for the destination.
        private void ScheduleSaveVideoPrompt(string tempPath, int delayMs)
        {
            var timer = new System.Windows.Forms.Timer { Interval = delayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_disposed)
                {
                    try { File.Delete(tempPath); } catch { }
                    return;
                }
                PromptSaveVideoFile(tempPath);
            };
            timer.Start();
        }

        // ── Lossless (PNG sequence) post-zoom flow ─────────────────────────
        private void ScheduleSaveLosslessPrompt(
            string pngFolder,
            Views.VideoDialog.LosslessEncodeChoice encode,
            int delayMs)
        {
            var timer = new System.Windows.Forms.Timer { Interval = delayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (_disposed)
                {
                    try { Directory.Delete(pngFolder, recursive: true); } catch { }
                    return;
                }
                PromptSaveLossless(pngFolder, encode);
            };
            timer.Start();
        }

        private async void PromptSaveLossless(
            string pngFolder,
            Views.VideoDialog.LosslessEncodeChoice encode)
        {
            // 1. Pick destination folder for the PNG sequence.
            string? destFolder = null;
            using (var fbd = new FolderBrowserDialog
            {
                Description =
                    "Choose a folder to keep the lossless PNG sequence" +
                    (encode != Views.VideoDialog.LosslessEncodeChoice.None
                        ? " (an encoded video will also be written next to it)" : ""),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            })
            {
                if (fbd.ShowDialog(this) == DialogResult.OK)
                    destFolder = fbd.SelectedPath;
            }

            if (string.IsNullOrEmpty(destFolder))
            {
                try { Directory.Delete(pngFolder, recursive: true); } catch { }
                SetStatus("Lossless PNG sequence discarded.");
                return;
            }

            // 2. Move temp folder contents into a uniquely-named subfolder.
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string finalFolder = Path.Combine(destFolder, $"FracturingFog_Zoom_{stamp}");
            try
            {
                Directory.CreateDirectory(finalFolder);
                foreach (string src in Directory.EnumerateFiles(pngFolder))
                {
                    string dst = Path.Combine(finalFolder, Path.GetFileName(src));
                    File.Move(src, dst, overwrite: true);
                }
                try { Directory.Delete(pngFolder, recursive: true); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to move PNG sequence:\n{ex.Message}",
                    "Save Lossless", MessageBoxButtons.OK, MessageBoxIcon.Error);
                try { Directory.Delete(pngFolder, recursive: true); } catch { }
                return;
            }

            SetStatus($"Lossless PNG sequence saved: {finalFolder}");

            if (encode == Views.VideoDialog.LosslessEncodeChoice.None) return;
            if (!FfmpegEncoder.IsAvailable())
            {
                MessageBox.Show(
                    "ffmpeg.exe is no longer available — keeping PNG sequence only.",
                    "Save Lossless", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 3. Encode with ffmpeg next to the PNG folder.
            var preset = encode switch
            {
                Views.VideoDialog.LosslessEncodeChoice.LosslessH264Mp4 =>
                    FfmpegEncoder.Preset.LosslessH264Mp4,
                Views.VideoDialog.LosslessEncodeChoice.Ffv1Mkv =>
                    FfmpegEncoder.Preset.Ffv1Mkv,
                Views.VideoDialog.LosslessEncodeChoice.HighQualityH264Mp4 =>
                    FfmpegEncoder.Preset.HighQualityH264Mp4,
                _ => FfmpegEncoder.Preset.LosslessH264Mp4,
            };
            string ext = FfmpegEncoder.DefaultExtensionFor(preset);
            string outPath = Path.Combine(destFolder, $"FracturingFog_Zoom_{stamp}.{ext}");

            SetStatus($"Encoding lossless video → {Path.GetFileName(outPath)} (ffmpeg)…");
            try
            {
                var (ok, log) = await FfmpegEncoder.EncodeAsync(
                    finalFolder, outPath, preset,
                    onProgressLine: line =>
                    {
                        if (line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase))
                        {
                            try { BeginInvoke(() => SetStatus($"ffmpeg: {line.Trim()}")); }
                            catch { }
                        }
                    });
                if (ok)
                    SetStatus($"Encoded: {Path.GetFileName(outPath)}");
                else
                    ShowCopyableError("Save Lossless",
                        "ffmpeg encode failed.\n\n" +
                        DiagnoseFfmpegFailure(log) +
                        "\n--- Full output ---\n" +
                        TailLog(log));
            }
            catch (Exception ex)
            {
                ShowCopyableError("Save Lossless",
                    $"ffmpeg encode exception:\n{ex.Message}\n\n{ex.StackTrace}");
            }
        }

        // Inspects the diagnostic block returned by FfmpegEncoder and prepends
        // a plain-English hint when the exit code matches a well-known cause.
        private static string DiagnoseFfmpegFailure(string log)
        {
            // FfmpegEncoder embeds "ffmpeg exit code: N" as the first line of
            // its diagnostic block.
            int exitCode = 0;
            int idx = log.IndexOf("ffmpeg exit code:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int nlEnd = log.IndexOf('\n', idx);
                string line = nlEnd > 0 ? log.Substring(idx, nlEnd - idx) : log.Substring(idx);
                int colon = line.IndexOf(':');
                if (colon > 0 && int.TryParse(line[(colon + 1)..].Trim(), out int n))
                    exitCode = n;
            }

            return exitCode switch
            {
                -1073741515 =>  // 0xC0000135 STATUS_DLL_NOT_FOUND
                    "Diagnosis: ffmpeg.exe launched but cannot load a required DLL.\n" +
                    "Fix: replace it with a *static* build (single-exe, no DLL deps).\n" +
                    "Recommended: https://www.gyan.dev/ffmpeg/builds/  →  release essentials zip\n" +
                    "Extract bin\\ffmpeg.exe into the app's Tools\\ folder.\n",
                -1073741819 =>  // 0xC0000005 access violation
                    "Diagnosis: ffmpeg crashed (access violation). The build may be corrupt\n" +
                    "or incompatible. Try a different static build.\n",
                -1073741701 =>  // 0xC000007B not a valid Win32 app
                    "Diagnosis: arch mismatch. Need an x64 ffmpeg.exe for this process.\n",
                _ => string.Empty,
            };
        }

        // Shows an error dialog whose text can be selected & copied to the
        // clipboard. Standard MessageBox supports Ctrl+C but no visible
        // selection — users assume the text is uncopyable. This replacement
        // uses a read-only multiline TextBox so selection is obvious.
        private void ShowCopyableError(string title, string body)
        {
            using var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = true,
                ShowInTaskbar = false,
                ClientSize = new Size(640, 360),
                MinimumSize = new Size(420, 240),
                BackColor = Color.FromArgb(30, 30, 35),
                ForeColor = Color.Gainsboro,
            };

            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Color.Gainsboro,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = body.Replace("\n", Environment.NewLine),
            };

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            var copy = new Button
            {
                Text = "Copy",
                Width = 90,
                Height = 28,
                Top = 6,
                Left = 8,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 60),
                ForeColor = Color.Gainsboro,
            };
            copy.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(tb.Text); }
                catch { /* clipboard occasionally locked — ignore */ }
            };

            var ok = new Button
            {
                Text = "OK",
                Width = 90,
                Height = 28,
                Top = 6,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 60),
                ForeColor = Color.Gainsboro,
            };
            ok.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
            ok.Left = btnPanel.ClientSize.Width - ok.Width - 8;
            btnPanel.Resize += (_, _) => ok.Left = btnPanel.ClientSize.Width - ok.Width - 8;

            btnPanel.Controls.Add(copy);
            btnPanel.Controls.Add(ok);
            f.Controls.Add(tb);
            f.Controls.Add(btnPanel);
            f.AcceptButton = ok;
            f.CancelButton = ok;

            f.ShowDialog(this);
        }

        private static string TailLog(string log, int maxChars = 1500)
        {
            if (log.Length <= maxChars) return log;
            return "…" + log[^maxChars..];
        }

        private void PromptSaveVideoFile(string tempPath)
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Save Video Zoom",
                Filter = "MP4 video (*.mp4)|*.mp4",
                DefaultExt = "mp4",
                FileName = $"FracturingFog_Zoom_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
                OverwritePrompt = true,
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    File.Move(tempPath, sfd.FileName, overwrite: true);
                    SetStatus($"Video saved: {Path.GetFileName(sfd.FileName)}");
                }
                catch (Exception ex)
                {
                    try { File.Delete(tempPath); } catch { }
                    MessageBox.Show($"Failed to save video:\n{ex.Message}",
                        "Save Video", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                try { File.Delete(tempPath); } catch { }
                SetStatus("Recorded video discarded.");
            }
        }

        private void StopVideo()
        {
            lock (_videoLock) _videoCts?.Cancel();
        }

        private async Task VideoLoop(
            QDCoord cx0, QDCoord cy0, double z0,
            QDCoord cx1, QDCoord cy1, double z1,
            double seconds,
            CancellationToken ct,
            bool reverse = false)
        {
            if (_calculator == null || _renderer == null) return;

            // Cancel any in-flight ordinary calculation so it doesn't race
            // with our per-frame calculations.
            lock (_calcLock) { _calcCts?.Cancel(); }

            double logZ0 = Math.Log(Math.Max(z0, 1e-12));
            double logZ1 = Math.Log(Math.Max(z1, 1e-12));

            bool centerMoves = !QDEqual(cx0, cx1) || !QDEqual(cy0, cy1);
            bool zoomChanges = z0 != z1;

            double panSecs = centerMoves && zoomChanges ? seconds * VideoPanFraction
                            : centerMoves ? seconds
                            : 0.0;
            double zoomSecs = seconds - panSecs;

            if (reverse)
            {
                // Reverse phase order: zoom out first (at cx0/cy0), then pan
                // at the shallow end zoom z1. This mirrors the forward flow
                // (pan@z0 → zoom@cx1) and avoids panning across huge pixel
                // distances at deep zoom.
                if (zoomSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swZoom = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swZoom.Elapsed.TotalSeconds / zoomSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);

                        double zoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);

                        await RenderVideoFrame(cx0, cy0, zoom, ct);
                        if (last) break;
                    }
                }

                if (panSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swPan = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swPan.Elapsed.TotalSeconds / panSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);

                        QDCoord cx = QDLerp(cx0, cx1, te);
                        QDCoord cy = QDLerp(cy0, cy1, te);

                        await RenderVideoFrame(cx, cy, z1, ct);
                        if (last) break;
                    }
                }
            }
            else
            {
                var sw = Stopwatch.StartNew();

                // ── Phase 1: pan to target CX/CY at current zoom ──────────────────
                if (panSecs > 0.0)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        double t = sw.Elapsed.TotalSeconds / panSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);

                        QDCoord cx = QDLerp(cx0, cx1, te);
                        QDCoord cy = QDLerp(cy0, cy1, te);

                        await RenderVideoFrame(cx, cy, z0, ct);
                        if (last) break;
                    }
                }

                // ── Phase 2: zoom in at target CX/CY (full QD precision) ─────────
                if (zoomSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swZoom = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swZoom.Elapsed.TotalSeconds / zoomSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);

                        double zoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);

                        await RenderVideoFrame(cx1, cy1, zoom, ct);
                        if (last) break;
                    }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    UpdateCoordBoxes();
                    _miniMapPanel?.RefreshIndicator();
                });
            }
        }

        private async Task RenderVideoFrame(QDCoord cx, QDCoord cy, double zoom, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            await InvokeAsync(() =>
            {
                if (_disposed) return;
                ApplyVideoFrameState(cx, cy, zoom);
            });

            if (ct.IsCancellationRequested) return;
            await Task.Run(() =>
            {
                if (_calculator == null) return;
                _calculator.Calculate(ct);
            }, ct);

            if (ct.IsCancellationRequested) return;
            await InvokeAsync(() =>
            {
                if (_disposed || _calculator == null || _renderer == null) return;
                // Adaptive contrast (histogram equalization) — re-colors the
                // buffer in place before brightness/contrast/grid overlay and
                // before MP4/PNG capture, matching the interactive Calculate path.
                if (_histogramEq > 0)
                    _calculator.ApplyHistogramEqualization(_histogramEq / 100.0);
                UploadProcessedBuffer(_calculator, _renderer);
                CaptureMp4Frame();
            });
        }

        // Feeds the post-processed buffer (what was just sent to the GPU) to
        // any active recorders (MP4 + lossless PNG sequence). No-op when no
        // recording is active — slideshow legs never set either writer. A
        // write failure disables that recorder but does not interrupt the zoom
        // and does not affect the other recorder.
        private void CaptureMp4Frame()
        {
            var buf = _lastUploadedBuffer;
            if (buf == null) return;

            var mp4 = _videoMp4Writer;
            var sw = _videoMp4Sw;
            if (mp4 != null && sw != null)
            {
                try { mp4.WriteFrame(buf, sw.Elapsed.Ticks); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Mp4 frame write failed: {ex.Message}");
                    ClearVideoRecordingState(deleteTempFile: true);
                    SetStatus("MP4 recording disabled (encoder error).");
                }
            }

            var png = _videoPngWriter;
            if (png != null)
            {
                try { png.WriteFrame(buf); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PNG frame write failed: {ex.Message}");
                    ClearLosslessRecordingState(deleteFolder: true);
                    SetStatus("Lossless recording disabled (PNG write error).");
                }
            }
        }

        // Per-limb lerp. The sum of the limbs equals the represented value, and
        // sum(lerp(a_i, b_i, t)) = lerp(sum(a_i), sum(b_i), t), so a separate
        // lerp on each limb is arithmetically equivalent to lerping the full
        // multi-precision number — no normalisation needed.
        private static QDCoord QDLerp(QDCoord a, QDCoord b, double t) => new(
            a.Hi + (b.Hi - a.Hi) * t,
            a.Lo + (b.Lo - a.Lo) * t,
            a.X2 + (b.X2 - a.X2) * t,
            a.X3 + (b.X3 - a.X3) * t);

        private static bool QDEqual(QDCoord a, QDCoord b)
            => a.Hi == b.Hi && a.Lo == b.Lo && a.X2 == b.X2 && a.X3 == b.X3;

        // Updates view + calculator state for one video frame. Mirrors the
        // relevant pieces of ApplyViewState() but stays on the UI thread and
        // auto-promotes the quality preset when zoom passes a tier boundary.
        // All four limbs of cx/cy are pushed through so deep-zoom targets land
        // on the correct pixel (otherwise CenterXLo etc. are zeroed and the
        // view ends up many pixels off at zoom >= 1e15).
        private void ApplyVideoFrameState(QDCoord cx, QDCoord cy, double zoom)
        {
            if (_calculator == null) return;

            // Cap quality at Ultra — video disallows the Extreme regime.
            QualityPreset target = _quality;
            double cap = QualityPreset.Ultra.ZoomMax;
            if (zoom > cap) zoom = cap;

            if (zoom > _quality.ZoomMax)
            {
                foreach (var p in QualityPreset.All)
                {
                    if (p.Tier == QualityTier.Extreme) continue;
                    if (p.ZoomMax >= zoom) { target = p; break; }
                }
                if (target.Tier != _quality.Tier)
                {
                    _quality = target;
                    _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
                    _qualityCombo.Text = _quality.Name;
                    _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
                    //if (_qualityCombo2 != null) _qualityCombo2.Text = _quality.Name;
                }
            }

            _centerX = cx.Hi; _centerXLo = cx.Lo; _centerX2 = cx.X2; _centerX3 = cx.X3;
            _centerY = cy.Hi; _centerYLo = cy.Lo; _centerY2 = cy.X2; _centerY3 = cy.X3;
            _zoom = Math.Clamp(zoom, _quality.ZoomMin, _quality.ZoomMax);

            _calculator.CenterX = _centerX;
            _calculator.CenterXLo = _centerXLo;
            _calculator.CenterX2 = _centerX2;
            _calculator.CenterX3 = _centerX3;
            _calculator.CenterY = _centerY;
            _calculator.CenterYLo = _centerYLo;
            _calculator.CenterY2 = _centerY2;
            _calculator.CenterY3 = _centerY3;
            _calculator.Zoom = _zoom;
            _calculator.Quality = _quality;

            if (_iterLocked)
            {
                _calculator.MaxIterations = _lockedIterations;
            }
            else
            {
                int it = _quality.ComputeIterations(_zoom);
                // Honour the region's authored iteration count as a floor so
                // deep targets don't render as in-set black when the preset's
                // formula produces fewer iterations than the region needs.
                if (_videoTargetIterations > it) it = _videoTargetIterations;
                _calculator.MaxIterations = it;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Video Slideshow: loop forever — pick random non-Extreme region + random
        // theme, reset to classic view, run a video zoom, pause 7 s, repeat.
        // ─────────────────────────────────────────────────────────────────────

        public void StartVideoSlideshow(double? secondsOverride = null, bool constantRate = false, bool reverse = false)
        {
            if (_videoSlideshowRunning) return;
            if (_calculator == null || _renderer == null) return;

            double seconds = secondsOverride ?? VideoSlideshowSeconds;

            _videoSlideshowRunning = true;
            _videoButton.Text = "■ Stop";
            _videoButton.BackColor = Color.FromArgb(70, 30, 30);
            _videoButton.FlatAppearance.BorderColor = Color.FromArgb(120, 50, 50);
            string mode = reverse ? "reverse " : "";
            SetStatus(constantRate
                ? $"Video {mode}slideshow running (constant rate, min {seconds:F1}s)…"
                : $"Video {mode}slideshow running ({seconds:F1}s per leg)…");

            CancellationTokenSource cts;
            lock (_videoSlideshowLock)
            {
                _videoSlideshowCts?.Cancel();
                _videoSlideshowCts = new CancellationTokenSource();
                cts = _videoSlideshowCts;
            }

            Task.Run(() => VideoSlideshowLoop(seconds, constantRate, reverse, cts.Token), cts.Token)
                .ContinueWith(t =>
                {
                    if (!IsHandleCreated || _disposed) return;
                    Invoke(() =>
                    {
                        _videoSlideshowRunning = false;
                        _videoButton.Text = "Video";
                        _videoButton.BackColor = Color.FromArgb(55, 40, 70);
                        _videoButton.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 130);
                        _videoTargetIterations = 0;
                        if (t.IsFaulted)
                            SetStatus($"Video slideshow error: {t.Exception?.InnerException?.Message}");
                        else
                            SetStatus("Video slideshow stopped.");
                    });
                }, TaskScheduler.Default);
        }

        public void StopVideoSlideshow()
        {
            lock (_videoSlideshowLock) _videoSlideshowCts?.Cancel();
        }

        public bool IsVideoSlideshowRunning => _videoSlideshowRunning;

        private async Task VideoSlideshowLoop(double seconds, bool constantRate, bool reverse, CancellationToken ct)
        {
            // Exclude Extreme-tier regions and regions whose zoom is at or
            // near the default classic view (zoom <= 5) — those produce a
            // near-zero log-range against DefaultZoom (0.3) and would either
            // be visually pointless or, in constant-rate mode, blow up the
            // duration of every other leg by becoming the minLogRange anchor.
            const double SlideshowMinRegionZoom = 5.0;
            var regions = new System.Collections.Generic.List<FractalRegion>();
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                if (r.QualityPreset.Tier != QualityTier.Extreme
                    && r.Zoom > SlideshowMinRegionZoom)
                    regions.Add(r);

            // palettes is rebuilt per leg so themes whose MaxRecommendedZoom
            // is below the leg's deepest endpoint are excluded.
            var palettes = GetAllPaletteNames();
            if (regions.Count == 0 || palettes.Count == 0) return;

            int lastRegion = -1, lastTheme = -1;
            double ultraMax = QualityPreset.Ultra.ZoomMax;
            double draftMin = QualityPreset.Draft.ZoomMin;

            // Constant-rate scaling: the shallowest region in the pool gets
            // exactly `seconds`; deeper regions get a proportionally longer
            // duration so the log-zoom rate is invariant across legs.
            // Floor minLogRange at log(SlideshowMinRegionZoom / DefaultZoom)
            // (~2.8) as a sanity bound — protects against future pool entries
            // that creep just past the filter and would otherwise produce
            // multi-minute legs.
            double logStart = Math.Log(DefaultZoom);
            double minLogRange = double.MaxValue;
            if (constantRate)
            {
                foreach (var r in regions)
                {
                    double rtz = Math.Clamp(r.Zoom, draftMin, ultraMax);
                    double range = Math.Log(rtz) - logStart;
                    if (range > 0 && range < minLogRange) minLogRange = range;
                }
                double minFloor = Math.Log(SlideshowMinRegionZoom / DefaultZoom);
                if (minLogRange < minFloor) minLogRange = minFloor;
                if (minLogRange == double.MaxValue || minLogRange <= 0)
                    constantRate = false;   // pathological pool — fall back
            }

            while (!ct.IsCancellationRequested)
            {
                // Pick a region different from the previous one.
                int ri;
                do { ri = _slideshowRng.Next(regions.Count); }
                while (regions.Count > 1 && ri == lastRegion);
                lastRegion = ri;
                var region = regions[ri];

                // Clamp target zoom to Ultra cap.
                double tz = region.Zoom;
                if (tz > ultraMax) tz = ultraMax;
                if (tz < draftMin) tz = draftMin;

                // Refresh the palette pool for this leg's deepest endpoint.
                // Reverse legs start deep and zoom out, forward legs zoom in to
                // a deep target — either way the cap to enforce is the leg's
                // deep end. Pick a theme from the filtered pool, then fall back
                // to the unfiltered list only if the filter empties everything.
                var legPalettes = Models.ColorPalette.GetPaletteNamesForZoom(tz);
                if (legPalettes.Count == 0) legPalettes = palettes;

                // Pick a theme different from the previous one. lastTheme
                // is reset whenever the per-leg pool differs in size from the
                // full pool, since the previous index may no longer point at
                // the same name.
                if (legPalettes.Count != palettes.Count) lastTheme = -1;
                int ti;
                do { ti = _slideshowRng.Next(legPalettes.Count); }
                while (legPalettes.Count > 1 && ti == lastTheme);
                lastTheme = ti;
                string theme = legPalettes[ti];

                // Per-leg duration: fixed in variable-rate mode; scales with
                // log-zoom depth in constant-rate mode (user-supplied seconds
                // applies to the shallowest region; deeper regions take longer).
                double legSeconds = seconds;
                if (constantRate)
                {
                    double logRange = Math.Log(tz) - logStart;
                    if (logRange > 0)
                        legSeconds = seconds * (logRange / minLogRange);
                    if (legSeconds < seconds) legSeconds = seconds;   // never below minimum
                }

                // Snapshot the current on-screen frame so we can cross-fade
                // into the new leg's classic starting view instead of hard
                // cutting. On the first iteration this is the user's pre-
                // slideshow view; on subsequent iterations it is the previous
                // leg's deep-zoom final frame.
                uint[] oldLegBuf;
                lock (_calcLock)
                {
                    if (_lastUploadedBuffer != null
                        && _calculator != null
                        && _lastUploadedWidth == _calculator.Width
                        && _lastUploadedHeight == _calculator.Height)
                    {
                        oldLegBuf = new uint[_lastUploadedBuffer.Length];
                        _lastUploadedBuffer.CopyTo(oldLegBuf, 0);
                    }
                    else if (_calculator != null)
                    {
                        oldLegBuf = new uint[_calculator.ColorBuffer.Length];
                        _calculator.ColorBuffer.CopyTo(oldLegBuf, 0);
                    }
                    else
                    {
                        oldLegBuf = Array.Empty<uint>();
                    }
                }

                // UI: jump to the leg's starting view (classic for forward,
                // deep region for reverse), force the matching quality tier,
                // apply theme. No render here — we cross-fade explicitly below
                // before VideoLoop.
                await InvokeAsync(() =>
                {
                    if (_disposed) return;

                    if (reverse)
                    {
                        // Start each leg at the deep region view; animate back
                        // to classic during VideoLoop.
                        _centerX = region.CenterX; _centerXLo = region.CenterXLo;
                        _centerX2 = region.CenterX2; _centerX3 = region.CenterX3;
                        _centerY = region.CenterY; _centerYLo = region.CenterYLo;
                        _centerY2 = region.CenterY2; _centerY3 = region.CenterY3;
                        _zoom = tz;

                        // Pick quality tier matching the deep start zoom.
                        QualityPreset startQ = QualityPreset.Standard;
                        foreach (var p in QualityPreset.All)
                        {
                            if (p.Tier == QualityTier.Extreme) continue;
                            if (p.ZoomMax >= _zoom) { startQ = p; break; }
                        }
                        _quality = startQ;
                    }
                    else
                    {
                        _centerX = DefaultCenterX; _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
                        _centerY = DefaultCenterY; _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
                        _zoom = DefaultZoom;
                        _quality = QualityPreset.Standard;
                    }

                    _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
                    _qualityCombo.Text = _quality.Name;
                    _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
                    //if (_qualityCombo2 != null) _qualityCombo2.Text = _quality.Name;

                    // Per-leg iteration floor — honours each region's authored
                    // iter count for the upcoming VideoLoop.
                    _videoTargetIterations = region.Iterations;

                    // Push the leg's starting state into the calculator so the
                    // pre-render below produces the correct fade-in frame.
                    if (_calculator != null)
                    {
                        _calculator.CenterX = _centerX;
                        _calculator.CenterXLo = _centerXLo;
                        _calculator.CenterX2 = _centerX2;
                        _calculator.CenterX3 = _centerX3;
                        _calculator.CenterY = _centerY;
                        _calculator.CenterYLo = _centerYLo;
                        _calculator.CenterY2 = _centerY2;
                        _calculator.CenterY3 = _centerY3;
                        _calculator.Zoom = _zoom;
                        _calculator.Quality = _quality;
                        if (_iterLocked)
                            _calculator.MaxIterations = _lockedIterations;
                        else
                        {
                            int it = _quality.ComputeIterations(_zoom);
                            if (reverse && region.Iterations > it) it = region.Iterations;
                            _calculator.MaxIterations = it;
                        }
                    }

                    ApplyColorThemeSilent(theme);
                    SetStatus($"Video {(reverse ? "reverse " : "")}slideshow: {region.Name}  •  {theme}  ({legSeconds:F1}s)");
                });

                if (ct.IsCancellationRequested) break;

                // Pre-render the classic starting frame on a background thread.
                // Snapshot eq strength once so a slider change mid-render does
                // not split the result across two values.
                int eqStrengthSnapshot = _histogramEq;
                uint[] newLegBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    _calculator.Calculate(ct);
                    if (eqStrengthSnapshot > 0)
                        _calculator.ApplyHistogramEqualization(eqStrengthSnapshot / 100.0);
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                if (ct.IsCancellationRequested) break;

                // Cross-fade prev-leg final frame → new-leg classic starting frame.
                // Same per-pixel CPU dissolve the Slideshow uses for theme/region
                // transitions; reused here so video-slideshow leg boundaries no
                // longer hard-cut.
                if (oldLegBuf.Length == newLegBuf.Length && oldLegBuf.Length > 0)
                {
                    const int legFadeSteps = 24;
                    const int legFadeStepMs = 80;   // ~1.9 s total
                    await CrossFade(oldLegBuf, newLegBuf, legFadeSteps, legFadeStepMs, ct);
                }
                else
                {
                    await InvokeAsync(() =>
                    {
                        if (!_disposed && _renderer != null && _calculator != null)
                            _renderer.UpdateTexture(newLegBuf, _calculator.Width, _calculator.Height);
                    });
                }

                if (ct.IsCancellationRequested) break;

                QDCoord legStartCX, legStartCY, legTargetCX, legTargetCY;
                double legStartZoom, legTargetZoom;
                if (reverse)
                {
                    legStartCX = new QDCoord(region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    legStartCY = new QDCoord(region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);
                    legStartZoom = tz;
                    legTargetCX = new QDCoord(DefaultCenterX, 0.0, 0.0, 0.0);
                    legTargetCY = new QDCoord(DefaultCenterY, 0.0, 0.0, 0.0);
                    legTargetZoom = DefaultZoom;
                }
                else
                {
                    legStartCX = new QDCoord(DefaultCenterX, 0.0, 0.0, 0.0);
                    legStartCY = new QDCoord(DefaultCenterY, 0.0, 0.0, 0.0);
                    legStartZoom = DefaultZoom;
                    legTargetCX = new QDCoord(region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    legTargetCY = new QDCoord(region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);
                    legTargetZoom = tz;
                }

                await VideoLoop(legStartCX, legStartCY, legStartZoom,
                                legTargetCX, legTargetCY, legTargetZoom,
                                legSeconds, ct, reverse: reverse);

                if (ct.IsCancellationRequested) break;

                // Pause 7 s before next leg.
                try { await Task.Delay(VideoSlideshowPauseMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}