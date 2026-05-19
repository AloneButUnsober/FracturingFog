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
                ItemSize = new Size(108, 26),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _tabs.DrawItem += OnDrawTabItem;

            _tabs.TabPages.Add(BuildAboutTab());
            _tabs.TabPages.Add(BuildSystemInfoTab());
            _tabs.TabPages.Add(BuildFeaturesTab());
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
