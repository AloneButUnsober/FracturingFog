// Views/ServerAdminView.axaml.cs
// Hybrid-shell: UserControl hosted modeless by MainWindow.SyncServerAdmin. The
// VM poll lifecycle (start on host Opened, stop on host Closed) and close =>
// hide (VM CloseRequested -> IsServerAdminVisible=false, wired in ShellViewModel)
// are owned by the host + shell flag. This view keeps only the folder-browse
// and Help interactions that need the live TopLevel.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ServerAdminView : UserControl
{
    public ServerAdminView()
    {
        InitializeComponent();
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is ServerAdminViewModel vm)
        {
            vm.BrowseFolderRequested += async (_, t) => await BrowseFolderAsync(t.kind, t.assign);
        }
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/ServerAdmin-Guide.md",
            null,
            "Server Admin — Help");

    private async Task BrowseFolderAsync(string kind, Action<string> assign)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        string title = kind switch
        {
            "certsDir" => "Pick server certs directory",
            "logDir"   => "Pick server logs directory",
            "workDir"  => "Pick server work directory",
            _          => "Pick directory",
        };
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        if (picked is { Count: > 0 })
            assign(picked[0].Path.LocalPath);
    }
}
