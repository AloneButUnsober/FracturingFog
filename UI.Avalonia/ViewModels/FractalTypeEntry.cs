// ViewModels/FractalTypeEntry.cs
//
// Single combo-box entry in the toolbar Type dropdown. Wraps either a
// built-in FractalType, the non-selectable "— Registered —" divider, or
// a promoted user-equation pulled from RegisteredFractalCatalog. Mirrors
// the WinForms PopulateFractalTypeCombo layout.

using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class FractalTypeEntry
{
    public string Label { get; init; } = string.Empty;
    public bool IsDivider { get; init; }
    public FractalType Type { get; init; }
    public RegisteredFractal? Promoted { get; init; }

    public override string ToString() => Label;

    public static FractalTypeEntry BuiltIn(FractalType type, string label)
        => new() { Label = label, Type = type };

    public static FractalTypeEntry Divider(string label = "— Registered —")
        => new() { Label = label, IsDivider = true };

    public static FractalTypeEntry FromPromoted(RegisteredFractal r)
        => new() { Label = r.Name, Type = r.Type, Promoted = r };
}
