// Views/JobListView.axaml.cs
// Hybrid-shell: UserControl hosted modeless by MainWindow.SyncJobList. Poll
// lifecycle (host Opened/Closed) + close => hide (VM CloseRequested ->
// IsJobListVisible=false in ShellViewModel) are owned by the host + shell flag.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

public partial class JobListView : UserControl
{
    public JobListView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
