// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/SplashWindow.axaml.cs
//
// Code-behind for the startup splash. Kept deliberately dumb: it reads the
// entry-assembly version for the footer, offers a SetStatus() setter the host
// can drive as bootstrap progresses, and a FadeOutAndClose() that plays the
// Opacity transition (declared in the AXAML) before closing. All dismissal
// timing lives in App — the splash never reaches back into the shell.

using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace FracturingFog.UI.Avalonia.Views;

public sealed partial class SplashWindow : Window
{
    private TextBlock? _statusText;
    private bool _closing;

    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _statusText = this.FindControl<TextBlock>("StatusText");

        var version = this.FindControl<TextBlock>("VersionText");
        if (version != null)
            version.Text = ResolveVersion();
    }

    /// <summary>Update the splash status line (e.g. "Loading themes…"). Safe to
    /// call from any thread; marshals to the UI thread.</summary>
    public void SetStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (_statusText != null) _statusText.Text = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() => { if (_statusText != null) _statusText.Text = text; });
        }
    }

    /// <summary>Fade the splash out over the AXAML Opacity transition, then close.
    /// Idempotent — a second call while a fade is already in flight is ignored.</summary>
    public void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;

        // Drop opacity to 0 to trigger the DoubleTransition, then close once the
        // ~250 ms animation has had time to run. A guard timer closes even if the
        // transition never fires (transparency unsupported on some platforms).
        Opacity = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { Close(); } catch { /* already closed */ }
        };
        timer.Start();
    }

    private static string ResolveVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip the "+<git hash>" build-metadata suffix SourceLink appends.
                int plus = info.IndexOf('+');
                if (plus > 0) info = info.Substring(0, plus);
                return "v" + info;
            }
            var ver = asm.GetName().Version;
            return ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        }
        catch
        {
            return "";
        }
    }
}
