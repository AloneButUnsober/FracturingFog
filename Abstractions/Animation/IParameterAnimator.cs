// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

    /// <summary>Rough per-frame cost class of the parameter this animator
    /// drives. Consumed by the animated-param ceiling (Animation Roadmap
    /// Phase 6) to drop the most expensive tracks first when the enabled
    /// track count exceeds the ceiling. Defaults to
    /// <see cref="AnimatableParamCost.Cheap"/> for animators that don't
    /// resolve a cost (e.g. the Julia c orbit).</summary>
    AnimatableParamCost Cost => AnimatableParamCost.Cheap;
}
