using System;

using Avalonia.Controls;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>
    /// Options controlling how a <see cref="PanelHostWindow"/> frames its panel.
    /// Carries the per-dialog window chrome (title, size, background) that used
    /// to live on each view's <c>&lt;Window&gt;</c> root before the view became
    /// a <c>UserControl</c>.
    /// </summary>
    public sealed record PanelHostOptions(
        string Title,
        double Width = double.NaN,
        double MinWidth = double.NaN,
        double Height = double.NaN,
        double MinHeight = double.NaN,
        bool SizeToContentHeight = true,
        bool CanResize = false,
        bool ShowInTaskbar = false,
        WindowStartupLocation StartupLocation = WindowStartupLocation.Manual,
        IBrush? Background = null);

    /// <summary>
    /// Generic pop-out host for a feature panel (<c>UserControl</c>). Owns the
    /// window chrome, wires the panel's <see cref="IClosableDialog"/> VM to the
    /// window's close, and attaches the shared Esc-to-close behavior — the
    /// responsibilities each view used to carry itself when it was a
    /// <c>Window</c>. Show it via <see cref="WindowService.ShowPanelDialogAsync"/>
    /// so it gets the standard placement + screen-fit treatment.
    /// </summary>
    public sealed class PanelHostWindow : Window
    {
        private IClosableDialog? _boundVm;

        /// <summary>True when the panel VM requested close with success, false
        /// on cancel, null when dismissed via the window X / Esc.</summary>
        public bool? DialogResult { get; private set; }

        public PanelHostWindow(Control panel, PanelHostOptions opts)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));
            if (opts == null) throw new ArgumentNullException(nameof(opts));

            Title = opts.Title;
            ShowInTaskbar = opts.ShowInTaskbar;
            WindowStartupLocation = opts.StartupLocation;
            CanResize = opts.CanResize;
            SizeToContent = opts.SizeToContentHeight ? SizeToContent.Height : SizeToContent.Manual;
            if (!double.IsNaN(opts.Width)) Width = opts.Width;
            if (!double.IsNaN(opts.MinWidth)) MinWidth = opts.MinWidth;
            if (!double.IsNaN(opts.Height)) Height = opts.Height;
            if (!double.IsNaN(opts.MinHeight)) MinHeight = opts.MinHeight;
            Background = opts.Background ?? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));

            Content = panel;
            EscapeCloseBehavior.Attach(this);

            // The close signal comes from an IClosableDialog. Most views expose
            // it on their VM (DataContext); some (code-behind close via Click
            // handlers) expose it on the control itself. Prefer the control,
            // fall back to the DataContext, and rebind if the DataContext is
            // assigned later.
            _panel = panel;
            Bind(panel as IClosableDialog ?? panel.DataContext as IClosableDialog);
            panel.DataContextChanged += (_, _) =>
            {
                if (_boundVm == null)
                    Bind(_panel as IClosableDialog ?? _panel.DataContext as IClosableDialog);
            };
        }

        private Control? _panel;

        private void Bind(IClosableDialog? closable)
        {
            if (ReferenceEquals(_boundVm, closable)) return;
            if (_boundVm != null) _boundVm.CloseRequested -= OnCloseRequested;
            _boundVm = closable;
            if (_boundVm != null) _boundVm.CloseRequested += OnCloseRequested;
        }

        private void OnCloseRequested(object? sender, bool result)
        {
            DialogResult = result;
            Close();
        }
    }
}
