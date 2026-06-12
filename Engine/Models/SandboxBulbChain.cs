// Models/SandboxBulbChain.cs
//
// Multi-step Sandbox-Bulb expression. Parallel to the Roslyn chain path in
// UserBulbCalculator (WrapUserSourceChain). Each step is its own DSL
// expression returning Vec3. Step N can reference the output of any prior
// step by name — those names are registered as Vec3 slots in the shared
// scope before parsing step N.
//
// Built-in identifiers per step:
//   z, c (Vec3), n (Real), pi, e   — same as SandboxBulbExpression
//   <param-name>, t                 — scalar extras
//   <prior-step-name>               — Vec3, filled before step N runs

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    public sealed class SandboxBulbChain
    {
        private readonly Sbx3Node[] _stepRoots;
        private readonly int[] _stepOutputSlots;
        private readonly int[] _extraSlots;
        public int EnvSize { get; }

        private SandboxBulbChain(Sbx3Node[] roots, int[] outSlots, int[] extras, int envSize)
        { _stepRoots = roots; _stepOutputSlots = outSlots; _extraSlots = extras; EnvSize = envSize; }

        /// <summary>Read-only view of the per-step AST roots. Exposed so the
        /// AnalyticDE pattern matcher can walk the chain in compile order.</summary>
        public IReadOnlyList<Sbx3Node> StepRoots => _stepRoots;

        /// <summary>Output slot index for step i. Aligns with <see cref="StepRoots"/>.</summary>
        public IReadOnlyList<int> StepOutputSlots => _stepOutputSlots;

        public static SandboxBulbChain Parse(IReadOnlyList<UserBulbChainStep> steps, IReadOnlyList<string> extraScalarNames)
        {
            if (steps == null || steps.Count == 0)
                throw new FormatException("Chain must have at least one step.");

            var scope = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["z"] = SandboxBulbExpression.SlotZ,
                ["c"] = SandboxBulbExpression.SlotC,
                ["n"] = SandboxBulbExpression.SlotN,
            };
            int envSize = SandboxBulbExpression.ReservedSlots;

            int extraCount = extraScalarNames?.Count ?? 0;
            var extraSlots = new int[extraCount];
            for (int i = 0; i < extraCount; i++)
            {
                string nm = extraScalarNames![i] ?? string.Empty;
                if (string.IsNullOrEmpty(nm)) { extraSlots[i] = -1; continue; }
                if (scope.ContainsKey(nm)) throw new FormatException($"Duplicate extra '{nm}'");
                int s = envSize++;
                scope[nm] = s;
                extraSlots[i] = s;
            }

            int stepCount = steps.Count;
            var roots = new Sbx3Node[stepCount];
            var outSlots = new int[stepCount];

            for (int i = 0; i < stepCount; i++)
            {
                string outName = string.IsNullOrWhiteSpace(steps[i].OutputName) ? $"step{i}" : steps[i].OutputName;
                if (outName is "z" or "c" or "n" or "pi" or "e" or "let" or "in")
                    throw new FormatException($"Step {i} output name '{outName}' is reserved.");

                var expr = SandboxBulbExpression.ParseWithScope(steps[i].Source ?? "z", scope, envSize);
                roots[i] = expr.Root;
                envSize = expr.EnvSize;

                int slot = envSize++;
                if (!scope.TryAdd(outName, slot))
                    throw new FormatException($"Duplicate step output name '{outName}'.");
                outSlots[i] = slot;
            }

            return new SandboxBulbChain(roots, outSlots, extraSlots, envSize);
        }

        public SbxVal3[] NewEnv() => new SbxVal3[EnvSize];

        public Vec3 EvalStep(Vec3 z, Vec3 c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            env[SandboxBulbExpression.SlotZ] = SbxVal3.V(z);
            env[SandboxBulbExpression.SlotC] = SbxVal3.V(c);
            env[SandboxBulbExpression.SlotN] = SbxVal3.R(n);
            for (int i = 0; i < _extraSlots.Length && i < extras.Length; i++)
            {
                int s = _extraSlots[i];
                if (s >= 0) env[s] = SbxVal3.R(extras[i]);
            }

            SbxVal3 last = SbxVal3.V(z);
            for (int i = 0; i < _stepRoots.Length; i++)
            {
                last = _stepRoots[i].Eval(env);
                env[_stepOutputSlots[i]] = last;
            }
            return last.AsVec();
        }

        public Quat EvalStepQuat(Quat z, Quat c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            env[SandboxBulbExpression.SlotZ] = SbxVal3.Q(z);
            env[SandboxBulbExpression.SlotC] = SbxVal3.Q(c);
            env[SandboxBulbExpression.SlotN] = SbxVal3.R(n);
            for (int i = 0; i < _extraSlots.Length && i < extras.Length; i++)
            {
                int s = _extraSlots[i];
                if (s >= 0) env[s] = SbxVal3.R(extras[i]);
            }

            SbxVal3 last = SbxVal3.Q(z);
            for (int i = 0; i < _stepRoots.Length; i++)
            {
                last = _stepRoots[i].Eval(env);
                env[_stepOutputSlots[i]] = last;
            }
            return last.AsQuat();
        }
    }
}
