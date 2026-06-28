// Views/JobListView.axaml.cs
// Code-behind for the cluster job list. Same lifetime pattern as
// ClusterDashboardView: start the 10 s VM poll on Opened, stop on Closed,
// cancel the OS close so the shell flag drives visibility.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class JobListView : Window
{
    public JobListView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is JobListViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }
}
