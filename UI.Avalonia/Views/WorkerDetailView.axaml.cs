// Views/WorkerDetailView.axaml.cs
// Code-behind mirrors JobDetailView: start the 5 s VM poll on Open, stop
// on Closed, cancel OS-close so the shell's IsWorkerDetailVisible flag
// stays authoritative.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class WorkerDetailView : Window
{
    public WorkerDetailView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is WorkerDetailViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
            Closed += (_, _) => vm.StopPolling();
        }
    }
}
