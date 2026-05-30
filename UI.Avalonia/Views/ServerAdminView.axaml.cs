// Views/ServerAdminView.axaml.cs
// Code-behind. Starts the VM's poll timer on Show, stops it on Hide.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ServerAdminView : Window
{
    public ServerAdminView()
    {
        InitializeComponent();
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
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }
}
