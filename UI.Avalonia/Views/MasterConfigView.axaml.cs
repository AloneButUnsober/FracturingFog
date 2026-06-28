// Views/MasterConfigView.axaml.cs
// D-5e. No background polling — values change rarely and a timer would
// clobber an in-progress edit. Triggers one Load on Open so the form
// reflects what the master is currently running; subsequent refreshes
// are operator-driven via the Load button.

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public partial class MasterConfigView : Window
{
    public MasterConfigView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDcChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDcChanged(object? sender, EventArgs e)
    {
        if (DataContext is MasterConfigViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            Opened += (_, _) => _ = vm.LoadAsync();
        }
    }
}
