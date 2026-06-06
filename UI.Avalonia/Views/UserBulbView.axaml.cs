using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>UserBulbDialog</c>. Modeless 3D bulb editor. Host
/// wires the VM's events: CompileRequested, RenderRequested, PromotionChanged,
/// NamePromptRequested, ConfirmDeleteRequested, OpenFilePromptRequested,
/// SaveFilePromptRequested, MessageRequested, ExportMeshRequested. Host
/// also drives <see cref="ViewModels.UserBulbViewModel.AnimationTick"/>
/// from its own 30 Hz timer while IsPlaying is true and calls
/// <see cref="ViewModels.UserBulbViewModel.NotifyRenderDone"/> when a frame
/// finishes uploading.
/// </summary>
public sealed partial class UserBulbView : Window
{
    public UserBulbView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
