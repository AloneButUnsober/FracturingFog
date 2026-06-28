// Views/JobDetailView.axaml.cs
// Code-behind mirrors ClusterDashboardView: start the 2 s VM poll on
// Open, stop on Closed, cancel OS-close so the shell's
// IsJobDetailVisible flag stays authoritative.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class JobDetailView : Window
{
    public JobDetailView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is JobDetailViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }
}
