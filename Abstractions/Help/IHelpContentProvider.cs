// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

    /// <summary>
    /// Wave 5.4 — Math help two-level grouping. A group bundles a family of
    /// related sub-tabs (e.g. all 2D escape-time families) under one outer
    /// tab so the strip stops wrapping past ~30 entries. The FloatingHelpView
    /// renders an outer TabControl over groups and an inner TabControl over
    /// each group's sub-tabs.
    /// </summary>
    public sealed record HelpSubTabGroup(string Title, IReadOnlyList<HelpSubTab> SubTabs);

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
        string BatchText { get; }
        string AudioText { get; }
        string EditorText { get; }
        string BioText { get; }

        /// <summary>Phase 3 — client/server walkthrough: cert setup, master
        /// password, connection save, render preset, remote batch, common
        /// errors, security notes. Static text from
        /// <see cref="HelpTextBundle.ClientServerText"/>.</summary>
        string ClientServerText { get; }

        /// <summary>CalcGen (User Equation editor) authoring reference —
        /// grammar, gating rules, example gallery, troubleshooting.</summary>
        string CalcGenText { get; }

        /// <summary>ColorGen (algorithmic colour theme editor) authoring
        /// reference — DSL syntax, inputs, functions, example gallery,
        /// troubleshooting.</summary>
        string ColorGenText { get; }

        /// <summary>Reference for the Avalonia MainWindow top toolbar — every
        /// combo, toggle button, and status-bar element.</summary>
        string ToolbarText { get; }

        /// <summary>Regions (coordinate bookmarks) reference — built-in vs
        /// user, save / apply, JSON schema, sort + filter.</summary>
        string RegionsText { get; }

        /// <summary>Slideshow + single-shot Video Zoom reference — timings,
        /// audio-reactive mode, VCR controls, recording presets.</summary>
        string SlideshowText { get; }

        /// <summary>Server Admin dialog reference — limits, TLS hardening,
        /// rate limit, cert paths, stale sweep, lifecycle controls.</summary>
        string ServerAdminText { get; }

        /// <summary>Poster (multi-tile print-resolution capture) reference —
        /// dialog options, workflow, remote poster via Client dialog.</summary>
        string PosterText { get; }

        /// <summary>Module-by-module architecture overview for contributors —
        /// solution layout, build, entry points, see-also doc list.</summary>
        string ArchitectureText { get; }

        /// <summary>Math tab is itself a TabControl — each entry becomes one
        /// sub-tab. Order is preserved. Kept for back-compat with any host
        /// that still consumes the flat list; the Avalonia shell binds to
        /// <see cref="MathSubTabGroups"/>.</summary>
        IReadOnlyList<HelpSubTab> MathSubTabs { get; }

        /// <summary>
        /// Wave 5.4 — two-level grouping. Default impl wraps
        /// <see cref="MathSubTabs"/> into a single "All" group so legacy
        /// providers stay compatible. Hosts override to publish a real
        /// family-grouped layout.
        /// </summary>
        IReadOnlyList<HelpSubTabGroup> MathSubTabGroups
            => new[] { new HelpSubTabGroup("All", MathSubTabs) };

        /// <summary>Clickable links rendered under the About body. Host
        /// chooses what to launch; the VM simply raises a LinkRequested
        /// event with the URL.</summary>
        IReadOnlyList<HelpLink> AboutLinks { get; }

        /// <summary>Hardware tab body. Recomputed on every Refresh click so
        /// the user can hot-plug a GPU and see the new state.</summary>
        string GetSystemInfoText();
    }
}
