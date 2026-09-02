// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Assets/AssetSourceRegistry.cs
//
// Central list of every IAssetSource the Asset Manager surfaces, in type-tree
// display order. The Avalonia Asset Manager view (A1) binds to this; keeping
// the roster here means the UI never hard-codes the eight singletons.

using System.Collections.Generic;
using FracturingFog.Abstractions.Assets;

namespace FracturingFog.Assets
{
    public static class AssetSourceRegistry
    {
        /// <summary>All asset sources in left-pane display order. A fresh list
        /// per call — adapters are cheap and stateless.</summary>
        public static IReadOnlyList<IAssetSource> All() => new IAssetSource[]
        {
            new RegionAssetSource(),
            new ColorThemeAssetSource(),
            new AnimationAssetSource(),
            new UserEquationAssetSource(),
            new SandboxEquationAssetSource(),
            new UserBulbAssetSource(),
            new SlideshowConfigAssetSource(),
            new WatermarkAssetSource(),
            new SceneAssetSource(),
            new WorkspaceAssetSource(),
            new LightingFxAssetSource(),
        };
    }
}
