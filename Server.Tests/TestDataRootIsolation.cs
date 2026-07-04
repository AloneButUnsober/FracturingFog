using System;
using System.IO;
using System.Runtime.CompilerServices;

using FracturingFog.Abstractions;

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
