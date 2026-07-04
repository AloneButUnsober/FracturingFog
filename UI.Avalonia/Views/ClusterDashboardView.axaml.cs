// Views/ClusterDashboardView.axaml.cs
// Code-behind. Mirrors ServerAdminView: starts the VM poll timer on Open,
// stops it on Close, and cancels the Window-Closing so the shell flag
// (IsClusterDashboardVisible) stays authoritative — a future Show call can
// re-open the same window/VM pair.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ClusterDashboardView : Window
{
    public ClusterDashboardView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClusterDashboardViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }
}
