// Views/WorkerDetailView.axaml.cs
// Hybrid-shell: UserControl hosted modeless by MainWindow.SyncWorkerDetail. Poll
// lifecycle (host Opened/Closed) + close => hide (VM CloseRequested ->
// IsWorkerDetailVisible=false in ShellViewModel) are owned by the host + shell
// flag. Single-instance VM: the WorkerId setter clears state + immediate-polls.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

public partial class WorkerDetailView : UserControl
{
    public WorkerDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
