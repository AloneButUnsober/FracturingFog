using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using static FracturingFog.Views.FormHelpers;

namespace FracturingFog.Views
{
    /// <summary>
    /// Floating help / documentation window.  Mirrors the borderless dark
    /// look-and-feel of <see cref="FloatingMenu"/>: tab-based navigation with
    /// About, System Info, Features, Mandelbrot Math, and Mandelbrot Bio.
    /// </summary>
    public sealed class FloatingHelp : Form
    {
        #region UI Components

        private readonly Form _parentForm;
        private readonly string _programName;
        private readonly string _programVersion;
        private readonly Func<string> _rendererDescriptionProvider;
        private readonly Func<(int Width, int Height, int MaxIter, bool HighPrecision)?> _calculatorInfoProvider;

        private readonly Panel _headerPanel;
        private readonly Label _titleLabel;
        private readonly Button _refreshButton;
        private readonly Button _closeButton;
        private readonly Panel _footerPanel;
        private readonly Button _footerCloseButton;
        private readonly TabControl _tabs;
        private readonly ToolTip _toolTip = new();

        // Mouse click-n-drag window repositioning
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private bool _disposed;

        #endregion UI Components

        #region Events

        public event EventHandler? OnCloseHelpClick;

        #endregion Events

        #region DLL Imports

        [DllImport("User32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("User32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        #endregion DLL Imports

        #region Constructors

        public FloatingHelp(
            Form parentForm,
            string programName,
            string programVersion,
            Func<string> rendererDescriptionProvider,
            Func<(int Width, int Height, int MaxIter, bool HighPrecision)?> calculatorInfoProvider)
        {
            _parentForm = parentForm;
            _programName = programName;
            _programVersion = programVersion;
            _rendererDescriptionProvider = rendererDescriptionProvider;
            _calculatorInfoProvider = calculatorInfoProvider;

            ClientSize = new Size(560, 686);
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            Text = $"{_programName} — Help";

            // ESC closes
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) OnCloseHelpClick?.Invoke(this, EventArgs.Empty);
            };

            #region Header

            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _headerPanel.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            _titleLabel = new Label
            {
                Text = $"{_programName} v{_programVersion}  —  Help",
                Left = 6,
                Top = 6,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 100),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            _titleLabel.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };
            _headerPanel.Controls.Add(_titleLabel);

            _closeButton = MakeBtn("X", 36, ClientSize.Width - 42, 6, "Close help window (Esc)");
            _closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _closeButton.BackColor = Color.FromArgb(80, 35, 35);
            _closeButton.FlatAppearance.BorderColor = Color.FromArgb(140, 60, 60);
            _closeButton.ForeColor = Color.FromArgb(240, 200, 200);
            _closeButton.Padding = new Padding(0, 0, 1, 1);
            _closeButton.Margin = new Padding(0);
            _closeButton.Click += (s, e) => OnCloseHelpClick?.Invoke(s, e);
            _headerPanel.Controls.Add(_closeButton);
            _toolTip.SetToolTip(_closeButton, "Close help window (Esc)");

            _refreshButton = MakeBtn("Refresh", 70, ClientSize.Width - 118, 6, "Refresh hardware/system info");
            _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshButton.Click += (s, e) => RebuildSystemInfoTab();
            _headerPanel.Controls.Add(_refreshButton);
            _toolTip.SetToolTip(_refreshButton, "Refresh hardware/system info");

            #endregion Header

            #region Footer

            _footerPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _footerPanel.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            _footerCloseButton = MakeBtn("Close", 110, (ClientSize.Width - 110) / 2, 8, "Close help window (Esc)");
            _footerCloseButton.Height = 28;
            _footerCloseButton.Anchor = AnchorStyles.Top;
            _footerCloseButton.BackColor = Color.FromArgb(80, 35, 35);
            _footerCloseButton.FlatAppearance.BorderColor = Color.FromArgb(140, 60, 60);
            _footerCloseButton.ForeColor = Color.FromArgb(240, 220, 220);
            _footerCloseButton.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _footerCloseButton.Click += (s, e) => OnCloseHelpClick?.Invoke(s, e);
            _footerPanel.Controls.Add(_footerCloseButton);
            _toolTip.SetToolTip(_footerCloseButton, "Close help window (Esc)");

            #endregion Footer

            #region Tabs

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Normal,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(92, 26),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _tabs.DrawItem += OnDrawTabItem;

            _tabs.TabPages.Add(BuildAboutTab());
            _tabs.TabPages.Add(BuildSystemInfoTab());
            _tabs.TabPages.Add(BuildFeaturesTab());
            _tabs.TabPages.Add(BuildAudioTab());
            _tabs.TabPages.Add(BuildEditorTab());
            _tabs.TabPages.Add(BuildMathTab());
            _tabs.TabPages.Add(BuildBioTab());

            #endregion Tabs

            Controls.Add(_tabs);
            Controls.Add(_footerPanel);
            Controls.Add(_headerPanel);

            _footerPanel.Resize += (s, e) =>
                _footerCloseButton.Left = (_footerPanel.ClientSize.Width - _footerCloseButton.Width) / 2;

            FormClosing += OnFormClosing;
        }

        #endregion Constructors

        #region Tab Builders

        private TabPage BuildAboutTab()
        {
            var page = MakePage("About");

            var rtb = MakeRichText(page);
            var sb = new StringBuilder();
            sb.AppendLine($"{_programName} v{_programVersion}");
            sb.AppendLine(new string('─', 60));
            sb.AppendLine();
            sb.AppendLine("Real-time high-precision Mandelbrot set explorer.");
            sb.AppendLine();
            sb.AppendLine("=== Platform ===");
            sb.AppendLine($"Operating System : {Environment.OSVersion}");
            sb.AppendLine($"OS Architecture  : {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Process Arch.    : {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($".NET Runtime     : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine();
            sb.AppendLine("=== Graphics ===");
            sb.AppendLine($"Active renderer  : {SafeRendererDescription()}");
            sb.AppendLine("DirectX backends : Direct3D 11 (FL10.0+), Direct3D 12 (when available)");
            sb.AppendLine("Vortice bindings : v3.8.3");
            sb.AppendLine();
            sb.AppendLine("=== Build ===");
            sb.AppendLine("Target framework : net10.0-windows");
            sb.AppendLine("Platform target  : x64");
            sb.AppendLine("High-DPI mode    : PerMonitorV2");
            sb.AppendLine();
            sb.AppendLine("=== Credits ===");
            sb.AppendLine("UI / engine      : Bradley Brown");
            sb.AppendLine("Renderer         : Vortice.Windows (MIT)");
            sb.AppendLine("Video encoding   : ffmpeg (LGPL build)");

            rtb.Text = sb.ToString();

            // External project / docs links
            int linkTop = page.ClientSize.Height - 36;
            page.Controls.Add(MakeLink("Mandelbrot set (Wikipedia)",
                "https://en.wikipedia.org/wiki/Mandelbrot_set",
                12, linkTop, page));
            page.Controls.Add(MakeLink("DirectX Diagnostic (dxdiag)",
                "ms-settings:about",
                240, linkTop, page));

            return page;
        }

        private TabPage BuildSystemInfoTab()
        {
            var page = MakePage("Hardware");
            page.Tag = "system-info";
            var rtb = MakeRichText(page);
            rtb.Tag = "sysinfo-body";
            rtb.Text = BuildSystemInfoText();
            return page;
        }

        private void RebuildSystemInfoTab()
        {
            foreach (TabPage page in _tabs.TabPages)
            {
                if ((page.Tag as string) != "system-info") continue;
                foreach (Control c in page.Controls)
                {
                    if (c is RichTextBox rtb && (rtb.Tag as string) == "sysinfo-body")
                    {
                        rtb.Text = BuildSystemInfoText();
                    }
                }
            }
        }

        private string BuildSystemInfoText()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== Renderer ===");
            sb.AppendLine($"Active:          {SafeRendererDescription()}");
            try { sb.AppendLine($"D3D12 available: {DirectX12Renderer.IsAvailable()}"); }
            catch (Exception ex) { sb.AppendLine($"D3D12 available: (probe failed: {ex.Message})"); }
            sb.AppendLine();

            sb.AppendLine("=== GPU Adapters (DXGI) ===");
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                uint idx = 0;
                while (factory.EnumAdapters1(idx, out var adapter).Success)
                {
                    var desc = adapter.Description1;
                    sb.AppendLine($"Adapter {idx}: {desc.Description}");
                    sb.AppendLine($"  Vendor ID:      0x{desc.VendorId:X4}");
                    sb.AppendLine($"  Device ID:      0x{desc.DeviceId:X4}");
                    sb.AppendLine($"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB");
                    sb.AppendLine($"  Shared RAM:     {desc.SharedSystemMemory / (1024 * 1024)} MB");
                    adapter.Dispose();
                    idx++;
                }
            }
            catch (Exception ex) { sb.AppendLine($"  (DXGI enumeration failed: {ex.Message})"); }

            sb.AppendLine();
            sb.AppendLine("=== D3D11 Feature Level ===");
            try
            {
                Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    null,
                    Vortice.Direct3D.DriverType.Hardware,
                    Vortice.Direct3D11.DeviceCreationFlags.None,
                    null,
                    out _, out var fl, out _);
                sb.AppendLine($"Max Feature Level: {fl}");
            }
            catch { sb.AppendLine("  (Could not query D3D11 feature level.)"); }

            sb.AppendLine();
            sb.AppendLine("=== Displays ===");
            try
            {
                int sidx = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    sb.AppendLine($"Screen {sidx}: {screen.DeviceName}{(screen.Primary ? " (primary)" : "")}");
                    sb.AppendLine($"  Bounds:   {screen.Bounds.Width}×{screen.Bounds.Height}  @ ({screen.Bounds.X},{screen.Bounds.Y})");
                    sb.AppendLine($"  Working:  {screen.WorkingArea.Width}×{screen.WorkingArea.Height}");
                    sb.AppendLine($"  BitDepth: {screen.BitsPerPixel}");
                    sidx++;
                }
            }
            catch (Exception ex) { sb.AppendLine($"  (Screen enumeration failed: {ex.Message})"); }

            sb.AppendLine();
            sb.AppendLine("=== CPU / OS ===");
            sb.AppendLine($"Logical CPUs:    {Environment.ProcessorCount}");
            sb.AppendLine($"Machine name:    {Environment.MachineName}");
            sb.AppendLine($"User:            {Environment.UserName}");
            sb.AppendLine($"OS:              {Environment.OSVersion}");
            sb.AppendLine($".NET Runtime:    {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Architecture:    {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"System page:     {Environment.SystemPageSize} bytes");

            sb.AppendLine();
            sb.AppendLine("=== Memory ===");
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                sb.AppendLine($"Total physical:  {gcInfo.TotalAvailableMemoryBytes / (1024 * 1024)} MB");
                sb.AppendLine($"Process working: {Environment.WorkingSet / (1024 * 1024)} MB");
                sb.AppendLine($"GC total alloc:  {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            catch (Exception ex) { sb.AppendLine($"  (Memory query failed: {ex.Message})"); }

            sb.AppendLine();
            sb.AppendLine("=== Fractal Calculator ===");
            var calc = _calculatorInfoProvider();
            if (calc.HasValue)
            {
                sb.AppendLine($"SIMD vector width (double): {System.Numerics.Vector<double>.Count}");
                sb.AppendLine($"Current size:    {calc.Value.Width}×{calc.Value.Height}");
                sb.AppendLine($"Max iterations:  {calc.Value.MaxIter}");
                sb.AppendLine($"Precision:       {(calc.Value.HighPrecision ? "Double-Double (DD)" : "Double (SP)")}");
            }
            else
            {
                sb.AppendLine("  (Calculator not initialised.)");
            }

            return sb.ToString();
        }

        private TabPage BuildFeaturesTab()
        {
            var page = MakePage("Features");
            var rtb = MakeRichText(page);

            int themeCount = Models.ColorPalette.BuiltIns.Count
                           + Models.ColorPalette.UserPalettes.Count;

            rtb.Text =
@"=== Navigation ===

  Mouse wheel        Zoom in / out at cursor
  Left-click drag    Pan the view
  Double-click       Center on point + zoom in
  Right-click        Context menu (toolbar, mini-map, etc.)
  Reset (R)          Restore default view (-0.5, 0, zoom 0.3)

=== Toolbar / Floating Menu ===

  Span        Span the window across all monitors
  Image       Save a high-resolution PNG screenshot
  Poster      Generate a multi-tile poster-size render
  Slideshow   Auto-cycle regions (30 s) and color themes (10 s)
  Video       Smooth animated zoom from current view to a target
  Menu        Toggle the floating coordinate/control window

  Quality     Standard / DeepHP / Extreme arithmetic presets
              (auto-promotes from double → DD → QD as zoom deepens)
  Theme       Color map selector — {THEME_COUNT} built-in palettes
              plus user-imported JSON themes
  Region      Named bookmarks — built-in tour + your saved views

=== Coordinate / Region Panel ===

  CX, CY      Real / imaginary coordinates of the view center
              (accepts pipe-separated DD/QD limb format for paste-back)
  Zoom        Scalar zoom factor — paste large values like 1e48
  Iterations  Max escape iterations (min 64, no upper cap)
  Lock        Pin iterations during pan/zoom (no auto-recalc)
  Go          Apply the typed coordinates / zoom / iter values
  Flip Y      Mirror the view vertically (negate CY)

  Brightness  −100 … +100 post-process offset
  Contrast    −100 … +100 post-process multiplier
  Adaptive    0 … 100 histogram equalization (reveals flat detail)

  Save / Delete / Exp… / Imp…
              Persist custom regions to JSON, share, reload

=== Color Themes ===

  Categories  Escape-time, distance estimation, orbit traps,
              binary / argument decomposition, domain coloring,
              field lines, histograms, stripe averages, potentials,
              lemniscate, lighting / Phong / PBR (3-D),
              chromostereopsis, post-process, json-imported, etc.
  Exp / Imp   Export / import individual themes as JSON
  Delete      Remove a user-imported theme (built-ins protected)
  Reload      Re-scan disk for edited theme JSON files

=== Overlays / Mini Windows ===

  Grid        Cartesian complex-plane overlay
  Mini Map    Inset showing whole-set position of current view
  Mini Depth  Per-pixel iteration depth heat-map indicator
  Mini Mode   Shrink window to minimum size + on-top, borderless
  On Top      Keep main window above all others

=== Capture ===

  Screenshot  Single-frame PNG at panel resolution or oversampled
  Poster      Multi-tile composite render at print resolution
  Video       Animated keyframe zoom rendered via ffmpeg
  Video Slideshow
              Continuous zoom-out → next-region → zoom-in loop

=== Precision ===

  Double  (SP)    ~15 digits  — zoom ≤ ~1e13
  Double-Double   ~31 digits  — zoom ≤ ~1e25
  Quad-Double     ~62 digits  — zoom ≤ ~1e50+
  Auto-promotion crosses thresholds based on the active view.
  Perturbation theory (Series Approx. + BLA) accelerates deep zooms.
";
            rtb.Text = rtb.Text.Replace("{THEME_COUNT}", themeCount.ToString());
            return page;
        }

        private TabPage BuildAudioTab()
        {
            var page = MakePage("Audio");
            page.Tag = "audio-help";
            var rtb = MakeRichText(page);

            rtb.Text =
@"=== Audio-Reactive Slideshow ===

The slideshow can be driven by music or any audio source so that
color-theme and region transitions land on the beat. When enabled,
the standard fixed-duration timer (12 s / 3 s) is replaced by a
beat counter: every N detected beats trigger a theme change, every
M beats trigger a region change. Cross-fade duration also scales
with the detected BPM (default 3/4 of one beat).

Open the audio settings from:
  Floating Menu  →  Audio  →  ""Audio Settings…""

The dialog is MODELESS — you can leave it open while you start the
slideshow, browse the main view, or change regions. Settings are
applied only when you click OK; clicking Cancel discards changes.

=== Master Enable ===

The Audio-Reactive checkbox in the Floating Menu (above the
""Audio Settings…"" button) is the master switch. When OFF the
slideshow uses fixed-duration timing. When ON the engine is
started automatically whenever the slideshow runs (or remains
running if you start it directly from the dialog).

State persists between launches via
  %APPDATA%\FracturingFog\audio-settings.json

=== Source ===

Four input sources are available, selected by the ""Source""
combo at the top of the dialog:

  System Loopback  Captures whatever is currently playing on the
                   default audio output (Spotify, browser, video
                   players, games). Nothing else to configure;
                   start playing audio anywhere on the PC and the
                   detector will pick it up.

  Audio File       Plays a local file (MP3 / WAV / FLAC / OGG /
                   AIFF / WMA) through the engine. Click ""Browse…""
                   to pick a file. File playback drives the
                   detector and is also rendered to speakers so
                   you hear the same audio that's being analyzed.
                   Playback ends silently when the file finishes
                   — restart with a new file or switch source.

  Microphone       Default capture device. Good for live shows or
                   external speakers. Raise Sensitivity if the
                   mic level is low.

  Fractal Synth    Internally generated audio derived from the
                   fractal itself (closed-loop showcase mode).
                   No external source needed. Two extra options
                   apply only to this source — see below.

The File-path / Browse controls are enabled only when ""Audio File""
is the active source. The Fractal-Synth checkboxes are enabled
only when ""Fractal Synth"" is the active source.

=== Sensitivity ===

Range 0–100 %. Default 50 %.

Controls the onset-detection threshold of the spectral-flux beat
detector. Lower values report only the strongest hits (good for
heavy drums); higher values fire on subtler transients (good for
ambient or speech).

If you are dropping beats — slideshow stays on one theme too long
— raise this. If themes are changing on every snare hit, lower it.

=== Beats per Theme ===

Default 8 (≈ 2 bars at 4/4).

Number of detected beats required before the slideshow advances
to the next color theme. Each beat increments an internal counter;
on reaching this value, the slideshow's theme-skip flag fires and
the cross-fade begins.

  4    Theme changes every bar  — frenetic.
  8    Every two bars           — default, musical.
 16    Every four bars (phrase) — calm.

=== Beats per Region ===

Default 32 (≈ 8 bars).

Number of detected beats before a full region change. Always
≥ Beats-per-Theme; the dialog enforces this on commit. Region
changes also reset the theme counter, so a region change won't
fire on the same beat as a theme change.

=== Synth BPM (Fractal Synth only) ===

Range 30–240. Default 120.

When ""Fractal Synth"" is the source, this is the synth arpeggio's
BPM. The internal synth generates pitches by probing the fractal
along the current viewport, sequenced at this tempo. Routed to
the beat detector when ""Route fractal synth output through
analyzer"" is ticked, so the slideshow can lock to this BPM.

=== Route Fractal Synth through Analyzer ===

When ticked, the synth's audio is fed BACK into the beat detector,
closing the loop: the synth tempo drives the slideshow even
without external audio. Untick to drive the slideshow from a
separate source (microphone, file) while the synth runs silently
on speakers.

Only applies when Source = Fractal Synth.

=== Play Fractal Synth Audio to Speakers ===

When ticked, the synth's audio is rendered to the system's default
output device. When unticked, the synth runs silently — useful if
you only want its output for closed-loop analysis but don't want
to hear it. Untick for headphone-friendly demos.

Only applies when Source = Fractal Synth.

=== Beat-Detector EQ (per-band flux weight) ===

Five sliders, each 0–200 %, default 100 %. Order:

  Bass     20  – 150  Hz   (kick drum, sub bass)
  LowMid   150 – 400  Hz   (bass guitar, low toms)
  Mid      400 – 1500 Hz   (vocals, snare body)
  HighMid  1500 – 4000 Hz  (cymbals attack, vocal sibilance)
  High     4000 – 12 000 Hz (hi-hats, brushes, air)

Each band's positive spectral flux is multiplied by its weight
before summing into the onset signal. 0 % silences the band
entirely; 200 % doubles its contribution.

Use this to steer which instruments drive the beat detector:

  Following the kick drum    Bass 200 %, others 50 %
  Ignoring hi-hat clutter    High 0 %, HighMid 50 %
  Vocal-driven trigger       Mid 200 %, Bass 50 %, High 0 %
  Everything-on (default)    All 100 %

The Reset button restores all five sliders to 100 %.

=== Fade × beat ===

Range 0.10 – 2.00. Default 0.75 (three-quarters of one beat).

Sets cross-fade duration as a fraction of one detected beat.
At 120 BPM, one beat = 500 ms, so:

  0.25     125 ms fade   — snappy, almost cut
  0.50     250 ms fade   — quick
  0.75     375 ms fade   — default, musical
  1.00     500 ms fade   — one full beat
  2.00    1000 ms fade   — luxurious, half-bar fade

Both color-theme and region transitions use this fraction.
Internally clamped to [0.10, 2.00] and to a minimum of 120 ms
absolute, so very high BPMs still produce a visible fade.

Performance: read once per slideshow transition. No render-loop
overhead.

=== BPM and Level Readouts ===

Below the EQ block:

  BPM      Estimated tempo from the analyzer. ""—"" until enough
           beats have been collected (~5–10 s of audio). Updates
           live; if the source's tempo changes, the estimate
           drifts to match within a few bars.

  Level    Per-band energy bars (Bass / Mid / High). Each is an
           8-step ASCII bar that pulses with the input. Empty
           bars across the board mean no audio is reaching the
           analyzer — check Source and Sensitivity.

Both readouts refresh every 100 ms while the dialog is open.

=== Slideshow Start / Stop Button ===

The bottom-left button starts or stops the slideshow without
closing the dialog. Label and color reflect the current state:

  ▶ Start Slideshow   (green) — slideshow is stopped
  ■ Stop Slideshow    (red)   — slideshow is running

Useful for quickly testing different settings: tweak EQ, press
Stop, press Start to apply the audio engine state, watch the
result, repeat. Settings are applied on OK, but the slideshow
toggle works immediately.

=== OK / Cancel ===

  OK       Applies all changes (source, sensitivity, beat counts,
           synth BPM, synth flags, EQ weights, fade fraction) and
           saves them to disk. The audio engine is reconfigured
           live if it's running.
  Cancel   Discards changes. Engine state and on-disk settings are
           untouched.

The dialog is modeless, so closing it (or pressing Esc) does NOT
stop the slideshow.

=== Workflow Examples ===

  ""Visualize Spotify""
    1. Start Spotify and begin playback.
    2. Floating Menu  →  Audio  →  tick ""Audio-Reactive"".
    3. Open Audio Settings, leave Source = System Loopback.
       Wait for the BPM readout to populate.
    4. Click the green ""▶ Start Slideshow"" button at the bottom
       of the dialog (or close the dialog and use the toolbar
       Slideshow button).
    5. Beats per Theme 8, Beats per Region 32, Fade 0.75 ×
       beat gives a musical default.

  ""Drive from an MP3 file""
    1. Open Audio Settings.
    2. Source = Audio File. Browse… → pick the file.
    3. Click OK. File begins playing through speakers.
    4. Start the slideshow.
    5. Click Cancel does NOT stop file playback — toggle
       Audio-Reactive off in the Floating Menu to halt the
       engine cleanly.

  ""Closed-loop demo (no external audio)""
    1. Source = Fractal Synth, Synth BPM = 120.
    2. Tick BOTH ""Route synth through analyzer"" and ""Play synth
       audio"".
    3. OK. The synth plays and drives the slideshow itself.

  ""Kick-drum-only trigger""
    1. EQ: Bass 200 %, LowMid 100 %, others 0 %.
    2. Sensitivity 60–70 %.
    3. Beats per Theme 4 (one bar).
    4. Watch theme changes land on every kick.

=== Troubleshooting ===

  ""BPM stays at —""
    • Source not producing audio yet (start the player).
    • System loopback: nothing playing on the default output.
    • Microphone: muted or missing permission.
    • Wait ~10 seconds; the detector needs a window of beats.

  ""Level bars are empty""
    • No audio reaching the analyzer. Check the Source combo.
    • For System Loopback, confirm Windows is routing audio to
      the same default device the engine opened.
    • Raise Sensitivity if the bars flicker but never fill.

  ""Slideshow advances too often / too rarely""
    • Adjust Beats per Theme / Beats per Region first.
    • If those feel right but the detector is firing wrong,
      tune Sensitivity and the EQ band weights.
    • The Fade × beat slider does NOT affect when transitions
      fire — only how long they take once started.

  ""Cross-fade looks instant at high BPM""
    • 200 BPM × 0.10 × beat = 30 ms — below the 120 ms floor.
      Floor kicks in; raise Fade × beat for a longer fade.

  ""File playback ended silently""
    • Audio source ended is shown in the status bar.
    • Engine remains configured. Pick a new file and press OK,
      or switch to System Loopback.

  ""I want the engine to keep running with the slideshow stopped""
    • Default policy: stop with slideshow. Open Audio Settings,
      then leave it open — the engine restarts automatically
      when the slideshow runs again, and the meters keep
      reflecting live audio for as long as it's playing through
      a non-slideshow path.

=== Persistence ===

All values commit to disk via OK. Stored as JSON:
  %APPDATA%\FracturingFog\audio-settings.json

Fields stored:
  Enabled, Source, FilePath, Sensitivity, BeatsPerTheme,
  BeatsPerRegion, RouteSynthThroughAnalyzer, PlaySynthOutput,
  SynthBpm, BandWeights[5], FadeBeatFraction.

Missing fields (e.g. older config without FadeBeatFraction) load
with defaults — no migration required.

=== Implementation Notes ===

  • Onset detector: spectral flux per band (5 bands), weighted
    sum, adaptive threshold. Reports a beat when the smoothed
    flux exceeds threshold + sensitivity-scaled margin.
  • BPM estimator: autocorrelation over recent inter-onset
    intervals; refines over time.
  • Beat handler runs on the audio capture thread; it flips
    volatile skip flags consumed by the slideshow loop. No UI
    marshalling on the audio thread.
  • Theme/region advancement happens at the slideshow's normal
    transition points — the beat handler just shortcuts the
    timer. This keeps the cross-fade rendering deterministic
    even if a beat fires mid-frame.
  • Fade × beat is read once at the start of each slideshow
    transition; changing it mid-run takes effect on the next
    transition, not the in-flight one.
";
            return page;
        }

        /// <summary>
        /// Selects the Color Theme Editor tab. Called by MainForm when the
        /// editor's "Help" button is clicked so the user lands directly on
        /// the editor documentation instead of having to find the tab.
        /// </summary>
        public void ShowEditorTab()
        {
            foreach (TabPage page in _tabs.TabPages)
            {
                if ((page.Tag as string) == "editor-help")
                {
                    _tabs.SelectedTab = page;
                    return;
                }
            }
        }

        private TabPage BuildEditorTab()
        {
            var page = MakePage("Theme Editor");
            page.Tag = "editor-help";
            var rtb = MakeRichText(page);

            rtb.Text =
@"=== Color Theme Editor ===

A floating window that lets you create new color themes from scratch
or edit existing ones, with live preview into the main render window.

Open from the Floating Menu: Color Themes → ""Edit Theme…"" button.
The editor uses the currently-selected theme as its starting point.

=== Layout ===

  Left column   Target, Identity, Kind, Color Stops, Cycle,
                In-Set, Post-FX Defaults, action buttons
  Right column  3D Lighting (Phong/PBR), Phong3D Extras,
                Pbr3D Extras  (visible only when relevant Kind chosen)

=== Target ===

  Region   Picks a saved region. Jumping a region inside the editor
           also moves the main view and snaps both the toolbar and
           floating-menu region combos.

  Theme    Picks the starting theme. Selecting a theme:
            • Populates every field in the editor.
            • Pushes the theme as the live preview map.
            • Snaps the toolbar + floating-menu theme combos.
            • Editor combos do NOT track changes made outside the
              editor — once it is open, the editor owns the selection.

  Algorithmic themes (HSV, Bernstein, Painted, etc.) have no exported
  parameter surface. The editor disables Apply / Save / Export and
  prompts you to click ""New Blank"" to start from a blank Gradient.

=== Identity ===

  Name         Display name in combos. Required; must be unique among
               user themes. Saving with an existing name prompts to
               replace.
  Category     Free-form group label. ""User"" is the default; built-ins
               use ""3D Relief"", ""Cycling"", ""Domain Coloring"", etc.
  Desc         One-line description shown as tooltip in the combo.
  Max zoom     Optional cap on zoom where this theme stays useful for
               automated viewing (slideshow / video). Tick ""Limited""
               to enable; leave unticked for no cap. Themes whose
               signal degrades at deep zoom (orbit-aware, distance-
               estimation) should carry a finite value so slideshow
               and video skip them past that depth.

=== Kind (theme type) ===

Four radio buttons select the underlying rendering model. Switching
Kind reveals or hides the type-specific sections on the right column.

  Gradient   Linear gradient stretched once across the iteration range.
             Simple, predictable, no time-varying parameters.
  Cycling    Gradient that wraps multiple times based on CycleSpeed.
             Better for deep zoom because color signal never goes
             flat-black.
  Phong3D    Cycling gradient with Blinn-Phong directional lighting.
             Uses the fractal's normal map for relief shading.
  Pbr3D      Cycling gradient with Cook-Torrance PBR lighting.
             Adds metalness, roughness, energy-conserving highlights.

=== Color Stops (all kinds) ===

  Position   Normalized [0, 1]. 0 = start of gradient (low iterations),
             1 = end (high / escape). Stops are sorted by Position on
             save and on render.
  Swatch     Click to open a color picker (wheel + RGB entry).
  R / G / B  Numeric channel entry, 0–255. Swatch updates as you type.
  X          Removes that stop.
  + Add Stop Appends a new stop at Position 1, white.

  Minimum 2 stops required to render. The renderer linearly
  interpolates between consecutive stops.

=== Cycle (Cycling / Phong3D / Pbr3D) ===

  Speed   How fast the gradient repeats across the iteration range.
          Default 0.02 = roughly one full cycle every 50 smooth-units.
          Higher → more rapid color cycling (good for deep zoom).
          Lower → broader bands.
  Hidden for the Gradient kind, which does not cycle.

=== 3D Lighting — Shared (Phong3D + Pbr3D) ===

  Steepness   Z-scale factor applied to the surface normal.
              Smaller = deeper carving (dramatic relief); larger =
              flatter surface. Typical range 0.8 – 2.5. Default 1.6.
  Ambient     Base illumination added to every pixel before lighting.
              Prevents shadow areas from going pure black. Typical
              0.05 – 0.20. Default 0.12.

  Key Light and Fill Light  Two independent directional lights:
    Dir X / Y / Z   Light direction vector. Normalised on use; you
                    can enter rough values like (-0.5, 0.7, 0.6).
                    +X = right, +Y = up (complex plane), +Z = toward
                    viewer.
    Diffuse RGB     Lambertian color contribution (matte). Swatch
                    + numeric entry. Multiplied by max(0, N·L).
    Specular RGB    Highlight color (mirror-like). Multiplied by
                    max(0, N·H)^Shininess.
    Shininess       Specular exponent. Higher = tighter, sharper
                    highlight (mirror). Lower = broader, softer
                    (matte). Typical 16 (soft) – 128 (sharp).

  Typical setup: Key light is bright/white and aimed front-upper-left;
  Fill light is dimmer/cool and aimed from opposite side to lift the
  shadows.

=== Phong3D Extras ===

  Key spec    Multiplier on the Key light's specular contribution.
              0 = no highlights from key. Default 0.85.
  Fill spec   Multiplier on the Fill light's specular. Usually small
              (0.10 – 0.30) so fill stays matte. Default 0.25.
  Fill diff   Multiplier on the Fill light's diffuse. Controls how
              much color the fill light adds. Default 0.35.

  Use these to balance the two lights without changing the per-light
  color values. Cranking fill-diff lifts shadow color; cranking
  key-spec pushes sharper hotspots.

=== Pbr3D Extras ===

  Lighting    Choice of PBR lighting profile:
              • PBRRealistic — physically accurate, subtle. Closer to
                offline renderer output.
              • PBRBright    — HDR-boosted, glow-friendly. Better for
                deep-zoom interior shots where realism matters less
                than visible color.
  Glow exp    Exponent in the glow-boost function:
                glow(t) = GlowScale * pow(t, GlowExp)
              Default 8. Higher = glow concentrates near escape (t=1)
              and dies off quickly at low t.
  Glow scl    Linear scale on the same function. Set to 0 to disable
              the glow boost entirely. Useful for highlighting the
              very edge of escape without lifting interior detail.

  Material bands  Piecewise metal / roughness function. Each row:
    UpperT      The band applies when t < UpperT. Bands are evaluated
                in list order; the first band whose threshold exceeds
                t wins. The final band's UpperT acts as a catch-all
                (set it to 1.0 or higher).
    Metal       0 = dielectric (plastic, stone). 1 = full metal.
                Values in between blend the two BRDFs.
    Roughness   0 = mirror-smooth. 1 = fully diffuse. 0.7 is a
                useful default for stone / rough metal.
  + Add Band  Appends a band; remember to update its UpperT so it
              actually catches a range of t.

  A common pattern is one matte interior band followed by one
  glossy band near escape, e.g.:
    UpperT=0.85, Metal=0.0, Roughness=0.80   ← bulk of the gradient
    UpperT=1.00, Metal=0.6, Roughness=0.20   ← shiny escape rim

=== In-Set (Interior) ===

  Override   When ticked, paints in-set (unescaped) pixels with the
             chosen color instead of opaque black. Useful for themes
             where black hides too much against a dark gradient.
  Swatch     Click to color-pick the in-set color.
  R / G / B  Numeric entry; swatch tracks live.
  Unticked   Default opaque black (0xFF000000) — historical behavior.

=== Post-FX Defaults ===

The editor can record default values for the three post-processing
sliders on the Floating Menu (Brightness / Contrast / Adaptive). On
theme selection elsewhere, those sliders snap to the theme's defaults
unless the slider is locked.

  Brightness — set by theme   −100 … +100. Tick to persist.
  Contrast   — set by theme   −100 … +100. Tick to persist.
  Adaptive   — set by theme   0 … 100. Tick to persist.

  When the checkbox is unticked, the JSON field is omitted (null) and
  the host slider keeps whatever the user left it on. When ticked,
  the value is stored in the theme JSON and applied on theme select.

  The Floating Menu sliders also carry per-slider Lock checkboxes —
  ticking a lock pins the slider position across theme switches so
  a theme's default cannot override it.

=== Live Preview & Actions ===

  Live preview   When ticked (default), changes push to the main
                 render window via a 150 ms debounce. Drag a slider
                 freely; the calculator re-runs once you settle.
  Apply          Force a push to the main render immediately,
                 regardless of the live-preview state.
  New Blank      Discard current edits and start from a fresh
                 Gradient (two stops: black → white). Useful when
                 the source theme is algorithmic and not editable.
  Revert         Reload from the last source theme name. Discards
                 unsaved edits.
  Save to Library
                 Validates Name / ≥ 2 stops, then adds or replaces a
                 user theme in %APPDATA%\FracturingFog\colorthemes.json.
                 Triggers a combo rebuild on the toolbar and floating
                 menu so the new name is immediately selectable.
  Export JSON…   Writes a single-theme JSON array to a location of
                 your choice. The output round-trips through the
                 existing Import flow in the Floating Menu.

=== Region Sync ===

  Selecting a Region in the editor jumps the main view AND updates
  the toolbar and floating-menu region combos. The reverse is NOT
  true: while the editor is open, its own combos are not modified
  by selections made elsewhere — this keeps the editor predictable
  while you are editing.

=== File Format ===

  Library file:  %APPDATA%\FracturingFog\colorthemes.json
                 (auto-saved on every Save to Library / library edit)
  Source seed:   <install>\Resources\ColorThemes\colorthemes.json
                 (merged into the user file on first launch only)

  Each entry is a single ColorThemeData object. Fields are emitted
  with WhenWritingNull semantics — anything left null in the editor
  is omitted from the JSON entirely. Hand-edits to the JSON are
  picked up on the next launch, or via the Reload button.

=== Quick Workflows ===

  ""Tweak an existing 3D theme""
    1. Open editor (Color Themes → Edit Theme…)
    2. Theme combo → pick the source theme
    3. Adjust Steepness / Ambient / Light directions
    4. Save to Library (uses the same name = replace)

  ""Build a fresh gradient by hand""
    1. New Blank
    2. Color Stops → set positions + colors
    3. Pick Cycling if you want repetition at deep zoom
    4. Adjust CycleSpeed
    5. Name + Save to Library

  ""Share a theme""
    1. Save to Library (or simply edit the running theme)
    2. Export JSON… to a file
    3. Send the file. Recipient uses Floating Menu Import to ingest.

=== Per-Kind Parameter Index ===

  Every kind uses:  Name, Category, Description, Max Zoom,
                    Color Stops, In-Set Override, Post-FX Defaults.

  Gradient
    • Color Stops only (no Cycle, no 3D Lighting).
    • Renders as one linear gradient across the iteration range.

  Cycling
    • Color Stops
    • Cycle Speed  — repetition rate of the gradient.

  Phong3D    (Cycling + Blinn-Phong directional lighting)
    • Color Stops
    • Cycle Speed
    • Steepness, Ambient
    • Key Light    (Dir, Diffuse, Specular, Shininess)
    • Fill Light   (Dir, Diffuse, Specular, Shininess)
    • Key spec, Fill spec, Fill diff  — per-light multipliers.

  Pbr3D      (Cycling + Cook-Torrance PBR lighting)
    • Color Stops
    • Cycle Speed
    • Steepness, Ambient
    • Key Light, Fill Light  (Shininess unused by PBR brdf —
                              roughness drives highlight sharpness)
    • Lighting mode  (PBRRealistic | PBRBright)
    • Glow exp, Glow scl     — additive emission near escape.
    • Material bands         — piecewise (metal, roughness) over t.

  Fields not in the active kind are hidden in the editor and stored
  as null in the saved JSON (no waste, no confusion on round-trip).
";
            return page;
        }

        private TabPage BuildMathTab()
        {
            var page = MakePage("Mathematics");

            var sub = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Normal,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(78, 24),
                Multiline = true,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            sub.DrawItem += OnDrawTabItem;

            sub.TabPages.Add(BuildMathSubTab("Overview",       MathOverviewText()));
            sub.TabPages.Add(BuildMathSubTab("Mandelbrot",     MathMandelbrotText()));
            sub.TabPages.Add(BuildMathSubTab("Julia",          MathJuliaText()));
            sub.TabPages.Add(BuildMathSubTab("Burning Ship",   MathBurningShipText()));
            sub.TabPages.Add(BuildMathSubTab("Tricorn",        MathTricornText()));
            sub.TabPages.Add(BuildMathSubTab("Multibrot",      MathMultibrotText()));
            sub.TabPages.Add(BuildMathSubTab("Phoenix",        MathPhoenixText()));
            sub.TabPages.Add(BuildMathSubTab("Newton",         MathNewtonText()));
            sub.TabPages.Add(BuildMathSubTab("Nova",           MathNovaText()));
            sub.TabPages.Add(BuildMathSubTab("Buddhabrot",     MathBuddhabrotText()));
            sub.TabPages.Add(BuildMathSubTab("IFS",            MathIFSText()));
            sub.TabPages.Add(BuildMathSubTab("L-System",       MathLSystemText()));
            sub.TabPages.Add(BuildMathSubTab("Attractor",      MathAttractorText()));
            sub.TabPages.Add(BuildMathSubTab("Mandelbulb",     MathMandelbulbText()));
            sub.TabPages.Add(BuildMathSubTab("User Equation",  MathUserEquationText()));
            sub.TabPages.Add(BuildMathSubTab("User Bulb 3D",   MathUserBulbText()));
            sub.TabPages.Add(BuildMathSubTab("Sandbox",        MathSandboxText()));

            page.Controls.Add(sub);
            return page;
        }

        private TabPage BuildMathSubTab(string label, string body)
        {
            var page = MakePage(label);
            var rtb = MakeRichText(page);
            rtb.Text = body;
            return page;
        }

        #region Math sub-tab content

        private static string MathOverviewText() =>
@"=== Fractals in Fracturing Fog ===

The Mathematics tab groups every fractal family the renderer can
produce.  Each subtab covers one family:

  • Mandelbrot    Escape-time z² + c on the parameter plane
  • Julia         Escape-time z² + c with c fixed (dynamical plane)
  • Burning Ship  z → (|Re z| + i|Im z|)² + c
  • Tricorn       z → conj(z)² + c
  • Multibrot     z → z^d + c, integer d ≥ 2
  • Phoenix       z → z² + c + p·z_(n−1)  (two-step memory)
  • Newton        Root-finding basins of f(z) = z^d − 1
  • Nova          Newton-style with c offset (relaxation map)
  • Buddhabrot    Density plot of escaping Mandelbrot orbits
  • IFS           Affine contractions via chaos game
  • L-System      String-rewriting + turtle graphics
  • Attractor     Strange attractors (Clifford, De Jong, Lorenz)
  • Mandelbulb    3D triplex-power distance-estimation render
  • User Equation Roslyn-compiled per-pixel C# step function

=== Categories ===

Escape-time          Mandelbrot, Julia, Burning Ship, Tricorn,
                     Multibrot, Phoenix, User Equation
Root-finding         Newton, Nova
Density / point      Buddhabrot, IFS, Attractor
Geometric rewrite    L-System
3D distance estimate Mandelbulb

=== Common Render Pipeline ===

For escape-time families the renderer:

  1. Maps each pixel to a complex c via
       cx = CenterX + (x − W/2)·scale
       cy = CenterY + (y − H/2)·scale
       scale = (3.5 / max(W,H)) / Zoom
  2. Iterates the family's recurrence with z₀ = 0 (or z₀ = c).
  3. Stops at |z|² ≥ bailout² or iter ≥ MaxIterations.
  4. Smooths the iteration count:
       smooth = n + 1 − log₂(log₂(|z|))
  5. Feeds smooth (and derivative-derived distance / normal data)
     into the active IColorMap to produce the pixel ARGB.

For non-escape families the rendering pipeline differs — see the
individual subtabs.

=== Why Each Family Looks Different ===

Tiny algebraic tweaks to a single recurrence produce wildly
different geometry.  Changing z² → conj(z)² gives Tricorn;
absolute-value the components and you get Burning Ship; raise the
power d to get Multibrot.  All share the same skeleton; the
difference is one or two lines of arithmetic inside Step().
";

        private static string MathMandelbrotText() =>
@"=== The Mandelbrot Set ===

The Mandelbrot set M is the set of complex numbers c for which the
quadratic iteration

        z₀ = 0
        zₙ₊₁ = zₙ² + c

remains bounded (|zₙ| ≤ 2 for all n).  Points outside M escape to
infinity at a finite rate; that escape rate, colored by a palette,
produces the familiar fractal imagery.

=== Historical Timeline ===

  1905   Pierre Fatou & Gaston Julia study iteration of rational
         maps on the complex plane.  Julia describes connected /
         disconnected behaviour but cannot visualize it.
  1978   Robert W. Brooks and Peter Matelski publish the first
         crude computer-generated picture of the set in their
         paper on Kleinian groups.
  1980   Benoit B. Mandelbrot, working at IBM's Yorktown Heights
         lab, produces high-resolution renders that reveal the
         set's astonishing self-similar structure.  He coins the
         name in 1982.
  1985   Adrien Douady & John H. Hubbard prove M is connected and
         introduce the parameter ray theory.  They name the set
         in honor of Mandelbrot.
  1991   Mitsuhiro Shishikura proves the boundary of M has
         Hausdorff dimension 2.
  2000s  Perturbation methods (K. I. Martin) make zooms past 1e50
         tractable on consumer hardware.

=== Mathematical Properties ===

  • Connected: every point in M is path-connected (Douady-Hubbard).
  • Boundary has fractal (Hausdorff) dimension 2.
  • Area ≈ 1.50659177 (numerical; closed form unknown).
  • Locally — but not globally — self-similar.  Tiny copies of M
    (""mini-brots"") appear at every scale, embedded in spirals,
    filaments, and dendrites.
  • The Mandelbrot set is the bifurcation locus of the family
    fₐ(z) = z² + c — it indexes all quadratic Julia sets.
  • Cardioid: the main body is the image of the unit disk under
    w → w/2 − w²/4.  Its cusp lies at c = 1/4.
  • Period-2 bulb: the circle of radius 1/4 centered at c = −1.
  • Conjecture (MLC): M is locally connected — open since 1985,
    one of the deepest open problems in complex dynamics.

=== Escape-Time Algorithm ===

  function mandelbrot(c, maxIter):
      z = 0
      for n in 0 … maxIter:
          if |z| > 2: return n            # escaped
          z = z * z + c
      return maxIter                      # inside (treated as)

The bailout |z| > 2 follows from the fact that once |z| exceeds 2
the orbit must diverge.  Smoothing tricks (continuous escape time,
distance estimation, orbit traps) extract sub-pixel detail.

=== Why Deep Zoom is Hard ===

At zoom 10ⁿ the pixel spacing is ~4·10⁻ⁿ.  IEEE-754 double has
~15 decimal digits, so beyond zoom 10¹³ the pixel grid stops
resolving distinct complex numbers — banding and ""solid-color""
artifacts appear.  Solutions:

  • Extended precision (DD, QD, MPFR).  Slow per-pixel but exact.
  • Perturbation theory: iterate ONE reference orbit in high
    precision, then iterate per-pixel deltas in double.  Series
    approximation + bilinear approximation (BLA) skip thousands
    of inner iterations at a time.

Fracturing Fog uses double / DD / QD auto-promotion combined with
perturbation + SA + BLA for zooms past 1e45.

=== C# Equation (User Equation tab) ===

  // Classic Mandelbrot — z₀ = 0, zₙ₊₁ = zₙ² + c
  return z*z + c;
";

        private static string MathJuliaText() =>
@"=== The Julia Set ===

Fix c ∈ ℂ.  The filled Julia set K(c) is the set of z₀ ∈ ℂ whose
orbits under

        zₙ₊₁ = zₙ² + c

remain bounded.  Unlike the Mandelbrot set (parameter plane), the
Julia set lives in the dynamical plane: every point on screen is
treated as a candidate z₀ with the SAME c.

=== Connection to the Mandelbrot Set ===

Fatou: K(c) is connected ⇔ 0 ∈ K(c) ⇔ c ∈ M.  Pick c inside the
Mandelbrot set: you get a connected, often dendritic, Julia set.
Pick c outside: you get a totally disconnected Cantor dust
(""Fatou dust"").  Mandelbrot's set is therefore a topological
catalogue of all quadratic Julia sets.

=== Notable c Values ===

  c =  0           Unit disk (the trivial Julia set)
  c = −1           San Marco fractal (period-2 fixed cycle)
  c = −0.7 + 0.27i Dragon-like dendrite (Fracturing Fog default)
  c = −0.835−0.232i Spiraling dendrite
  c =  0.285+0.01i Cauliflower / mini-Mandelbrot lookalike
  c = −0.4+0.6i    Douady's rabbit
  c = i            Dendrite of Misiurewicz type

=== Escape-Time Algorithm ===

  function julia(z, c, maxIter):
      for n in 0 … maxIter:
          if |z| > 2: return n
          z = z * z + c
      return maxIter

Only the initial condition changes: z₀ = pixel coordinate, c =
the dialog-supplied constant.

=== Parameters (FractalParameters) ===

  JuliaC : Complex   Constant c.  Default (−0.7, 0.27015).

=== Symmetry ===

J(c) is symmetric under z → −z (rotation by π).  Many Julia sets
exhibit additional discrete rotational symmetries when c lies on
the boundary of a bulb.

=== C# Equation ===

  // Julia: c fixed, z varies, z₀ = pixel.  Engine seeds z = 0, so
  // on iteration 0 we copy the pixel coordinate into z, then run
  // the standard z² + jc recurrence.
  if (n == 0) z = c;
  return z*z + new Complex(-0.7, 0.27015);
";

        private static string MathBurningShipText() =>
@"=== The Burning Ship ===

Discovered by Michael Michelitsch & Otto E. Rössler (1992).  Same
escape-time framework as Mandelbrot, but each iteration takes the
ABSOLUTE VALUE of both components before squaring:

        zₙ₊₁ = ( |Re(zₙ)| + i·|Im(zₙ)| )² + c

Expanding:

        zr' = zr² − zi² + cx          (after |zr|, |zi|)
        zi' = 2·|zr|·|zi| + cy

The map is NOT analytic — the derivative is discontinuous along
the real and imaginary axes.  This sharp cusp behaviour creates
the characteristic ""flames"" and ""mast"" structures.

=== Why ""Burning Ship""? ===

Rendered conventionally with the imaginary axis inverted, the main
body resembles a three-masted galleon engulfed in flames.

=== Set Location ===

Re ∈ [−2.5, 1.5],  Im ∈ [−2, 1.5] (with axis convention above).
Notable features:

  • Main hull around c ≈ (−1.75, 0)
  • Antenna spike along the negative real axis past −1.94
  • Mini-ship at c ≈ (−1.7568, −0.0381)

=== C# Equation ===

  // Burning Ship: |Re z| + i|Im z|, then square, then + c
  var w = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary));
  return w*w + c;
";

        private static string MathTricornText() =>
@"=== The Tricorn / Mandelbar ===

Studied by Crowe, Hasson, Rippon & Strain-Clark (1989).  Replace
z² with its complex CONJUGATE squared:

        zₙ₊₁ = conj(zₙ)² + c

In component form (zr + i·zi):

        zr' =  zr² − zi² + cx
        zi' = −2·zr·zi + cy        ← sign flip vs Mandelbrot

The conjugation makes the map ANTI-holomorphic; its even-order
iterate conj(conj(z)²)² = (z̄²)̄² = (z²)² is holomorphic, so
period-2 dynamics behave like a Multibrot of degree 4.

=== Geometry ===

Threefold symmetric: the set looks like a three-cornered
""Mandelbar"" with three large lobes meeting at the origin.  Mini
Mandelbrot-like islands appear in the antenna.  Boundary has
fractal dimension 2 (Inou 2000).

=== Connection to Bicomplex Dynamics ===

The Tricorn is the connectedness locus of the family
fc(z) = conj(z)² + c, and is the parameter plane of the
""anti-quadratic"" maps.  Period-2 bulbs are PARABOLIC (semi-stable),
giving sharp cusp boundaries instead of smooth bulbs.

=== C# Equation ===

  // Tricorn: conjugate before squaring
  var zb = Complex.Conjugate(z);
  return zb*zb + c;
";

        private static string MathMultibrotText() =>
@"=== Multibrot Sets ===

Generalize Mandelbrot by raising z to an integer power d ≥ 2:

        zₙ₊₁ = zₙ^d + c                d ∈ ℤ, d ≥ 2

  d = 2   Standard Mandelbrot
  d = 3   ""Tribrot"" — twofold symmetric, two cardioids
  d = 4   Threefold symmetric, three cardioids
  d = n   (n−1)-fold rotational symmetry

The bulb structure rotates by 2π/(d−1) around the origin.  Each
multibrot has the same self-similarity properties as Mandelbrot
but with more arms.

=== Implementation Note ===

Repeated complex multiplication for d > 4 accumulates floating-
point error.  The kernel uses polar form:

        r     = |z|,  θ = arg(z)
        z^d   = r^d · (cos(d·θ) + i·sin(d·θ))

Derivative tracking (for distance estimation / normals) uses
d·z^(d−1) similarly converted through polar form.

=== Parameters ===

  MultibrotExponent : int   Power d.  Default 3.  Clamped to ≥ 2.

=== Fractional d ===

Real (non-integer) exponents are mathematically defined via
z^d = exp(d·log z), but produce branch-cut artifacts.  Fracturing
Fog uses integer d to avoid this.

=== C# Equation ===

  // Multibrot of integer power d.  d = 3 below.
  int d = 3;
  return Complex.Pow(z, d) + c;
";

        private static string MathPhoenixText() =>
@"=== The Phoenix Fractal ===

Introduced by Shigehiro Ushiki (1988).  Second-order recurrence:

        zₙ₊₁ = zₙ² + c + p · zₙ₋₁

Carries TWO-STEP memory.  The extra p·z_(n−1) term couples the
current iterate to its predecessor, allowing dynamics that pure
first-order maps cannot produce.

=== Properties ===

  • For p = 0 it collapses to the standard Mandelbrot (or Julia)
    family.
  • For p real and small (|p| < 0.1) the resulting set looks like
    a deformed Julia set with phoenix-feather plumes.
  • The set lies in the dynamical plane (z₀ = pixel) — c is the
    parameter, but in Fracturing Fog the pixel coordinate plays
    the role of c and the iterate z starts at 0, mirroring the
    Mandelbrot convention.
  • A famous orientation (p ≈ 0.5667, c ≈ 0) produces an iconic
    flame-bird outline — the namesake ""phoenix"".

=== Implementation ===

  zr', zi'   = zr² − zi² + cx + Re(p·prev),  2·zr·zi + cy + Im(p·prev)
  prev       ← (zr, zi)                     before the new value lands

State carried per pixel: (zr, zi, prevZr, prevZi).

=== Parameters ===

  PhoenixP : Complex   Coupling constant p.  Default (0.56667, 0).

=== C# Equation ===

  // User Equation can't carry prev-z between steps via the signature
  // (z, c, n) → z.  A simplified single-step approximation drops the
  // memory term; for true Phoenix select FractalType = Phoenix instead.
  // Approximation:
  var p = new Complex(0.56667, 0.0);
  return z*z + c + p * z;     // closes the loop in one step
";

        private static string MathNewtonText() =>
@"=== Newton Fractal ===

Newton's root-finding iteration applied as a dynamical system on ℂ.
For polynomial f(z) the iteration

        zₙ₊₁ = zₙ − R · f(zₙ) / f'(zₙ)

converges quadratically (for R = 1) to a root once near enough.
The Newton fractal colors each pixel by WHICH root the iteration
converged to, optionally shaded by speed of convergence.

Default polynomial:  f(z) = z^d − 1     (roots = d-th roots of 1)

        f'(z) = d · z^(d−1)
        z   ← z − R · (z^d − 1) / (d · z^(d−1))

=== Geometry ===

  • d basins, one per root of unity, each meeting EVERY OTHER
    basin at every boundary point.  This is Wada's lakes property:
    no boundary pixel borders fewer than d basins.
  • Boundary has fractal dimension > 1.
  • Hausdorff dimension increases with d.
  • Relaxation parameter R ≠ 1 (""generalized Newton"") slows or
    accelerates convergence; R = 2 produces the ""Halley method""-
    flavoured shape.

=== Historical Notes ===

  • Cayley (1879) studied the d = 2 case.  Cayley conjectured —
    incorrectly — that the d = 3 case would behave just as
    cleanly, but boundary basins are dense.
  • The intricate boundary was first drawn by Peitgen, Saupe &
    Jürgens (1980s).

=== Parameters ===

  NewtonExponent   : int      Polynomial degree d.  Default 3.
                              Clamped to [2, 8].
  NewtonRelaxation : double   Relaxation factor R.  Default 1.0.
  NewtonPolyCoeffs : Complex[]?  Optional custom polynomial
                                 coefficients (currently unused
                                 by the default kernel; reserved
                                 for future custom-polynomial
                                 path).

=== C# Equation (approximation) ===

  // Newton step for f(z) = z^3 − 1.  Treats ""c"" as starting z
  // since User Equation seeds z = 0 — uses iteration count n to
  // pick the initial point.
  if (n == 0) z = c;
  var z2 = z*z;
  var f  = z*z2 - Complex.One;
  var fp = 3 * z2;
  return z - f / fp;
";

        private static string MathNovaText() =>
@"=== Nova Fractal ===

Variant of the Newton fractal — Paul Derbyshire (1990s).  Adds a
constant offset c to the Newton iteration so the parameter c can
be varied per pixel (parameter-plane Newton):

        zₙ₊₁ = zₙ − R · f(zₙ) / f'(zₙ) + c

For f(z) = z^d − 1 this gives a Mandelbrot-flavoured Newton:
roots, basins, and a Mandelbrot-style boundary all in one image.
Setting c = 0 collapses Nova back to the pure Newton fractal.

=== Properties ===

  • Combines basin colouring (root convergence) with escape-time
    colouring (when iteration fails to converge).
  • Tends to produce intricate ""embedded Mandelbrot"" copies on
    the basin boundaries.
  • Heavily dependent on the start point convention.  Two common
    choices: z₀ = 1 (yields the canonical Nova) and z₀ = c
    (parameter-plane variant).

=== Implementation Note ===

Fracturing Fog currently routes Nova through the same calculator
as Newton.  Selecting Nova in the UI uses the Newton kernel with
the user's exponent and relaxation; full Nova c-offset support is
on the roadmap.

=== C# Equation ===

  // Nova for f(z) = z^3 − 1, with c parameter offset.
  if (n == 0) z = Complex.One;
  var z2 = z*z;
  var f  = z*z2 - Complex.One;
  var fp = 3 * z2;
  return z - f / fp + c;
";

        private static string MathBuddhabrotText() =>
@"=== The Buddhabrot ===

Melinda Green (1993).  A density plot of the MANDELBROT orbits —
not the set itself.  For each randomly sampled c whose orbit
escapes within a chosen iteration band, the entire orbit
(z₀, z₁, z₂, …) is replayed and each visited pixel gets a +1
increment.  The accumulated density buffer is then tone-mapped
through the active color map.

Mathematical definition:

        ρ(z) = Σ_{c escaping}  Σ_{n ≥ 0}  𝟙[ zₙ(c) ≈ z ]

=== Nebulabrot ===

Three iteration bands feed R, G, B channels separately:
short orbits → red, mid orbits → green, long orbits → blue.
The result has a luminous, nebula-like appearance — hence
""Nebulabrot"".

=== Rotation ===

Conventionally displayed rotated 90° so the cardioid is vertical
and the structure resembles a seated Buddha figure (Daniel Green
2003, the namesake).

=== Parameters ===

  BuddhaSamples   : int   Number of c samples.  Default 500 000.
  BuddhaIterLow   : int   Low band cutoff iters.  Default 500.
  BuddhaIterMid   : int   Mid band cutoff iters.  Default 5 000.
  BuddhaIterHigh  : int   High band cutoff iters.  Default 50 000.

More samples → smoother density and brighter output.  Render time
scales linearly with samples and roughly linearly with band size.

=== Stochastic — Not Deterministic ===

Two runs at the same view produce slightly different images
because samples are random.  Higher sample counts converge toward
the underlying density distribution.

=== C# Equation ===

Buddhabrot is a HISTOGRAM render, not an escape-time recurrence —
it can't be expressed in the User Equation kernel.  Choose
FractalType = BuddhaBrot for the real thing.
";

        private static string MathIFSText() =>
@"=== Iterated Function Systems (IFS) ===

Hutchinson (1981), popularized by Michael Barnsley.  A finite set
of CONTRACTIVE affine maps

        T_i(x, y) = ( a_i·x + b_i·y + e_i ,
                      c_i·x + d_i·y + f_i )

has a unique compact attractor A satisfying

        A = ⋃ T_i(A)

The attractor is rendered via the CHAOS GAME (Barnsley): pick a
random point, apply a randomly chosen map (weighted by w_i), and
plot the result.  After N iterations the plotted points fill the
attractor with arbitrary precision.

=== Classic Examples ===

  Sierpinski Triangle   3 half-scale maps to triangle corners
  Sierpinski Carpet     8 third-scale maps (excludes center)
  Barnsley Fern         4 maps with a single tiny stem map
  Koch Snowflake        4 third-scale maps, one rotated
  Dragon Curve          2 half-scale rotated maps
  Tree                  2 + branch maps

=== Self-Similarity Dimension ===

For N maps each of contraction ratio r the similarity dimension
is

        d = log N / log (1/r)

Sierpinski triangle (N=3, r=1/2):  log 3 / log 2 ≈ 1.585
Koch curve         (N=4, r=1/3):  log 4 / log 3 ≈ 1.262

=== Parameters ===

  IFSPresetName : string   ""Sierpinski Triangle"", ""Barnsley Fern"",
                           ""Koch"", ""Dragon"", ""Tree"", etc.
  IFSIterations : int      Total chaos-game iterations.  Default
                           2 000 000.  More iterations → smoother
                           coverage of fine attractor branches.
  IFSMaps       : List<AffineMap>?
                           Optional override.  Each AffineMap is
                           (A, B, C, D, E, F, Weight).  Picks are
                           weighted by Weight; the first 50
                           settle iterations are discarded.

=== C# Equation ===

IFS uses the chaos game, not per-pixel iteration — it cannot be
expressed through the User Equation kernel.
";

        private static string MathLSystemText() =>
@"=== L-Systems ===

Aristid Lindenmayer (1968) — formal grammar for modelling plant
growth.  An L-system is

  axiom    A finite starting string.
  rules    Productions ""X → string""; rewrite every occurrence
           of X simultaneously each generation.
  alphabet Symbols.  Standard turtle interpretation:
              F   forward, draw line
              f   forward, no draw
              +   turn left by Δθ
              −   turn right by Δθ
              [   push state (position + heading)
              ]   pop state

After N generations the resulting string is walked as TURTLE
GRAPHICS, drawing the fractal curve.

=== Classic Curves ===

  Hilbert            Space-filling curve.  Axiom: A.
                     A → −BF+AFA+FB−, B → +AF−BFB−FA+
  Koch Snowflake     Axiom: F++F++F.   F → F−F++F−F
  Dragon Curve       Axiom: FX.        X → X+YF+,   Y → −FX−Y
  Sierpinski Curve   Many variants — F + rotation rules
  Plant              Axiom: X.   X → F[+X][−X]FX,   F → FF
  Penrose            Sub-tiling rules over multiple symbols

=== Dimension ===

For self-similar curves the dimension matches the IFS formula:
log N / log (1/r).  The Hilbert curve has dimension 2 — it FILLS
the plane in the limit.

=== Parameters ===

  LSystemPresetName : string   Preset name (""Hilbert"", ""Koch"",
                               ""Dragon"", ""Plant"", …).
  LSystemDepth      : int      Generation count N.  Default 5.
                               Clamped to [0, 12]; strings grow
                               exponentially with depth.

=== C# Equation ===

L-Systems use string-rewriting + turtle graphics — they cannot be
expressed through the User Equation per-pixel kernel.
";

        private static string MathAttractorText() =>
@"=== Strange Attractors ===

A 2D / 3D non-linear discrete dynamical system

        (xₙ₊₁, yₙ₊₁) = F(xₙ, yₙ; a, b, c, d)

whose orbits, after a brief transient, settle onto a set of
fractional (non-integer) Hausdorff dimension — a STRANGE
ATTRACTOR.  Rendered by iterating millions of points and
accumulating a per-pixel hit density.

=== Built-in Attractors ===

  Clifford    xₙ₊₁ = sin(a·yₙ) + c·cos(a·xₙ)
              yₙ₊₁ = sin(b·xₙ) + d·cos(b·yₙ)
              Defaults: (a, b, c, d) = (−1.4, 1.6, 1.0, 0.7)
              Smooth, butterfly-shaped.

  De Jong     xₙ₊₁ = sin(a·yₙ) − cos(b·xₙ)
              yₙ₊₁ = sin(c·xₙ) − cos(d·yₙ)
              Defaults: (1.4, −2.3, 2.4, −2.1).
              Wispy filaments with delicate symmetry.

  Hopalong    Barry Martin (1986).  Iterates an absolute-value
              and sign-of-x formula; produces concentric arcs.
              Defaults: (2.0, 1.0, 0.0, 0.0).

  Lorenz      3D continuous system (a = σ, b = ρ, c = β):
                ẋ = σ(y − x)
                ẏ = x(ρ − z) − y
                ż = xy − βz
              Projected to 2D via (x, z) for display.
              Defaults: σ=10, ρ=28, β=8/3.  The canonical
              ""butterfly"" attractor.

=== Mathematical Properties ===

  • Sensitive dependence on initial conditions: nearby orbits
    diverge exponentially (positive Lyapunov exponent).
  • Bounded yet unstable — orbits stay within a finite region.
  • Hausdorff dimension fractional and typically irrational.

=== Parameters ===

  AttractorPresetName  : string  ""Clifford"", ""De Jong"", ""Hopalong"",
                                 ""Lorenz"".
  AttractorIterations  : int     Points to plot.  Default 2 000 000.
  AttractorA/B/C/D     : double  Per-attractor parameters.

=== C# Equation ===

Strange attractors are point-density renders, not per-pixel
escape time — they cannot be expressed through User Equation.
";

        private static string MathMandelbulbText() =>
@"=== The Mandelbulb ===

Daniel White & Paul Nylander (2007–2009).  A 3D analogue of the
Mandelbrot set using the ""triplex"" power formula:

        for v = (x, y, z) in ℝ³:
          r     = |v|
          θ     = arctan2(√(x² + y²), z)        (polar angle)
          φ     = arctan2(y, x)                 (azimuth)

          v^p   = r^p · ( sin(p·θ)·cos(p·φ),
                          sin(p·θ)·sin(p·φ),
                          cos(p·θ) )

          vₙ₊₁ = vₙ^p + c                       (c = pixel ray)

Power p = 8 is the classic Mandelbulb; other powers give
different bulb shapes.

=== Rendering ===

Mandelbulb is rendered via DISTANCE ESTIMATION + RAYMARCHING:

  1. For each pixel cast a ray from the camera.
  2. At each ray position v, iterate the triplex formula and
     track an analytic running derivative dr.
  3. Distance estimate:
       DE(v) ≈ 0.5 · log(r) · r / dr
  4. Step the ray forward by DE(v) until DE < ε (hit) or
     ray exits bounding sphere (miss).
  5. Estimate the surface normal by central differences of DE
     and shade with a directional light.

=== Parameters ===

  BulbPower           : double  Triplex power p.  Default 8.
  BulbIterations      : int     DE inner iters.  Default 8.
  BulbMaxSteps        : int     Raymarch step cap.  Default 96.
  BulbEpsilon         : double  Hit threshold.  Default 0.0015.
  BulbCameraDistance  : double  Camera-origin distance.  Default 3.
  BulbCameraTheta/Phi : double  Camera spherical angles.
  BulbLightTheta/Phi  : double  Light spherical angles.

=== C# Equation ===

3D distance-estimation raymarchers can't be expressed as a 2D
per-pixel complex iteration.  Select FractalType = Mandelbulb to
render the true 3D set.
";

        private static string MathUserEquationText() =>
@"=== User Equations — Overview ===

The User Equation engine compiles a C# expression / statement
block at runtime (via Roslyn scripting) into a per-pixel step
function

        Complex Step(Complex z, Complex c, int n)

The renderer then iterates the standard escape-time loop:

        z = 0
        for n in 0 … MaxIterations:
            if |z|² ≥ 1024: break
            z = Step(z, c, n)

The smoothing function, bailout, and color pipeline mirror the
Mandelbrot path.

Open via:  Floating Menu → ""Equation…"" button
           Fractal Type → ""UserEquation""

The dialog is modeless and auto-compiles 500 ms after the last
keystroke.  Errors render in red below the editor.  Saved
equations live in:

    %APPDATA%\FracturingFog\userequations.json

=== Available Variables ===

  z   : System.Numerics.Complex
        The CURRENT iterate.  Starts at 0 on iteration 0.
        Access components:  z.Real, z.Imaginary, z.Magnitude,
                            z.Phase, Complex.Conjugate(z), …

  c   : System.Numerics.Complex
        The PIXEL coordinate in complex plane.  Constant per
        pixel.  c.Real = world X, c.Imaginary = world Y.

  n   : int
        Iteration index (0-based).  Useful for time-varying
        recurrences, e.g. mixing two maps.

=== Available APIs ===

  System.Numerics.Complex (full surface):
      operators       +  −  *  /
      static methods  Complex.Abs, Complex.Pow, Complex.Sin,
                      Complex.Cos, Complex.Tan, Complex.Exp,
                      Complex.Log, Complex.Sqrt, Complex.Conjugate,
                      Complex.Reciprocal, Complex.FromPolarCoordinates,
                      Complex.One, Complex.ImaginaryOne, Complex.Zero
      properties      .Real, .Imaginary, .Magnitude, .Phase

  System.Math (full surface, scalar):
      Abs, Sin, Cos, Tan, Atan2, Sinh, Cosh, Tanh, Exp, Log, Log2,
      Log10, Pow, Sqrt, Cbrt, Floor, Ceiling, Round, Min, Max,
      Math.PI, Math.E, Math.Tau

  Imports already in scope:
      using System;
      using System.Numerics;
      using static System.Math;     // (Sin/Cos etc. unqualified)

  References:
      System.Runtime, System.Numerics — no extra usings needed.

=== Syntax Rules ===

  • The body must RETURN a Complex.  Either:
        return z*z + c;          // single expression, no semicolon
                                  // before ""return"" needed
    or
        var w = z*z + c;
        return w + Complex.ImaginaryOne;
  • If you omit ""return"", the dialog wraps the body with one for
    a single expression — i.e. ""z*z + c"" is shorthand for
    ""return z*z + c;"".
  • Standard C# 12 syntax: ternary, switch expressions, pattern
    matching, local functions all work.
  • new Complex(re, im) and Complex.ImaginaryOne both available.

=== Seeding z₀ ===

The engine ALWAYS starts the orbit at z = 0.  Maps that have 0 as
a fixed point (z·sin(z), z·cos(z) − z, z² + 0·z, …) or that need a
pixel-dependent starting point (Julia, Heron, lambda) will produce
an all-in-set image with z₀ = 0.

The fix is to overwrite z on iteration 0 using the int parameter n:

        if (n == 0) z = c;                    // pixel as z₀ (Julia)
        if (n == 0) z = new Complex(0.5, 0);  // critical-point start

Because z is passed by VALUE the reassignment is local to the step
and does not interfere with the calculator's accumulator — the
returned value becomes the next iterate as normal.

=== Bailout and Smoothing ===

The runtime uses |z|² ≥ 1024 as the bailout (radius 32) — chosen
to give smooth coloring room across most maps.  Smoothed escape:

        smooth = n + 1 − log₂(log₂(max(|z|, 1+ε)))

If |z| stays bounded for MaxIterations, the pixel is treated as
in-set (theme's InSetColor).

=== Performance Notes ===

  • Scalar only — no SIMD.  Per-pixel delegate call overhead.
  • Interactive at 800 × 600 with ~256 iterations on a modern CPU.
  • Use Math.Abs over Complex.Abs when only the magnitude is
    needed (Complex.Abs allocates).
  • Avoid allocating new Complex instances inside the hot path —
    let the compiler fold them into temporaries.

=== Examples — Drop-in Snippets ===

Each block below is a complete equation body — paste it into the
editor and the renderer compiles + previews.

  --- Mandelbrot ---
  return z*z + c;

  --- Julia (constant baked) ---
  // Julia uses pixel as z₀.  Engine seeds z = 0, so on n = 0 we
  // load z from c, then iterate z² + jc normally.
  if (n == 0) z = c;
  return z*z + new Complex(-0.7, 0.27015);

  --- Burning Ship ---
  var w = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary));
  return w*w + c;

  --- Tricorn / Mandelbar ---
  var zb = Complex.Conjugate(z);
  return zb*zb + c;

  --- Multibrot (cubic) ---
  return Complex.Pow(z, 3) + c;

  --- Multibrot (degree d, runtime constant) ---
  int d = 5;
  return Complex.Pow(z, d) + c;

  --- Phoenix-flavoured single-step ---
  var p = new Complex(0.56667, 0.0);
  return z*z + c + p*z;

  --- Newton (z^3 − 1) ---
  if (n == 0) z = c;
  var z2 = z*z;
  var f  = z*z2 - Complex.One;
  var fp = 3 * z2;
  return z - f / fp;

  --- Nova (z^3 − 1, with c offset) ---
  if (n == 0) z = Complex.One;
  var z2 = z*z;
  var f  = z*z2 - Complex.One;
  var fp = 3 * z2;
  return z - f / fp + c;

  --- z² + c with sine perturbation ---
  return z*z + c + 0.1 * Complex.Sin(z);

  --- ""Magnet-1"" map (Lord Kelvin's magnet fractal) ---
  var num = z*z + c - Complex.One;
  var den = 2*z + c - 2;
  return (num / den) * (num / den);

  --- Exponential map ---
  return Complex.Exp(z) + c;

  --- Sine map (Devaney) ---
  // Sin(0) = 0, so z must start non-zero — seed z = c on n = 0.
  if (n == 0) z = c;
  return c * Complex.Sin(z);

  --- Cosine map ---
  // Cos(0) = 1 → fine to start at z = 0, but seeding from c gives
  // a more interesting first iterate.
  if (n == 0) z = c;
  return c * Complex.Cos(z);

  --- Lambda map (λ·z·(1 − z)) ---
  // Critical point z = 1/2 is the canonical start for the logistic
  // family.  Without seeding, z = 0 is a fixed point.
  if (n == 0) z = new Complex(0.5, 0.0);
  return c * z * (Complex.One - z);

  --- Bird-of-prey (perturbed Burning Ship) ---
  var w = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary));
  return w*w*w + c;

  --- Celtic Mandelbrot ---
  var zr2 = z.Real * z.Real - z.Imaginary * z.Imaginary;
  var zi2 = 2 * z.Real * z.Imaginary;
  return new Complex(Math.Abs(zr2) + c.Real, zi2 + c.Imaginary);

  --- Buffalo fractal ---
  var w = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary));
  return w*w - w + c;

  --- z² + c with time-varying twist ---
  double k = 0.005 * n;
  var rot = new Complex(Math.Cos(k), Math.Sin(k));
  return rot * (z*z) + c;

  --- Heron-step iteration ---
  // c / z at z = 0 is NaN — seed z = c (skip the singularity).
  if (n == 0) z = c;
  return 0.5 * (z + c / z);

=== Save / Load ===

  Save…    Prompts for a name; persists Source under that key.
           Re-saving an existing name replaces.
  Delete   Removes the currently-selected saved equation.
  Combo    Picking a saved entry loads it into the editor and
           recompiles immediately.

Saved entries round-trip through the JSON file by hand-edit; the
dialog reloads from disk on next launch.

=== Limitations ===

  • No high-precision (DD/QD) path — User Equation is double only.
    Zoom usefully cap ≈ 1e13.
  • No perturbation theory acceleration.
  • The signature does NOT expose the previous iterate, so true
    multi-step memory recurrences (Phoenix, recurrence relations
    with z_(n−1), z_(n−2)) can only be approximated.
  • Per-pixel delegate call overhead — slower than the typed
    kernels by ~3-5×.
";

        private static string MathUserBulbText() =>
@"=== User Bulb (3D) — Overview ===

The User Bulb engine is the 3D analogue of User Equation.  It
compiles a C# expression / statement block at runtime (via
Roslyn) into a per-iteration step function

        Vec3  Step(Vec3 z, Vec3 c, int n, double[] p)     // Vec3 algebra
        Quat  Step(Quat z, Quat c, int n, double[] p)     // Quat algebra

and renders the resulting escape-time set as a real 3D surface
using Mandelbulb-style raymarching with an analytic distance
estimator (for recognized closed-form maps) or a numerical
Jacobian distance estimator (for arbitrary maps).

Where User Equation produces flat 2D images of complex maps, User
Bulb produces shaded 3D bulbs, foam, sponges, and shells in
genuine three-space — lit by up to three directional lights,
shaded by surface normals, drawn from any camera angle, optionally
animated by a global time parameter t, and exportable to OBJ
mesh files for printing or external 3D editors.

Open via:  Fractal Type → ""User Bulb (3D)""
           Floating Menu → gear icon (when User Bulb is active)

The dialog is modeless and auto-compiles ~500 ms after the last
keystroke.  Errors render in red below the editor.  Camera,
lighting, iteration count, epsilon, bailout, Jacobian h, params,
animation, color driver, lighting weights, and all view knobs
update without recompiling — only changes to the source body,
algebra mode, chain steps, or param names trigger a fresh compile.

=== Dialog Layout (two columns) ===

Left column (top → bottom):
  Hint line + Saved row (combo + Save / Delete / Import / Export
    / Promote-to-fractal-list)
  Editor (multiline C# body)
  Error label
  Camera        Distance, Theta°, Phi°, Light θ°/φ°, Reset cam
  Render        Iterations, Bailout, Max steps, Epsilon, Jac h,
                Cull r, DE mode, Backend, Algebra, Slice W
  Params        Named scalar sliders (Add / Remove)
  Animation     ▶/■ Play, Speed, t
  Julia mode    Enable + c.X/c.Y/c.Z/c.W

Right column (top → bottom):
  Color driver  Combo + trap XYZ + iter axis
  Lighting      L1 / L2 / L3 intensity, AO samples, fog density
  View          FOV°, Clip+Y, Supersample (1x/2x/4x)
  Chain         Named-output step list (+ Step / X)
  Export        Export mesh (OBJ)…

=== How 3D Escape-Time Works ===

The 2D Mandelbrot maps each pixel to a complex c, runs
zₙ₊₁ = zₙ² + c starting from z₀ = 0, and asks ""does |z| stay
bounded?""  Plot escape time → fractal.

3D escape-time generalises by replacing the complex value with a
3D vector v ∈ ℝ³.  Each point p in 3-space becomes c.  We iterate
some user-defined map vₙ₊₁ = f(vₙ, c, n) and again ask ""does
|v| stay bounded?""  The SET of c-points that stay bounded is now
a 3D solid, not a 2D plane.

Rendering this solid as an image requires raymarching:

  1. The CAMERA sits on a sphere around the origin (controlled
     by Distance, Theta, Phi).  Each pixel emits a RAY.
  2. A bounding-sphere clip skips rays that miss the cull radius.
  3. A cone-march tile prepass estimates a per-tile entry t-hint
     so per-pixel marches can start past empty space.
  4. The ray marches forward one ""safe step"" at a time.  Step
     length = DISTANCE ESTIMATE (DE) at the current point — a
     lower bound on how far we can move without hitting surface.
  5. When DE < ε, declare a HIT and record the surface position.
  6. SHADE the hit: surface normal (forward-difference of DE),
     three directional lights (L1/L2/L3 intensities), optional
     screen-space AO, optional fog mix to sky color.
  7. COLOR the pixel through the active IColorMap, modulated by
     the active Color Driver (StepDepth / OrbitTrap / etc.).

The user only writes f.  Everything else — raymarching, DE,
normals, lighting, color, AO, fog, supersampling — is handled
by the engine.

=== The Vec3 Type (full API) ===

z and c are Vec3 (FracturingFog.Models.Vec3), a double-precision
3D vector with operator overloads:

  Fields:
    z.X, z.Y, z.Z          components (double)

  Properties:
    z.Length               √(X² + Y² + Z²)
    z.LengthSquared        X² + Y² + Z²

  Operators:
    a + b, a - b, -a       component-wise
    a * s, s * a, a / s    scalar multiply / divide

  Constants:
    Vec3.Zero, Vec3.One

  Static — geometric / arithmetic:
    Vec3.Dot(a, b)               dot product (scalar)
    Vec3.Cross(a, b)             cross product (Vec3)
    Vec3.Sin(v) / Cos(v)         component-wise trig
    Vec3.Sinh(v) / Cosh(v)       component-wise hyperbolic
    Vec3.Exp(v)                  component-wise exp
    Vec3.Abs(v)                  component-wise |x|

  Static — fractal-authoring helpers:
    Vec3.Pow(v, n)
        Triplex SPHERICAL power.  r=|v|, θ=atan2(y,x),
        φ=asin(z/r).  Returns
          r^n · (cos(nφ)cos(nθ), cos(nφ)sin(nθ), sin(nφ)).
        This is the standard Mandelbulb formula — Vec3.Pow(z, 8)
        IS the canonical p=8 bulb.

    Vec3.Rot(v, axis, angle)
        Rodrigues rotation of v around `axis` by `angle` radians.

    Vec3.BoxFold(v, limit)
        Per-axis Tglad fold:  |x| > limit ? sign(x)·2·limit − x : x.
        Mandelbox component.

    Vec3.SphereFold(v, rMin, rMax)
        Inversion fold: inside rMin scales by (rMax/rMin)²,
        between scales by rMax²/r², outside passes through.
        Mandelbox component.

    Vec3.AbsX(v) / AbsY(v) / AbsZ(v)
        Take absolute value of one axis only (asymmetric folds).

    Vec3.Mod(v, period)
        Periodic-space repeat per axis — tile a fractal across
        a lattice without going to infinity.

    Vec3.SMin(a, b, k)                   (scalars)
        Smooth-min DE blend:  −log(exp(−k·a)+exp(−k·b)) / k.
        Use to UNION two distance fields with C¹ continuity.

    Vec3.ToSpherical(v) → (r, θ, φ)
    Vec3.FromSpherical(r, θ, φ) → Vec3
        Bidirectional spherical-coord conversion.

  Instance methods:
    v.Normalized()         unit-length copy (Vec3.Zero if |v|≈0)

To build a new vector use the constructor:

    new Vec3(1.0, 2.0, 3.0)

Vec3 is a readonly record struct — cheap to copy, equality is
component-wise.

=== The Quat Type (4D mode) ===

When ""Algebra"" = ""Quat (4D)"", the step signature becomes
Quat→Quat.  The Quat type:

  Fields:    Q.W, Q.X, Q.Y, Q.Z
  Length, LengthSquared
  Operators: +, −, unary −, ·s (scalar), Quat·Quat (HAMILTON
             product — standard quaternion multiply)
  Quat.Zero, Quat.Identity (1, 0, 0, 0)
  q.Conjugate()              (W, −X, −Y, −Z)
  Quat.Dot(a, b)
  Quat.FromVec3(v, w = 0)    promote Vec3 to Quat
  q.ToVec3()                 project Q.X/Y/Z

The raymarched 3-space slice in Quat mode comes from the camera
ray's (x, y, z) plus the user-chosen ""Slice W"" 4th coordinate.
Changing Slice W explores different 3D slices of the same 4D set.

=== Algebra Mode (Vec3 vs Quat) ===

  Vec3 (3D)    Default.  z and c are Vec3 (3 components).
               Slice W ignored.  Fastest.

  Quat (4D)    z and c are Quat (4 components).  c.W comes from
               the Slice W slider, NOT from the pixel.
               Julia mode's c.W field becomes active.
               Each algebra change triggers a recompile.

=== Step Signature (full form) ===

The compiled body is wrapped as:

  Vec3 mode:  Vec3 Step(Vec3 z, Vec3 c, int n, double[] p)
  Quat mode:  Quat Step(Quat z, Quat c, int n, double[] p)

  z          previous iterate
  c          per-pixel constant (Vec3) or
             (px.X, px.Y, px.Z, SliceW) (Quat).
             In Julia mode this is REPLACED with the user JuliaC
             value for every iteration.
  n          0-based iteration index
  p          named-param vector.  Indexed by NAME in your source.
             A trailing slot p[p.Length-1] is reserved for the
             global animation time `t` — you can also reference it
             as the bare local `double t` (the wrapper unpacks it).

The body must RETURN a Vec3 (or Quat in Quat mode).

  • Single expression form (no semicolon, no `return` keyword):
        Vec3.Pow(z, 8) + c
    Wrapper adds `return … ;`.

  • Multi-statement form:
        var v = Vec3.Pow(z, 8);
        return v + c;

=== Iteration Loop ===

The engine ALWAYS starts the orbit at z = Zero (Vec3 or Quat).
At each sample point P (a position in 3-space along the camera
ray):

        c = (P.x, P.y, P.z)     // Vec3 mode
        c = (P.x, P.y, P.z, W)  // Quat mode (W = Slice W slider)
        z = Zero
        for n in 0 … Iterations:
            if |z| > Bailout: break
            z = Step(z, c, n, p)

In Julia mode, `c` is replaced with the fixed user-supplied
JuliaC for every iteration.  z is still seeded to Zero.

=== Distance Estimation — DE mode ===

Three DE modes, chosen from the ""DE mode"" combo:

  Auto       Engine attempts to detect a closed-form ""power-N
             triplex"" pattern in your source.  If matched, uses
             the fast Hubbard-Douady single-trajectory analytic
             DE.  Otherwise falls back to Numerical.  Default.

  Analytic   Forces Hubbard-Douady analytic DE:
                 DE(p) = 0.5 · ln(|z|) · |z| / dr
             where dr is updated analytically as
                 dr = p · r^(p−1) · dr + 1.
             ~4× faster than Numerical but only valid for triplex
             power maps.  Using it on the wrong map gives WRONG
             surfaces.  Pick this for vanilla Mandelbulb /
             Vec3.Pow(z, N) + c bodies.

  Numerical  Always use the numerical Jacobian DE.  Four trajectories
             run in lockstep:
                 z_base   c
                 z_px     c + h·êx
                 z_py     c + h·êy
                 z_pz     c + h·êz
             dr = max(|z_px−z_base|, |z_py−z_base|, |z_pz−z_base|) / h.
             Works for ANY map; ~4× slower than Analytic.

The ""Jac h"" slider sets the finite-difference perturbation:
1e-4 default.  Too small → cancellation noise; too large →
soft-edged surface.

=== Backend (CPU vs GPU) ===

  CPU                   Roslyn-compiled delegate via Parallel.For
                        over rows.  Always available; correct for
                        every map.

  GPU (experimental)    ILGPU JIT'd kernel.  Currently the GPU
                        backend ships ONE pre-baked kernel: triplex
                        spherical power-N with integer N + c.  Any
                        body the engine cannot translate falls
                        back to CPU silently.  Use to get 5–20×
                        speed on stock Vec3.Pow(z, N) + c renders.

=== Cull Radius ===

Each ray is clipped against a sphere of radius ""Cull r"" centered
at origin BEFORE marching.  Rays that miss the sphere render the
sky color directly — zero march cost.

  • Default 2.0 fits the canonical Mandelbulb.
  • Mandelbox / large folds need 4–8.
  • Aggressive cull (1.5) speeds exploration of small bulbs.

=== Camera, Lighting, Render Knobs ===

  Camera (sphere around origin):
    Distance      orbit radius.  Smaller = closer.
    Theta°        azimuth (around Y).
    Phi°          elevation (1..179°).  90° = equator.
    Reset cam     Restore the canonical default view.

  Lighting (KEY direction):
    Light θ°, φ°  Direction of the primary light L1.

  Render:
    Iterations    DE inner-loop count.  4–12 sane range.
    Max steps     Raymarch step cap.  64–192 typical.
    Bailout       |z| escape threshold.  2.0–4.0 standard.
    Epsilon       Surface hit threshold.  0.0005–0.005.
    Jac h         Numerical-DE perturbation.  1e-4 default.
    Cull r        Bounding-sphere radius.

  Pan / Zoom (top-level toolbar):
    CenterX/Y     Screen-space pan in NDC units (drag canvas).
    Zoom          Scales Distance / Zoom → cam moves in/out.

Mouse:
    Mouse wheel             Zoom (smaller/larger Distance).
    Left-click drag         Pan in screen space.
    Right-click drag X      Orbit Theta (spin horizontally).
    Right-click drag Y      Orbit Phi.  Y is INVERTED in
                            User Bulb so drag-down → camera tips
                            UP, matching standard 3D editors.

=== Params Bank (named scalar sliders) ===

Add arbitrary scalar params from the dialog's ""Params"" panel.
Each row gives Name, Value, Min, Max, and an X (remove) button.

In source code, reference a param BY NAME — the wrapper exposes
each as a local `double <name>`:

  Params:  k = 2.0, twist = 0.3, freq = 4.0

  Source:
    return Vec3.Pow(z, k) + c + Vec3.Sin(z * freq) * twist;

Changing a param VALUE re-renders only (no recompile).  Changing
a param NAME, adding, or removing one triggers a recompile.

=== Animation (global t) ===

The Animation bar plays a continuously increasing clock t (units
of seconds × Speed).  The ""t"" numeric directly drives a global
double named `t` available in your source.  Use it to morph or
spin maps:

    return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;

▶ starts the timer (~30 Hz updates).  ■ pauses.  Speed slider
multiplies the per-tick delta.  Setting t manually fires a render.

=== Julia Mode ===

Tick ""Enable (fix c)"" in the Julia group to swap the per-pixel
`c` with a single user-supplied constant for EVERY iteration.

  c.X / c.Y / c.Z   Vec3 mode: the constant in 3-space.
  c.W               Quat mode: 4th component of the constant.
                    Disabled when Algebra = Vec3.

Pixel coordinate still drives the raymarch position; only the
iteration's `c` is overridden.  This produces a 3D Julia set for
the chosen formula.

=== Color Drivers ===

Selects what the IColorMap receives as input per pixel.

  StepDepth         Number of march steps before hit (default).
                    Highlights silhouette depth.

  OrbitTrap         Min distance from orbit to the user-set trap
                    point (tx, ty, tz).  Reveals tendrils that
                    pass close to the trap.  ""tx/ty/tz"" numerics
                    set the trap location.

  EscapeAngle       Atan2 of the escape vector projected onto
                    user-chosen axis.  Highlights spiral structure.

  FinalMagnitude    Log of |z| at escape.  Smooth gradient
                    across the surface.

  IterComponent     Specific axis of the final iterate
                    (X / Y / Z chosen by the ""axis"" combo).
                    Anisotropic color across the bulb.

  Normal            Surface normal mapped to RGB.  Pure shading
                    debug.  No palette involvement.

=== Lighting (3-light + AO + fog) ===

The shader sums three directional-light contributions, an
optional ambient-occlusion term, and a fog mix to sky color.

  L1 intensity     0..N.  Primary key light (uses dialog's
                   Light θ°/φ°).
  L2 intensity     Secondary fill light (fixed offset).
  L3 intensity     Tertiary rim light (back-light).
  AO               Cone-march AO sample count.  0 disables.
                   4–8 reveals concavities.
  Fog              Beer's-law density.  0 disables; 0.2–0.5
                   gives atmospheric depth.

=== View (FOV / clip / supersample) ===

  FOV°             Perspective field of view.  60° default.
                   < 30° = telephoto (flat); > 90° = fisheye.
  Clip+Y           When ticked, half-space clip removes geometry
                   above the y=0 plane.  Useful for cross-section
                   views of solid bulbs.
  SS               Supersample: 1x, 2x, or 4x grid OGSS.
                   4x = render at 4× linear resolution and downsample.
                   Use for finished frames; SLOW.

=== Chain (multi-step, named outputs) ===

When the Chain panel has ≥ 1 step, the single-source editor is
IGNORED.  Each chain step has:

  Output name    Identifier added as a local Vec3 (or Quat)
                 available to subsequent steps and to the
                 ""Output"" expression of the chain.
  Source         A C# body returning Vec3 / Quat (same rules as
                 the single editor).

The chain runs sequentially per iteration; the LAST step's
output becomes the new z.  Earlier steps' named outputs are
visible to later steps:

  Step 1   name = pre
           source = Vec3.Rot(z, new Vec3(0,1,0), t)

  Step 2   name = sq
           source = Vec3.Pow(pre, 8) + c

Iteration 0: z = Zero → step1 makes `pre` from Zero rotated;
step2 makes `sq` from Vec3.Pow(pre, 8) + c.  z ← sq.

To revert to the single-editor flow, delete every chain row.

=== Save / Load and Promote ===

Saved bulbs persist to
    %APPDATA%\FracturingFog\userbulbs.json

  Save…       Stores the current editor text under the typed
              name (replaces an existing entry of the same name).
  Delete      Removes the selected saved entry.
  Import…     Reads a single-entry .fbulb JSON file.  Renames
              on name collision.
  Export…     Writes the selected entry to a .fbulb JSON file.
  Promote to fractal list
              When ticked, the saved bulb appears in the main
              Fractal-Type dropdown as a first-class option.

The store ships 10 default presets seeded on first run.  Delete,
edit, and re-save freely — defaults are not protected.

=== Mesh Export (OBJ) ===

Click ""Export mesh (OBJ)…"" to sample the DE field on a uniform
N³ grid inside a cube of side 2·Range centered on the origin.
Each grid cell with a surface crossing emits a voxel cube of
triangles.  Output is ASCII OBJ.

  Grid N      8 … 256.  N=64 ≈ 32k voxels, fast; N=128 ≈ 256k,
              slow (10s+).
  Range       Half-extent of the sample cube.  2.0 fits most
              canonical bulbs.

The result is BLOCKY (voxel cubes, not interpolated triangles).
Adequate for 3D printing or external smoothing.  Marching-cubes
with the 256-entry triangulation table is a follow-up.

=== Quick Reference — Available APIs in Step Body ===

  Imports already in scope (no `using` needed):
      using System;
      using System.Numerics;
      using FracturingFog.Models;
      using static System.Math;          // Sin/Cos/etc. unqualified

  Scalar math:
      Sin, Cos, Tan, Asin, Acos, Atan, Atan2,
      Sinh, Cosh, Tanh,
      Exp, Log, Log2, Pow, Sqrt, Cbrt,
      Abs, Min, Max, Floor, Ceiling, Round,
      Sign, Clamp,
      PI, E, Tau

  Vector helpers:
      Vec3.{Pow, Rot, BoxFold, SphereFold, AbsX, AbsY, AbsZ,
            Mod, SMin, ToSpherical, FromSpherical,
            Sin, Cos, Sinh, Cosh, Exp, Abs,
            Dot, Cross, Zero, One}
      Quat.{FromVec3, Conjugate, Dot, Zero, Identity}

  All standard C# 12 syntax: locals, ternary, switch expressions,
  pattern matching, local functions, tuples.

  Globals available as bare locals:
      double t          (animation clock)
      double <param>    (one per Params row, by Name)

=== Pitfalls ===

  • NO-ESCAPE MAPS look BLANK.  Sin-only formulas or hyperbolic
    formulas that orbit forever never escape.  DE returns a flat
    value every step; the ray never converges to a surface.
    Raise Bailout substantially or rephrase so |z| grows.

  • Z₀ = 0 FIXED POINTS.  Any f where f(0, 0) = 0 produces an
    all-in-set image when c = 0 is at the center.  Enable Julia
    mode (fixes c) or pre-rotate / pre-translate in source.

  • EXPLODING MAPS.  z^z, double-exp, and similar grow to ∞
    within one or two iterations.  Drop Iterations to 2–4, raise
    Bailout to 1e6 — or rescale inputs.

  • NaN/INF.  Math.Log / Math.Sqrt of negatives, Math.Atan2(0,0),
    division by zero propagate as NaN through Vec3 arithmetic
    silently.  Guard with + 1e-6 in denominators and
    Math.Max(r, 1e-12) before Math.Log.

  • DE MODE MISMATCH.  Analytic DE on a non-triplex map gives
    WRONG surfaces.  Use Auto (the detector will fall back to
    Numerical for unknown shapes) or pick Numerical explicitly.

  • TIGHT JAC h on DISCONTINUOUS MAPS.  Mandelbox-style folds
    have piecewise derivatives.  Raise Jac h to 1e-3 for folds.

  • CULL R TOO SMALL.  If your fractal extends beyond Cull r, the
    bounding sphere clips silhouettes.  Mandelbox needs 4–8;
    canonical bulbs are happy with 2.

  • GPU BACKEND FALLBACK.  Anything beyond plain
    Vec3.Pow(z, INT) + c silently falls back to CPU.  Check
    perf — if GPU was expected and you don't see a speedup, the
    body wasn't translatable.

  • PERF.  Roslyn delegate ~40 ns per call.  Heavy Math.Pow /
    Atan2 multiplies that.  Prefer x*x*x over Math.Pow(x, 3);
    hoist invariants out of the body where possible.

=== Troubleshooting ===

Black screen, no shape:
  • Error label green ✓ Compiled?  Red = syntax/compile error.
  • Bump Bailout to 16 — your map may not escape at 4.
  • Drop Iterations to 4 — may be hitting NaN partway.
  • Spin Camera Theta — initial view may face empty side.
  • Raise Cull r — fractal may sit outside the bounding sphere.

Speckled normals / noisy shading:
  • Raise Jac h from 1e-4 → 1e-3.
  • Drop Epsilon to 0.0005.

Bulb looks ""melted"" / soft edges:
  • Lower Epsilon to 0.0008.
  • Raise Max steps to 192.

Banding / tile boundaries visible:
  • DE mode may be wrong.  Force Numerical and re-render.
  • Drop the temporal cache by toggling re-compile (edit source
    and revert) to flush stale tiles.

Render is unbearably slow:
  • Drop Iterations to 4, Max steps to 48 for exploration.
  • Switch Backend → GPU for plain Vec3.Pow bodies.
  • Set SS back to 1x.  Resize window smaller.

=== Limitations ===

  • Roslyn warm-up: first compile after app start ~500 ms.
    Edits after are debounced 500 ms then instant.
  • GPU backend only handles pre-baked triplex Vec3.Pow(z, N) + c
    kernels.  Anything else uses CPU.
  • Quaternion GPU translator not implemented yet.
  • Mesh exporter is voxel-cube (blocky).  Real marching cubes
    with the 256-entry triangulation table is a follow-up.
  • Numerical DE uses max column norm — a conservative spectral-
    radius proxy.  Some maps render with surfaces slightly
    inside the true set boundary.
  • Region save/recall round-trips the saved bulb NAME, not the
    raw source.  Save your edits to the library first.

=== Examples — Starting Points ===

Each block is a complete equation body.  Paste it into the editor
(or save with the Saved combo for re-use).  Each example lists a
suggested CONFIG block — adjust Distance / Cull / etc. from the
canonical defaults.

────────────────────────────────────────────────────────────────
  1. SQUARE TRIPLEX  (the default — fast 3D Mandelbrot analogue)
────────────────────────────────────────────────────────────────
Source:
  return new Vec3(
      z.X*z.X - z.Y*z.Y - z.Z*z.Z,
      2*z.X*z.Y,
      2*z.X*z.Z) + c;

Config:
  Algebra Vec3 · Backend CPU (or GPU)
  DE mode Auto (detects analytic pattern)
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0015 · Jac h 1e-4 · Cull r 2.0
  Camera Distance 3 · Theta 45° · Phi 63°

────────────────────────────────────────────────────────────────
  2. MANDELBULB p=8  (canonical Mandelbulb via Vec3.Pow helper)
────────────────────────────────────────────────────────────────
Source:
  return Vec3.Pow(z, 8) + c;

Config:
  Algebra Vec3 · Backend GPU (analytic kernel)
  DE mode Auto (analytic Hubbard-Douady)
  Iterations 8 · Bailout 2 · Max steps 128
  Epsilon 0.0008 · Cull r 1.5
  Camera Distance 2.8 · Phi 65°

────────────────────────────────────────────────────────────────
  3. POWER-12 RIDGED BULB  (deeper folds, more spines)
────────────────────────────────────────────────────────────────
Source:
  return Vec3.Pow(z, 12) + c;

Config:
  Algebra Vec3 · Backend GPU · DE mode Auto
  Iterations 10 · Bailout 2 · Max steps 160
  Epsilon 0.0006 · Cull r 1.5
  Lighting L1 1.0  L2 0.4  L3 0.3  AO 4  Fog 0.08

────────────────────────────────────────────────────────────────
  4. ANIMATED BREATHING BULB  (uses global t)
────────────────────────────────────────────────────────────────
Source:
  return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 6 · Bailout 4 · Max steps 64
  Epsilon 0.002 · Jac h 1e-4 · Cull r 1.5
  Animation ▶ Play  Speed 0.5
  SuperSample 1 (turn off SS for live playback)
  Power oscillates between 2 and 6 — bulb pulses on the beat.

  Notes:
  · Each tick is a full CPU raymarch. Analytic + GPU path requires
    the power to be a literal numeric constant (regex-detected), so
    animated power always falls to numerical Jacobian.
  · Drop Iterations / Max steps / window size to keep frame time
    under ~2 s. At Iter 6 / Steps 64 / 600x400 expect ~1-3 s/frame
    on midrange CPUs. Status bar showing ""calculating"" between
    frames is expected.
  · Temporal cache now keys on t — earlier builds would freeze the
    first frame and skip subsequent ticks. Rebuild if you see that.

────────────────────────────────────────────────────────────────
  5. QUARTIC + SIN PERTURBATION  (power escape + trig folds)
────────────────────────────────────────────────────────────────
Source:
  return Vec3.Pow(z, 4) + Vec3.Sin(z) * 0.5 + c;

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Jac h 1e-4 · Cull r 1.5
  Camera Distance 2.8 · Phi 65°
  Color driver OrbitTrap  tx 0  ty 0  tz 0  (highlights folds)

  Why not plain Vec3.Sin(z)*k + c: pure sin is bounded (|sin|≤1)
  so |z| stays bounded → never crosses bailout → DE meaningless
  → blank or filled-sphere render. Same for Cos. Trig that GROWS
  (Vec3.Sinh / Vec3.Cosh / Vec3.Exp) can also explode to Inf in
  the numerical Jacobian and stall the raymarch. The reliable
  pattern is a power term (escapes) + a bounded trig perturbation
  (adds visual texture). For a pure bounded-trig look, switch
  Color driver to OrbitTrap or FinalMagnitude — escape-time has
  no meaning for non-escaping maps.

────────────────────────────────────────────────────────────────
  6. ABS-BULB p=8  (Burning-Ship-style fold before squaring)
────────────────────────────────────────────────────────────────
Source:
  return Vec3.Pow(Vec3.Abs(z), 8) + c;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 2 · Max steps 128
  Epsilon 0.001 · Cull r 1.5
  Distinctive flat-top + sharp ridge silhouette.

────────────────────────────────────────────────────────────────
  7. MANDELBOX  (Tglad box fold + sphere fold + scale)
────────────────────────────────────────────────────────────────
Source:
  var v = Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0);
  return v * 2.0 + c;

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 12 · Bailout 16 · Max steps 192
  Epsilon 0.0006 · Jac h 1e-3 · Cull r 6.0
  Camera Distance 8 · Phi 75°
  Lighting AO 6 · Fog 0.15  (cavities benefit from AO)
  Variants: try scale = −1.5 (negative Mandelbox).

────────────────────────────────────────────────────────────────
  8. QUATERNION JULIA  (4D quaternion squaring, sliced)
────────────────────────────────────────────────────────────────
Source (Quat algebra):
  return z * z + c;

Config:
  Algebra Quat (4D) · Backend CPU · DE mode Numerical
  Slice W 0.3  (try 0, 0.1, 0.5, −0.4)
  Iterations 10 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Cull r 2.0
  Julia mode ON · c = (−0.2, 0.4, −0.4, 0.0)
  Each Slice W is a different 3D slice of the same 4D set.

────────────────────────────────────────────────────────────────
  9. VEC3 JULIA  (3D triplex with fixed c)
────────────────────────────────────────────────────────────────
Source:
  return new Vec3(
      z.X*z.X - z.Y*z.Y - z.Z*z.Z,
      2*z.X*z.Y,
      2*z.X*z.Z) + c;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 12 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Cull r 2.5
  Julia mode ON · c = (0.30, 0.50, −0.20)
  Re-render and vary c.X/Y/Z to morph the dendrite topology.

────────────────────────────────────────────────────────────────
  10. ROTATED-TRIPLEX HELIX  (Rodrigues + animated t)
────────────────────────────────────────────────────────────────
Source:
  var sq = new Vec3(
      z.X*z.X - z.Y*z.Y - z.Z*z.Z,
      2*z.X*z.Y,
      2*z.X*z.Z);
  return Vec3.Rot(sq, new Vec3(0, 1, 0), t * 0.3) + c;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0012 · Cull r 2.5
  Animation ▶ Play  Speed 0.4
  Bulb spins around Y; t-rotated input warps each iteration.

────────────────────────────────────────────────────────────────
  11. PERIODIC KALEIDO  (Vec3.Mod tiles a fractal across space)
────────────────────────────────────────────────────────────────
Source:
  var p = Vec3.Mod(z, 2.0);
  return Vec3.Pow(p, 8) + c;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 6 · Bailout 4 · Max steps 192
  Epsilon 0.001 · Jac h 5e-4 · Cull r 5.0
  Camera Distance 6 · FOV 80°
  Creates infinite lattice of mini-bulbs.

────────────────────────────────────────────────────────────────
  12. CROSS-PRODUCT RIBBONS  (twisted square triplex)
────────────────────────────────────────────────────────────────
Source:
  var sq = new Vec3(
      z.X*z.X - z.Y*z.Y - z.Z*z.Z,
      2*z.X*z.Y,
      2*z.X*z.Z);
  return sq + c + Vec3.Cross(z, c) * 0.5;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0012 · Cull r 3.0
  Color driver EscapeAngle  axis Y  (highlights spiral flow)

────────────────────────────────────────────────────────────────
  13. SMOOTH-MIN BLEND  (union of two DE fields with SMin)
────────────────────────────────────────────────────────────────
Use Chain (right column) with TWO steps:

  Step 1   name = a
           source = Vec3.Pow(z, 8) + c
  Step 2   name = b
           source = Vec3.Pow(z, 4) + c

  Single editor (overridden by chain — used here as scratch).

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 128
  Epsilon 0.0012 · Cull r 2.5
  Chain runs sequentially; LAST step's output becomes new z.
  Try swapping powers (8/3, 12/6) to morph between bulbs.

────────────────────────────────────────────────────────────────
  14. PARAMETRIC TWIST-BULB  (uses named param sliders)
────────────────────────────────────────────────────────────────
Params (Add three rows):
  p     Value 8.0   Min 2    Max 16
  k     Value 0.3   Min 0    Max 2
  freq  Value 4.0   Min 1    Max 20

Source:
  var v = Vec3.Pow(z, p);
  var twist = Vec3.Sin(z * freq) * k;
  return v + c + twist;

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.001 · Jac h 5e-4 · Cull r 2.5
  Drag p slider 4→12: morph between cubic and high-order bulb.
  Drag freq 2→16: add fine high-frequency surface detail.
  Drag k 0→1: blend from clean bulb to twisted surface.
";

        private static string MathSandboxText() =>
@"=== Sandbox — Overview ===

The Sandbox fractal type runs a USER-SUPPLIED EXPRESSION through
a restricted, in-process parser.  Unlike the User Equation
engine — which compiles arbitrary C# via Roslyn and therefore has
access to the full .NET BCL (File.IO, reflection, Process.Start) —
the Sandbox evaluator is built from a hand-written grammar with
NO access to the runtime or filesystem.

That makes Sandbox safe to share, import from JSON, paste from a
web page, or run on equations from untrusted sources.  The trade
is expressiveness: there are no statements, no loops, no method
calls outside the built-in function list below.

Open via:  Fractal Type → ""Sandbox""  →  Params button
The dialog auto-compiles 500 ms after each keystroke.  Errors
appear in red below the editor.

Saved equations are persisted to:

    %APPDATA%\FracturingFog\sandboxequations.json

When a Region is saved while Fractal Type = Sandbox, the name of
the active saved equation is recorded with the Region.  Recalling
the Region looks the source back up by name, so editing a saved
equation propagates to every Region that references it.

=== Iteration Model ===

The runtime drives the standard escape-time loop:

        z = 0
        for n in 0 … MaxIterations:
            if |z|² ≥ 1024: break
            z = Step(z, c, n)        // <-- your expression

The expression you write IS the body of Step.  It must evaluate
to a Complex value.  Bailout (radius 32), smoothing, and color
mapping mirror the Mandelbrot path.

=== Available Variables ===

  z   Complex — the CURRENT iterate.  Starts at 0 on iteration 0.
  c   Complex — the PIXEL coordinate (constant per pixel).
  n   Real    — iteration index (0-based).

=== Constants ===

  pi   3.14159265358979…
  e    2.71828182845904…
  i    Imaginary unit (Complex 0 + 1i)

=== Operators ===

  Arithmetic   +  −  *  /          Both real and complex operands
  Power        ^                   z^2, z^n; right-associative
  Unary minus  −                   −z, −(re(z) + im(z))
  Comparison   <  >  <=  >=  ==  != Real operands only.  Use abs(),
                                   re(), im(), arg() to project a
                                   Complex value to a scalar first.
  Logical      &&  ||  !           Short-circuit.  0 = false, any
                                   non-zero magnitude = true.
  Ternary      cond ? a : b        Branches may differ in type;
                                   real promotes to complex if
                                   the other branch is complex.
  Let          let x = expr in body  Introduces a local binding;
                                   bindings nest and may be chained.
  Grouping     ( … )

=== Built-in Functions ===

  Complex-returning (operate on real or complex):
      sin(z)   cos(z)   tan(z)
      sinh(z)  cosh(z)  tanh(z)
      exp(z)   log(z)   sqrt(z)
      conj(z)                // complex conjugate
      pow(a, b)              // a raised to b — same as a ^ b

  Real-returning (project Complex → scalar):
      abs(z)                 // magnitude |z|
      re(z)                  // real part
      im(z)                  // imaginary part
      arg(z)                 // argument (atan2(im, re))

=== Type Rules ===

The parser tracks two value kinds — Real and Complex — and
promotes Real → Complex automatically whenever a binary op mixes
them.  A few consequences:

  • Comparisons (< > <= >= == !=) require Real operands.  Wrap
    a complex value with abs(), re(), im(), or arg() to compare.
  • Logical operators read any value's magnitude — 0 is false,
    everything else is true.
  • The expression's final value is implicitly converted to
    Complex before being returned to the iterator.

=== Reserved Names ===

You may not rebind:  z, c, n, pi, e, i, let, in.

User let-bindings get their own slot — shadow-rebinding an outer
let with the same name in an inner let is allowed and follows
lexical scope.

=== Save / Load ===

  Save…    Prompts for a name; persists current source under it.
           Re-saving an existing name replaces.
  Delete   Removes the currently-selected saved equation.
  Combo    Picking a saved entry loads it into the editor and
           recompiles immediately.

Region recall:  the Region store records the SandboxName field
on save.  On Region select, MainForm asks the SandboxEquationStore
for the entry by name and loads its source.  If the saved entry
has been deleted, the Region falls back to whatever source was
last typed.

=== Examples — Drop-in Snippets ===

Each block below is a complete expression — paste it into the
editor and the renderer compiles + previews.  No trailing
semicolons; the expression IS the body.

  --- Mandelbrot ---
  z*z + c

  --- Mandelbrot using pow ---
  pow(z, 2) + c

  --- Multibrot (cubic) ---
  z^3 + c

  --- Multibrot (degree d, baked in) ---
  z^5 + c

  --- Tricorn / Mandelbar ---
  conj(z)^2 + c

  --- Burning Ship ---
  // Build w with absolute-valued real + imaginary parts.
  let w = abs(re(z)) + abs(im(z)) * i in w*w + c

  --- Phoenix-flavoured single-step ---
  z*z + c + 0.56667 * z

  --- z² + c with sine perturbation ---
  z*z + c + 0.1 * sin(z)

  --- Exponential map ---
  exp(z) + c

  --- Sine map ---
  // sin(0) = 0, so we need a non-zero seed on iteration 0.
  // Ternary swaps in c whenever n == 0.
  c * sin(n == 0 ? c : z)

  --- Cosine map ---
  c * cos(n == 0 ? c : z)

  --- Lambda / logistic map ---
  // Critical point z = 0.5 is the canonical start.
  let z0 = (n == 0 ? 0.5 : z) in c * z0 * (1 - z0)

  --- Heron iteration (Newton for √c) ---
  // Skip the c/0 singularity by seeding z = c on n == 0.
  let z0 = (n == 0 ? c : z) in 0.5 * (z0 + c / z0)

  --- Newton (z³ − 1) ---
  let z0 = (n == 0 ? c : z) in
    let z2 = z0*z0 in
      z0 - (z0*z2 - 1) / (3 * z2)

  --- Nova (z³ − 1 with c offset) ---
  let z0 = (n == 0 ? 1 : z) in
    let z2 = z0*z0 in
      z0 - (z0*z2 - 1) / (3 * z2) + c

  --- Magnet-1 (Lord Kelvin) ---
  let num = z*z + c - 1 in
    let den = 2*z + c - 2 in
      (num / den) ^ 2

  --- Time-varying twist on z² + c ---
  let k = 0.005 * n in
    (cos(k) + sin(k) * i) * z*z + c

  --- Celtic Mandelbrot ---
  // |Re(z²)| + i * Im(z²), then + c.
  let zr2 = re(z)*re(z) - im(z)*im(z) in
    let zi2 = 2 * re(z) * im(z) in
      abs(zr2) + zi2 * i + c

  --- Buffalo fractal ---
  let w = abs(re(z)) + abs(im(z)) * i in w*w - w + c

  --- Bird-of-prey (cubic Burning Ship) ---
  let w = abs(re(z)) + abs(im(z)) * i in w^3 + c

  --- Conditional kernel switch by n ---
  // Run z² + c for the first 8 iterations, then z³ + c.
  n < 8 ? z*z + c : z^3 + c

  --- Polar transform on c ---
  // Treat c in polar form, square the radius, rotate by phase.
  let r = abs(c) in
    let a = arg(c) in
      (r*r) * (cos(2*a) + sin(2*a) * i) + z*z

  --- Spiral perturbation ---
  z*z + c + 0.05 * (cos(n * 0.1) + sin(n * 0.1) * i)

  --- Mix two maps by n ---
  // Linear blend between z² + c and exp(z) + c over n.
  let t = n / 64 in
    (1 - t) * (z*z + c) + t * (exp(z) + c)

  --- Smooth ""abs"" via sqrt(z*conj(z)) ---
  // Just a demonstration of conj/sqrt; equivalent to abs(z).
  sqrt(z * conj(z)) + c

  --- Compare-driven branch ---
  // Diverge faster for pixels far from origin.
  abs(c) > 0.5 ? z*z + 2*c : z*z + c

=== Performance Notes ===

  • Pure-managed interpreter — no SIMD, no JIT-emitted IL.
    Roughly the same order of magnitude as User Equation but
    without the per-pixel delegate dispatch.
  • Each render thread allocates one environment array once
    (slots for z, c, n + every let-binding) and reuses it for
    every pixel.  No per-pixel allocation in the hot loop.
  • Deep let-binding chains pay a small recursive-eval cost —
    flatten where you can.
  • Real-only arithmetic stays in scalar mode internally and is
    cheaper than Complex arithmetic.  Use re()/im() projections
    when only a scalar is needed.

=== Safety Model ===

The DSL parser only emits AST nodes for the operators, functions,
and identifiers documented here.  It cannot:

  • Open, read, write, or enumerate files.
  • Invoke methods on the .NET BCL or any user assembly.
  • Allocate native memory, call P/Invoke, or use reflection.
  • Spawn processes, open sockets, or talk to the GPU directly.
  • Reach the file system or environment at all.

The only side effects an expression can have are:

  • Returning a Complex value the iterator consumes.
  • Throwing a runtime arithmetic exception (e.g. divide by 0,
    log of 0, sqrt of negative real), which the calculator
    catches per pixel and treats as ""escaped"".

Compared to User Equation:  Sandbox is roughly 2-3× more
restrictive but is safe to share, import, or run from untrusted
JSON.

=== Limitations ===

  • No high-precision (DD/QD) path — Sandbox is double only.
    Zoom usefully caps around ≈ 1e13.
  • No perturbation theory acceleration.
  • No statements:  every expression is a single value.  Use
    let-bindings for intermediate names.
  • No user-defined functions yet.  Repeat sub-expressions stay
    expanded.
  • No multi-step memory:  the signature exposes z, c, n only —
    not z_(n−1).  True Phoenix-style recurrences cannot be
    expressed directly.
";

        #endregion Math sub-tab content

        private TabPage BuildBioTab()
        {
            var page = MakePage("Mandelbrot");
            var rtb = MakeRichText(page);
            rtb.Height = page.ClientSize.Height - 90;

            rtb.Text =
@"=== Benoit B. Mandelbrot (1924 – 2010) ===

Polish-born French-American mathematician known as the
""father of fractal geometry"".

Born:        20 November 1924, Warsaw, Poland
Died:        14 October 2010, Cambridge, Massachusetts, USA (age 85)
Citizenship: French and American

=== Early Life ===

Born to a Lithuanian Jewish family in Warsaw, Mandelbrot fled with
his family to France in 1936 to escape the rising Nazi threat.
He was tutored largely by his uncle Szolem Mandelbrojt, a
mathematician at the Collège de France.  During WWII he hid in the
French countryside, attending school sporadically; despite this he
later credited his exceptional visual-geometric intuition to his
self-taught, picture-driven approach to mathematics.

=== Education & Career ===

  • École Polytechnique, Paris (Gaston Julia, Paul Lévy — 1944–47)
  • Caltech — M.S. in aeronautics (1949)
  • University of Paris — Ph.D. in mathematical sciences (1952)
  • Institute for Advanced Study, Princeton (1953, under von Neumann)
  • IBM Thomas J. Watson Research Center, Yorktown Heights NY
    (1958 – 1987) — IBM Fellow from 1974
  • Yale University — Sterling Professor of Mathematical
    Sciences (1999, becoming Yale's oldest tenure appointee)

=== Contributions ===

  • Coined the word ""fractal"" (1975, from Latin fractus = ""broken"")
  • Foundational text: ""The Fractal Geometry of Nature"" (1982)
  • Studied long-range dependence in cotton prices, river floods,
    word frequencies, telephone-line noise — finding scale
    invariance everywhere classical statistics had assumed
    Gaussian / Brownian behaviour
  • Hurst exponent / R/S analysis popularization
  • Multifractal formalism for turbulence and finance
  • Discovered & explored the set that now bears his name (1980)
  • Coastline paradox (1967) — ""How long is the coast of Britain?""

=== Honors (selected) ===

  • 1985  Barnard Medal for Meritorious Service to Science
  • 1986  Franklin Medal
  • 1993  Wolf Prize in Physics
  • 1999  Honorary doctorate, University of St Andrews
  • 2003  Japan Prize for Science and Technology
  • 2004  Best Business Book of the Year (FT/Goldman) — ""The
          (Mis)behavior of Markets""
  • 2006  Légion d'honneur (Officer)

=== In His Own Words ===

  ""Bottomless wonders spring from simple rules, which are
   repeated without end.""

  ""Clouds are not spheres, mountains are not cones, coastlines
   are not circles, and bark is not smooth, nor does lightning
   travel in a straight line.""
                                          — The Fractal Geometry of Nature

=== External Links ===
";
            // Link panel below text
            var linkPanel = new FlowLayoutPanel
            {
                Left = 0,
                Top = rtb.Top + rtb.Height + 4,
                Width = page.ClientSize.Width,
                Height = 84,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 4, 12, 4),
                BackColor = Color.FromArgb(22, 22, 22),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            linkPanel.Controls.Add(MakeLinkInline("Wikipedia: Benoit Mandelbrot",
                "https://en.wikipedia.org/wiki/Benoit_Mandelbrot"));
            linkPanel.Controls.Add(MakeLinkInline("MacTutor biography (St Andrews)",
                "https://mathshistory.st-andrews.ac.uk/Biographies/Mandelbrot/"));
            linkPanel.Controls.Add(MakeLinkInline("TED Talk: Fractals and the art of roughness",
                "https://www.ted.com/talks/benoit_mandelbrot_fractals_and_the_art_of_roughness"));
            linkPanel.Controls.Add(MakeLinkInline("IBM Research — Mandelbrot legacy",
                "https://www.ibm.com/history/benoit-mandelbrot"));
            page.Controls.Add(linkPanel);

            return page;
        }

        #endregion Tab Builders

        #region Helpers

        private static TabPage MakePage(string title) => new TabPage
        {
            Text = title,
            BackColor = Color.FromArgb(22, 22, 22),
            ForeColor = Color.FromArgb(200, 200, 200),
            Padding = new Padding(8),
            UseVisualStyleBackColor = false,
        };

        private static RichTextBox MakeRichText(TabPage parent)
        {
            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(210, 210, 210),
                Font = new Font("Consolas", 9.25f),
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = true,
                WordWrap = true,
            };
            rtb.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.LinkText) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            parent.Controls.Add(rtb);
            return rtb;
        }

        private LinkLabel MakeLink(string text, string url, int left, int top, Control parent)
        {
            var link = new LinkLabel
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                LinkColor = Color.FromArgb(120, 180, 240),
                ActiveLinkColor = Color.FromArgb(180, 220, 255),
                VisitedLinkColor = Color.FromArgb(150, 130, 200),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            link.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            _toolTip.SetToolTip(link, url);
            return link;
        }

        private LinkLabel MakeLinkInline(string text, string url) => new LinkLabel
        {
            Text = "• " + text,
            AutoSize = true,
            LinkColor = Color.FromArgb(120, 180, 240),
            ActiveLinkColor = Color.FromArgb(180, 220, 255),
            VisitedLinkColor = Color.FromArgb(150, 130, 200),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 2),
        }.Also(l =>
        {
            l.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            _toolTip.SetToolTip(l, url);
        });

        private string SafeRendererDescription()
        {
            try { return _rendererDescriptionProvider() ?? "none"; }
            catch { return "(unavailable)"; }
        }

        private void OnDrawTabItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not TabControl tc) return;
            var page = tc.TabPages[e.Index];
            var rect = tc.GetTabRect(e.Index);
            bool selected = e.Index == tc.SelectedIndex;

            using var bg = new SolidBrush(selected
                ? Color.FromArgb(45, 45, 45)
                : Color.FromArgb(28, 28, 28));
            e.Graphics.FillRectangle(bg, rect);

            using var border = new Pen(Color.FromArgb(60, 60, 60));
            e.Graphics.DrawRectangle(border, rect);

            using var fg = new SolidBrush(selected
                ? Color.FromArgb(230, 230, 230)
                : Color.FromArgb(160, 160, 160));
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            e.Graphics.DrawString(page.Text, tc.Font, fg, rect, fmt);
        }

        #endregion Helpers

        #region Form Events

        private void OnFormClosing(object? s, FormClosingEventArgs e)
        {
            _disposed = true;
        }

        #endregion Form Events
    }
}
