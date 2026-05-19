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
                Text = $"  {_programName} v{_programVersion}  —  Help",
                Left = 6,
                Top = 10,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200),
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
            var rtb = MakeRichText(page);

            rtb.Text =
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

At zoom 10ⁿ the pixel spacing is ~4 · 10⁻ⁿ.  IEEE-754 double has
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
";
            return page;
        }

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
