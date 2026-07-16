// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Standalone Lighting & FX panel. Bound to the same
/// <see cref="FractalParamsViewModel"/> as <see cref="FractalParamsView"/> so
/// every knob still routes through the existing LightingFxData partial — this
/// view only hosts the controls. Hybrid-shell: a UserControl opened modeless
/// from FractalParamsView (wrapped in a PanelHostWindow) so the Params panel
/// stays compact.
/// </summary>
public sealed partial class LightingFxDialog : UserControl
{
    // Yellow per user-colorblindness memory (#FFCC00). See FractalParamsView
    // code-behind for the same brushes — duplicated here so the dialog can
    // be opened independently of that view.
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    private static readonly IBrush OkBrush    = Brushes.LightGreen;
    private static readonly IBrush MutedBrush = Brushes.Gray;

    public LightingFxDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    /// <summary>Browse… handler for the HDRI preset / file row. Same flow as
    /// the original FractalParamsView handler — kept here so the dialog
    /// owns its file-picker UX end-to-end.</summary>
    private async void OnBrowseHdriClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var status = this.FindControl<TextBlock>("HdriStatus");

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick HDRI environment map",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("HDRI files")
                {
                    Patterns = new[] { "*.hdr", "*.pic", "*.exr" },
                    AppleUniformTypeIdentifiers = new[] { "public.image" },
                    MimeTypes = new[] { "image/vnd.radiance", "image/x-exr" },
                },
                new("Radiance HDR") { Patterns = new[] { "*.hdr", "*.pic" } },
                new("OpenEXR")      { Patterns = new[] { "*.exr" } },
                new("All files")    { Patterns = new[] { "*" } },
            },
        });
        if (picked is not { Count: > 0 }) return;

        string path = picked[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus(status, "Picker returned no usable path.", ErrorBrush);
            return;
        }

        var probe = HdriProbe.TryLoad;
        bool? loaded = probe == null ? (bool?)null : await Task.Run(() => probe(path));

        if (loaded == false)
            vm.EnvironmentName = path;
        else
            vm.ApplyHdriPick(path);

        if (loaded == true)
        {
            string fileName = Path.GetFileName(path);
            SetStatus(status, $"Loaded {fileName} — SkyMode = HDRI, IBL armed.", OkBrush);
        }
        else if (loaded == false)
        {
            SetStatus(status,
                "Failed to load HDRI (unsupported format / compression?). Falling back to gradient sky.",
                ErrorBrush);
        }
        else
        {
            SetStatus(status, "Path set; engine will load on next render.", MutedBrush);
        }
    }

    private static void SetStatus(TextBlock? status, string text, IBrush brush)
    {
        if (status == null) return;
        status.Text = text;
        status.Foreground = brush;
    }
}
