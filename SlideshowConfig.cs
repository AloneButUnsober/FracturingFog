using System;
using System.Windows.Forms;
using FracturingFog.Models;

namespace FracturingFog
{
    /// <summary>
    /// MainForm partial that owns persisted slideshow user configuration:
    /// timing values (used when audio-reactive is OFF), extreme-region toggle,
    /// and the entry point for the SlideshowSettingsDialog.
    /// </summary>
    public sealed partial class MainForm
    {
        private SlideshowSettings _slideshowSettings = SlideshowSettingsStore.Load();
        private Views.SlideshowSettingsDialog? _slideshowDialog;

        /// <summary>Public read-only access for the slideshow loop.</summary>
        public SlideshowSettings SlideshowSettings => _slideshowSettings;

        /// <summary>Apply persisted settings to dependent state at startup.</summary>
        public void InitializeSlideshowFromDisk()
        {
            FractalRegionLibrary.Instance.IncludeExtremeInAll = _slideshowSettings.UseExtremeRegions;
        }

        private void ShowSlideshowSettingsDialog()
        {
            if (_slideshowDialog != null && !_slideshowDialog.IsDisposed)
            {
                if (_slideshowDialog.WindowState == FormWindowState.Minimized)
                    _slideshowDialog.WindowState = FormWindowState.Normal;
                _slideshowDialog.BringToFront();
                _slideshowDialog.Activate();
                return;
            }

            var dlg = new Views.SlideshowSettingsDialog(
                _slideshowSettings,
                _audioSettings.Enabled,
                ShowAudioSettingsDialog);
            _slideshowDialog = dlg;
            dlg.FormClosed += (s, e) =>
            {
                try
                {
                    if (dlg.DialogResult == DialogResult.OK)
                    {
                        ApplySlideshowSettings(dlg.Result);
                        SlideshowSettingsStore.Save(_slideshowSettings);

                        // Sync audio-reactive master toggle to AudioSettings.Enabled.
                        if (dlg.AudioReactiveResult != _audioSettings.Enabled)
                            SetAudioReactiveEnabled(dlg.AudioReactiveResult);
                    }
                }
                finally
                {
                    if (ReferenceEquals(_slideshowDialog, dlg)) _slideshowDialog = null;
                    dlg.Dispose();
                }
            };
            dlg.Show(this);
        }

        private void ApplySlideshowSettings(SlideshowSettings updated)
        {
            _slideshowSettings.UseExtremeRegions = updated.UseExtremeRegions;
            _slideshowSettings.TotalDisplayMsPerRegion =
                System.Math.Max(1_000, updated.TotalDisplayMsPerRegion);
            _slideshowSettings.ColorThemeFadeMs =
                System.Math.Max(50, updated.ColorThemeFadeMs);
            _slideshowSettings.RegionFadeMs =
                System.Math.Max(50, updated.RegionFadeMs);
            _slideshowSettings.FadeSteps =
                System.Math.Clamp(updated.FadeSteps, 2, 200);

            FractalRegionLibrary.Instance.IncludeExtremeInAll = _slideshowSettings.UseExtremeRegions;
        }
    }
}
