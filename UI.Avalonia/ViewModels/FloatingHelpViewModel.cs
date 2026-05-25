// ViewModels/FloatingHelpViewModel.cs
//
// Avalonia port of the legacy WinForms FloatingHelp window. Content stays
// in the main project behind IHelpContentProvider so UI.Avalonia avoids
// copying ~2,500 lines of static help text. VM exposes:
//   • TitleText           — "<ProgramName> v<Version> — Help"
//   • AboutText / FeaturesText / AudioText / EditorText / BioText
//   • MathSubTabs         — pre-built sub-tab bodies
//   • AboutLinks          — clickable URLs
//   • HardwareText        — live system info; refreshed on demand
// Commands:
//   • CloseCommand   → CloseRequested event
//   • RefreshCommand → re-fetches HardwareText from provider
//   • OpenLinkCommand(HelpLink) → LinkRequested(url) event

using System;
using System.Collections.Generic;
using System.Reactive;
using FracturingFog.Help;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class FloatingHelpViewModel : ViewModelBase
{
    private readonly IHelpContentProvider _content;

    public FloatingHelpViewModel(IHelpContentProvider content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));

        TitleText    = $"{content.ProgramName} v{content.ProgramVersion} — Help";
        AboutText    = content.AboutText;
        FeaturesText = content.FeaturesText;
        AudioText    = content.AudioText;
        EditorText   = content.EditorText;
        BioText      = content.BioText;
        MathSubTabs  = content.MathSubTabs;
        AboutLinks   = content.AboutLinks;
        _hardwareText = content.GetSystemInfoText();

        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand = ReactiveCommand.Create(RefreshHardware);
        OpenLinkCommand= ReactiveCommand.Create<HelpLink>(link =>
        {
            if (link != null && !string.IsNullOrEmpty(link.Url))
                LinkRequested?.Invoke(this, link.Url);
        });
    }

    public string TitleText { get; }
    public string AboutText { get; }
    public string FeaturesText { get; }
    public string AudioText { get; }
    public string EditorText { get; }
    public string BioText { get; }
    public IReadOnlyList<HelpSubTab> MathSubTabs { get; }
    public IReadOnlyList<HelpLink> AboutLinks { get; }

    private string _hardwareText;
    public string HardwareText
    {
        get => _hardwareText;
        private set => this.RaiseAndSetIfChanged(ref _hardwareText, value);
    }

    private void RefreshHardware() => HardwareText = _content.GetSystemInfoText();

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<HelpLink, Unit> OpenLinkCommand { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? LinkRequested;
}
