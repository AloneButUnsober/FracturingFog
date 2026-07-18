// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Runtime.CompilerServices;

using FracturingFog.Abstractions;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Test-process data-root isolation.
///
/// Many stores (regions, colour themes, animations, slideshow configs) are
/// process-wide singletons that persist to <c>AppDataPaths.Root</c> —
/// <c>%APPDATA%\FracturingFog</c> by default, i.e. the developer's REAL data.
/// A test that constructs such a singleton and calls <c>Save()</c> serializes
/// its (empty, in test) in-memory list straight over the real file. That is
/// exactly how a <c>regions.json</c> got wiped to <c>[]</c>: the library
/// singleton never runs <c>Load()</c> in a test host, so its list is empty,
/// and <c>Save()</c> overwrites the user's regions.
///
/// The module initializer below runs once, before any test type is touched,
/// and redirects the data root to a throwaway temp directory for THIS process
/// only (no persistent anchor is written — the real root is left untouched).
/// After this, every store reads/writes under the temp dir and can never harm
/// real user data.
/// </summary>
internal static class TestDataRootIsolation
{
    [ModuleInitializer]
    internal static void RedirectDataRoot()
    {
        // Unique per test run so parallel/successive runs don't collide and a
        // crashed run's leftovers never seed the next one.
        string temp = Path.Combine(
            Path.GetTempPath(),
            "FracturingFog.Tests",
            Guid.NewGuid().ToString("N"));
        AppDataPaths.SetProcessRootOverride(temp);
    }
}

/// <summary>
/// Serializes every test class that writes the shared
/// <c>FractalRegionLibrary.Instance</c> (a process-wide singleton persisting
/// to one <c>regions.json</c>). Without this, classes run in parallel and
/// concurrent <c>Save()</c> calls race on the same file — corrupting the
/// atomic-swap backup assertions and each other's state. Also serialises the
/// sibling animation / scene library singletons, which the same classes mutate.
/// Members: <c>RegionEditorServiceTests</c>, <c>AnimationLibrarySaveLoopTests</c>,
/// <c>AssetSourceTests</c>, <c>SceneLibraryTests</c>.
/// </summary>
[CollectionDefinition(FractalRegionLibraryCollection.Name, DisableParallelization = true)]
public sealed class FractalRegionLibraryCollection
{
    public const string Name = "FractalRegionLibrary";
}
