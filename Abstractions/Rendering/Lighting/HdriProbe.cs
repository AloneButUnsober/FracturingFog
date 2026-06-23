// HdriProbe.cs
//
// Tiny indirection so the Avalonia UI shell can eagerly pre-warm an HDRI
// load on Browse… without taking a project reference to FracturingFog.Engine
// (where HdriRegistry / OpenExrReader live). The Engine layer populates the
// delegate at startup; the UI layer invokes it through this surface. If the
// delegate is null (engine not loaded, e.g. unit tests, or load happens
// before engine bootstrap), callers should treat the probe as a no-op and
// defer load surfacing to the next render frame.
//
// Why a delegate field and not an interface?
//   The UI -> Engine wiring crosses an architectural boundary the rest of
//   the codebase intentionally guards against. A two-method interface plus
//   DI container churn was too much ceremony for one Browse… handler.
//   A single static field that Engine sets once at bootstrap (alongside
//   HdriRegistry's other static state) keeps the cost proportional to the
//   feature.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>UI-facing probe surface for the HDRI loader. Populated by the
/// engine layer during host bootstrap so the Avalonia file-picker can pre-
/// warm and surface load failures without referencing the engine.</summary>
public static class HdriProbe
{
    /// <summary>Returns <c>true</c> if the file at <paramref name="path"/>
    /// decoded successfully. Null when the engine has not yet wired itself
    /// up — callers should treat that as "load result unknown" and skip the
    /// status update rather than display a misleading error.</summary>
    public static Func<string, bool>? TryLoad;

    /// <summary>Fire-and-forget background preload of an HDRI by path. Wired
    /// to <c>Task.Run(HdriRegistry.TryLoadFromFile)</c> by the engine static
    /// ctor. UI/VM setters call this on <c>EnvironmentName</c> changes so the
    /// first render frame finds the HDRI already cached instead of N render
    /// threads racing to parse the same file. Null when engine not loaded
    /// (tests / pre-bootstrap) — callers treat as no-op.</summary>
    public static Action<string>? Preload;
}
