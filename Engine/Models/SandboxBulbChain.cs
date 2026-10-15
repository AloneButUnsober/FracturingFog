// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

                SandboxBulbExpression expr;
                try { expr = SandboxBulbExpression.ParseWithScope(steps[i].Source ?? "z", scope, envSize); }
                catch (SbxParseException ex)
                {
                    // #1102 — name the step so the editor can point at it.
                    throw new SbxParseException($"Step {i + 1} ({outName}): {ex.Message}", ex.Position, ex.Length) { StepIndex = i };
                }
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

        // #1110 — typed compile of the whole chain (each step output a typed
        // local); the interpreter stays the fallback. Bit-identical to it.
        private SbxTypedVecStep? _typedVec;
        private SbxTypedQuatStep? _typedQuat;

        /// <summary>True once the typed compile for that axis mode is in use.</summary>
        public bool IsTypedCompiled(bool quatMode) => quatMode ? _typedQuat != null : _typedVec != null;

        /// <summary>Why the last typed compile fell back (null when it didn't).</summary>
        public string? TypedFallbackReason { get; private set; }

        /// <summary>Nodes of the last typed compile that run on SbxVal3 (see
        /// SandboxBulbExpression.TypedDynamicNodes).</summary>
        public int TypedDynamicNodes { get; private set; }

        // Fallback when a kind can't be inferred: each step on the dynamic #283
        // delegate (bit-identical to the walk, shares SbxFuncEval).
        private Func<SbxVal3[], SbxVal3>[]? _dynamic;

        /// <summary>True when the steps run on the dynamic #283 delegates.</summary>
        public bool IsCompiled => _dynamic != null;

        /// <summary>#1110 — compile the chain for one axis mode: the typed tree
        /// when every kind can be inferred, else each step on the dynamic #283
        /// delegate. True when either is in use afterwards (else the interpreter).</summary>
        public bool TryCompile(bool quatMode)
        {
            if (IsTypedCompiled(quatMode)) return true;
            var d = SandboxBulbTypedCompiler.TryCompile(_stepRoots, _stepOutputSlots, _extraSlots, quatMode, out var why, out int dynNodes);
            TypedFallbackReason = why;
            TypedDynamicNodes = dynNodes;
            if (d is SbxTypedQuatStep q) { _typedQuat = q; return true; }
            if (d is SbxTypedVecStep v) { _typedVec = v; return true; }
            if (_dynamic != null) return true;
            var dyn = new Func<SbxVal3[], SbxVal3>[_stepRoots.Length];
            for (int i = 0; i < dyn.Length; i++)
            {
                var f = SandboxBulbCompiler.TryCompile(_stepRoots[i]);
                if (f == null) return false;
                dyn[i] = f;
            }
            _dynamic = dyn;
            return true;
        }

        private void WriteExtras(SbxVal3[] env, ReadOnlySpan<double> extras)
        {
            for (int i = 0; i < _extraSlots.Length && i < extras.Length; i++)
            {
                int s = _extraSlots[i];
                if (s >= 0) env[s] = SbxVal3.R(extras[i]);
            }
        }

        public Vec3 EvalStep(Vec3 z, Vec3 c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            var typed = _typedVec;
            if (typed != null) { WriteExtras(env, extras); return typed(z, c, n, env); }
            env[SandboxBulbExpression.SlotZ] = SbxVal3.V(z);
            env[SandboxBulbExpression.SlotC] = SbxVal3.V(c);
            env[SandboxBulbExpression.SlotN] = SbxVal3.R(n);
            WriteExtras(env, extras);

            var dyn = _dynamic;
            SbxVal3 last = SbxVal3.V(z);
            for (int i = 0; i < _stepRoots.Length; i++)
            {
                last = dyn != null ? dyn[i](env) : _stepRoots[i].Eval(env);
                env[_stepOutputSlots[i]] = last;
            }
            return last.AsVec();
        }

        public Quat EvalStepQuat(Quat z, Quat c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            var typed = _typedQuat;
            if (typed != null) { WriteExtras(env, extras); return typed(z, c, n, env); }
            env[SandboxBulbExpression.SlotZ] = SbxVal3.Q(z);
            env[SandboxBulbExpression.SlotC] = SbxVal3.Q(c);
            env[SandboxBulbExpression.SlotN] = SbxVal3.R(n);
            WriteExtras(env, extras);

            var dyn = _dynamic;
            SbxVal3 last = SbxVal3.Q(z);
            for (int i = 0; i < _stepRoots.Length; i++)
            {
                last = dyn != null ? dyn[i](env) : _stepRoots[i].Eval(env);
                env[_stepOutputSlots[i]] = last;
            }
            return last.AsQuat();
        }
    }
}
