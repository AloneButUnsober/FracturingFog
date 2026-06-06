using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Shared base for Avalonia view models. Wraps ReactiveObject so derived
/// classes can use <c>this.RaiseAndSetIfChanged</c> for observable property
/// plumbing. Kept deliberately empty otherwise — Phase 2 view models stay
/// thin POCOs over the existing FracturingFog.Models DTOs.
/// </summary>
public abstract class ViewModelBase : ReactiveObject
{
}
