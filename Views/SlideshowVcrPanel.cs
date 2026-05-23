// SlideshowVcrPanel.cs
//
// Floating "VCR" control bar shown while a slideshow or video slideshow is
// running. Buttons: Play/Pause, Stop, Skip Region, Skip Color Theme.
// A Hide checkbox at the right collapses the panel down to just the
// checkbox itself — re-check to expand back. Used so the panel can be
// shrunk out of the way without losing the ability to re-show it.
//
// • Child of _renderPanel, anchored Bottom-Center so it stays put as the
//   render panel resizes.
// • Pure event surface — MainForm wires Click events to the existing
//   slideshow control methods (SkipSlideshowRegion / SkipSlideshowTheme /
//   ToggleSlideshowPause / StopSlideshow), so the panel owns no state
//   beyond which buttons are enabled and the Play/Pause label.
// • Skip-Theme is disabled while a video slideshow runs (legs already pick a
//   single theme each) — SetSkipThemeEnabled toggles it at start.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace FracturingFog.Views;

public sealed class SlideshowVcrPanel : Control
{
    private const int PanelWFull = 410;
    private const int PanelH = 52;
    private const int PanelWCollapsed = 76;
    private const int BtnW = 60;
    private const int BtnH = 36;
    private const int Gap = 6;
    private const int HideW = 64;

    private readonly Button _btnPlayPause;
    private readonly Button _btnStop;
    private readonly Button _btnSkipRegion;
    private readonly Button _btnSkipTheme;
    private readonly CheckBox _chkHide;

    public event EventHandler? PlayPauseClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? SkipRegionClicked;
    public event EventHandler? SkipThemeClicked;
    /// <summary>Fires after the panel's Size changes due to a Hide toggle.</summary>
    public event EventHandler? CollapsedChanged;

    public bool IsCollapsed => _chkHide.Checked;

    public SlideshowVcrPanel()
    {
        Width = PanelWFull;
        Height = PanelH;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.Gainsboro;

        _btnPlayPause  = MakeButton("⏸ Pause");
        _btnStop       = MakeButton("■ Stop");
        _btnSkipRegion = MakeButton("⏭ Region");
        _btnSkipTheme  = MakeButton("⏭ Color");

        _chkHide = new CheckBox
        {
            Text = "Hide",
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 56),
            ForeColor = Color.Gainsboro,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Bold),
            TabStop = false,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _chkHide.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 100);
        _chkHide.FlatAppearance.BorderSize = 1;
        _chkHide.FlatAppearance.CheckedBackColor = Color.FromArgb(70, 50, 30);
        _chkHide.CheckedChanged += (s, e) => ApplyCollapsed();

        _btnPlayPause.Click   += (s, e) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);
        _btnStop.Click        += (s, e) => StopClicked?.Invoke(this, EventArgs.Empty);
        _btnSkipRegion.Click  += (s, e) => SkipRegionClicked?.Invoke(this, EventArgs.Empty);
        _btnSkipTheme.Click   += (s, e) => SkipThemeClicked?.Invoke(this, EventArgs.Empty);

        Controls.Add(_btnPlayPause);
        Controls.Add(_btnStop);
        Controls.Add(_btnSkipRegion);
        Controls.Add(_btnSkipTheme);
        Controls.Add(_chkHide);

        LayoutChildren();
    }

    private void LayoutChildren()
    {
        int y = (PanelH - BtnH) / 2;
        int x = Gap;
        _btnPlayPause.SetBounds(x, y, BtnW, BtnH);     x += BtnW + Gap;
        _btnStop.SetBounds(x, y, BtnW, BtnH);          x += BtnW + Gap;
        _btnSkipRegion.SetBounds(x, y, BtnW + 14, BtnH); x += BtnW + 14 + Gap;
        _btnSkipTheme.SetBounds(x, y, BtnW + 14, BtnH);  x += BtnW + 14 + Gap;
        // Hide checkbox sits at the right of the full panel; in collapsed
        // mode it slides left to hug the left edge.
        _chkHide.SetBounds(IsCollapsed ? Gap : x, y, HideW, BtnH);
    }

    private void ApplyCollapsed()
    {
        bool collapsed = _chkHide.Checked;
        _btnPlayPause.Visible  = !collapsed;
        _btnStop.Visible       = !collapsed;
        _btnSkipRegion.Visible = !collapsed;
        _btnSkipTheme.Visible  = !collapsed;
        Width = collapsed ? PanelWCollapsed : PanelWFull;
        _chkHide.Text = collapsed ? "Show" : "Hide";
        LayoutChildren();
        Invalidate();
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPaused(bool paused)
    {
        _btnPlayPause.Text = paused ? "▶ Play" : "⏸ Pause";
        _btnPlayPause.BackColor = paused
            ? Color.FromArgb(40, 70, 40)
            : Color.FromArgb(48, 48, 56);
    }

    public void SetSkipThemeEnabled(bool enabled)   => _btnSkipTheme.Enabled  = enabled;
    public void SetSkipRegionEnabled(bool enabled)  => _btnSkipRegion.Enabled = enabled;
    public void SetPauseEnabled(bool enabled)       => _btnPlayPause.Enabled  = enabled;

    private static Button MakeButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 56),
            ForeColor = Color.Gainsboro,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Bold),
            TabStop = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 100);
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(90, 90, 100));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
