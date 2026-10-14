// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using PaletteBuilder.Views;

namespace PaletteBuilder;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        InstallDefaultWindowIcon("avares://PaletteBuilder/Resources/PaletteBuilder_Logo.ico");   // #429
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>#429 — on Linux (and macOS) a window shows no icon unless one is
    /// set: the exe's ApplicationIcon is a Windows-only resource. An application
    /// style gives every window (and Window subclass) the app icon, so the main
    /// window and every dialog get it; a window that sets its own icon keeps it.
    /// (Window.IconProperty.OverrideDefaultValue throws in Avalonia 12 — Window
    /// already owns that metadata.) Windows keeps using the exe icon, as before.</summary>
    private void InstallDefaultWindowIcon(string assetUri)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            using var asset = AssetLoader.Open(new Uri(assetUri));
            var copy = new MemoryStream();
            asset.CopyTo(copy);
            copy.Position = 0;
            var icon = new WindowIcon(copy);
            Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Window icon not set: {ex.Message}");
        }
    }
}
