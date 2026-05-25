// Abstractions/Help/IHelpContentProvider.cs
//
// Bridge between the Avalonia FloatingHelp VM and the host's help-text
// builders. The legacy WinForms FloatingHelp.cs hard-codes ~2,500 lines of
// verbatim help text inside C# string literals plus live DXGI / D3D11
// enumeration for the Hardware tab. To avoid copying all of that into
// UI.Avalonia, the host implements this provider and reuses its existing
// builders; the VM stays a thin renderer of tab text + links.

using System.Collections.Generic;

namespace FracturingFog.Help
{
    /// <summary>One sub-tab inside the Mathematics section.</summary>
    public sealed record HelpSubTab(string Title, string Body);

    /// <summary>One clickable link in the About tab footer.</summary>
    public sealed record HelpLink(string Label, string Url);

    /// <summary>
    /// Host-provided content for every tab of the FloatingHelp window. The
    /// VM reads these properties at construction time and re-reads
    /// <see cref="GetSystemInfoText"/> when the user hits Refresh.
    /// </summary>
    public interface IHelpContentProvider
    {
        string ProgramName { get; }
        string ProgramVersion { get; }

        string AboutText { get; }
        string FeaturesText { get; }
        string AudioText { get; }
        string EditorText { get; }
        string BioText { get; }

        /// <summary>Math tab is itself a TabControl — each entry becomes one
        /// sub-tab. Order is preserved.</summary>
        IReadOnlyList<HelpSubTab> MathSubTabs { get; }

        /// <summary>Clickable links rendered under the About body. Host
        /// chooses what to launch; the VM simply raises a LinkRequested
        /// event with the URL.</summary>
        IReadOnlyList<HelpLink> AboutLinks { get; }

        /// <summary>Hardware tab body. Recomputed on every Refresh click so
        /// the user can hot-plug a GPU and see the new state.</summary>
        string GetSystemInfoText();
    }
}
