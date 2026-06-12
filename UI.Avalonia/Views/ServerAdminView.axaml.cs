// Views/ServerAdminView.axaml.cs
// Code-behind. Starts the VM's poll timer on Show, stops it on Hide.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ServerAdminView : Window
{
    public ServerAdminView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
        // Closing is handled by MainWindow.SyncServerAdmin: it cancels the
        // close and flips ShellViewModel.IsServerAdminVisible=false. That
        // single source of truth lets a later ShowServerAdmin reopen the
        // window. A View-level Closing+Hide here would hide without flipping
        // the flag, so the next click sees IsServerAdminVisible still true,
        // RaiseAndSetIfChanged sees no change, no PropertyChanged fires, and
        // SyncServerAdmin never runs.
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is ServerAdminViewModel vm)
        {
            // Close() (not Hide()) so MainWindow.SyncServerAdmin's Closing
            // handler intercepts + flips IsServerAdminVisible=false.
            vm.CloseRequested += (_, _) => Close();
            vm.BrowseFolderRequested += async (_, t) => await BrowseFolderAsync(t.kind, t.assign);
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(this,
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
