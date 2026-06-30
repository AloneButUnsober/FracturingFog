namespace FracturingFog.Abstractions.Animation;

/// <summary>
/// A single parameter animator. Integrated by <c>ParameterAnimationBus</c>
/// once per tick. The animator owns its own state and mutates whatever
/// parameter it's bound to via captured refs / closures supplied at
/// construction. <c>Tick</c> must be silent — bus emits a single render
/// trigger after all animators have ticked.
/// </summary>
public interface IParameterAnimator
{
    string Name { get; }
    bool IsEnabled { get; }
    void Tick(double dt);
}
