// UserBulbSandboxEmitter.cs
//
// Walks a parsed Sandbox-Bulb AST (Sbx3Node) and emits an equivalent C#
// expression body that can be fed back through the Roslyn-based UserBulb
// compile pipeline. This is the foundation for ILGPU stage-2 — once the
// emitter is in place, Sandbox sources can ride the same JIT path that
// existing Roslyn sources use for the GPU backend.
//
// The emitter is type-aware: each sub-tree is tagged as Real / Vec / Quat
// at emit time so the produced C# uses the correct operator overloads
// (scalar broadcast, Vec3 Hadamard via Vec3.Mul, triplex via Vec3.Pow,
// Hamilton via Quat.operator*).
//
// The output references only: Vec3, Quat, Math, double literals, and the
// __p[] param array — the exact surface UserBulbCalculator.WrapUserSource
// already exposes. No additional helpers needed on the consumption side.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using FracturingFog.Models;

namespace FracturingFog.Calculators;

public enum SbxEmitKind { Real, Vec, Quat }

public sealed record SbxEmitResult(bool Ok, string? Error, string? Body, SbxEmitKind ResultKind);

public static class UserBulbSandboxEmitter
{
    /// <summary>Emit a C# expression body from a Sandbox AST. <paramref name="paramNames"/>
    /// maps trailing scalar slots to <c>__p[i]</c> identifiers; <c>t</c>
    /// (animation time) is at <c>__p[Length - 1]</c>, matching
    /// <c>UserBulbCalculator.ParamLocals</c>.</summary>
    public static SbxEmitResult Emit(Sbx3Node? root, IReadOnlyList<string> paramNames, bool quatMode)
        => Emit(root, paramNames, quatMode, gpuTarget: false);

    /// <summary>GPU-targeted overload. When <paramref name="gpuTarget"/> is
    /// true, Vec3/Quat helper calls route through <c>Vec3GpuOps.*</c> /
    /// <c>QuatGpuOps.*</c> mirrors that avoid IL Throw opcodes (ILGPU JIT
    /// rejects exception flow). Stage 3B added Quat-mode support; Quat-mode
    /// constants and Hamilton multiply ride the inline operators directly,
    /// runtime-exponent <c>qpow</c> routes through <c>QuatGpuOps.Pow</c>.</summary>
    public static SbxEmitResult Emit(Sbx3Node? root, IReadOnlyList<string> paramNames, bool quatMode, bool gpuTarget)
        => Emit(root, paramNames, quatMode, gpuTarget, extraSlots: null);

    /// <summary>Chain-aware overload. <paramref name="extraSlots"/> maps
    /// prior-step output slot indices to a (local C# identifier, value kind)
    /// pair. Used by Sandbox-chain GPU compile: each step's emitted body
    /// references prior step outputs by the local name the kernel source
    /// declared for them. Slot kinds are seeded into the emitter scope so
    /// member access (.X/.Y/.Z) on a prior-step name infers correctly.</summary>
    public static SbxEmitResult Emit(
        Sbx3Node? root,
        IReadOnlyList<string> paramNames,
        bool quatMode,
        bool gpuTarget,
        IReadOnlyDictionary<int, (string Name, SbxEmitKind Kind)>? extraSlots)
    {
        if (root == null) return new(false, "Empty AST.", null, SbxEmitKind.Real);
        try
        {
            var ctx = new EmitCtx(paramNames, quatMode, gpuTarget, extraSlots);
            var sb = new StringBuilder();
            var kind = ctx.Emit(root, sb);
            return new(true, null, sb.ToString(), kind);
        }
        catch (NotSupportedException ex)
        {
            return new(false, ex.Message, null, SbxEmitKind.Real);
        }
    }

    private sealed class EmitCtx
    {
        private readonly IReadOnlyList<string> _paramNames;
        private readonly bool _quat;
        private readonly bool _gpu;
        /// <summary>Type prefix for Vec3 helper calls: "Vec3" on CPU,
        /// "Vec3GpuOps" on GPU. Pow/Rot/BoxFold/SphereFold/Mod/Normalized
        /// have device-safe mirrors in Vec3GpuOps that avoid Throw.</summary>
        private string V3 => _gpu ? "Vec3GpuOps" : "Vec3";
        private readonly Dictionary<int, SbxEmitKind> _slotKinds = new();
        /// <summary>Let-bound slot → already-emitted value expression text.
        /// DSL is pure so direct substitution is safe; multiple references
        /// to the same let-bound name re-evaluate the value expression, which
        /// is fine for the small expression trees produced from real-world
        /// Sandbox sources and avoids a delegate dispatch on the hot path.</summary>
        private readonly Dictionary<int, string> _letSubs = new();
        /// <summary>Prior-step output slot → (local C# identifier, value kind).
        /// Chain compile populates this so step bodies referencing prior step
        /// outputs by name resolve to the local declared in the emitted Step.</summary>
        private readonly IReadOnlyDictionary<int, (string Name, SbxEmitKind Kind)>? _extraSlots;

        public EmitCtx(IReadOnlyList<string> paramNames, bool quatMode, bool gpuTarget = false)
            : this(paramNames, quatMode, gpuTarget, extraSlots: null) { }

        public EmitCtx(
            IReadOnlyList<string> paramNames,
            bool quatMode,
            bool gpuTarget,
            IReadOnlyDictionary<int, (string Name, SbxEmitKind Kind)>? extraSlots)
        {
            _paramNames = paramNames;
            _quat = quatMode;
            _gpu = gpuTarget;
            _extraSlots = extraSlots;
            _slotKinds[SandboxBulbExpression.SlotZ] = quatMode ? SbxEmitKind.Quat : SbxEmitKind.Vec;
            _slotKinds[SandboxBulbExpression.SlotC] = quatMode ? SbxEmitKind.Quat : SbxEmitKind.Vec;
            _slotKinds[SandboxBulbExpression.SlotN] = SbxEmitKind.Real;
            // Extras follow N. Calculator's ParamLocals places named params in
            // local doubles 0..N-1, then `t` last. Slot index = ReservedSlots + i.
            for (int i = 0; i < paramNames.Count; i++)
                _slotKinds[SandboxBulbExpression.ReservedSlots + i] = SbxEmitKind.Real;
            // `t` is always last in the extras the parser receives.
            _slotKinds[SandboxBulbExpression.ReservedSlots + paramNames.Count] = SbxEmitKind.Real;
            // Seed prior-step output slot kinds so member access on a chain
            // output (foo.x) infers correctly.
            if (extraSlots != null)
                foreach (var kv in extraSlots) _slotKinds[kv.Key] = kv.Value.Kind;
        }

        public SbxEmitKind Emit(Sbx3Node node, StringBuilder sb) => node switch
        {
            Sbx3Const  c => EmitConst(c, sb),
            Sbx3Slot   s => EmitSlot(s, sb),
            Sbx3Member m => EmitMember(m, sb),
            Sbx3Unary  u => EmitUnary(u, sb),
            Sbx3Binary b => EmitBinary(b, sb),
            Sbx3Ternary t => EmitTernary(t, sb),
            Sbx3Let    l => EmitLet(l, sb),
            Sbx3Call   call => EmitCall(call, sb),
            _ => throw new NotSupportedException($"Emit: unsupported node {node.GetType().Name}"),
        };

        private SbxEmitKind EmitConst(Sbx3Const c, StringBuilder sb)
        {
            if (c.V.IsQuat)
            {
                sb.Append("new Quat(")
                  .Append(Lit(c.V.W)).Append(", ")
                  .Append(Lit(c.V.X)).Append(", ")
                  .Append(Lit(c.V.Y)).Append(", ")
                  .Append(Lit(c.V.Z)).Append(')');
                return SbxEmitKind.Quat;
            }
            if (c.V.IsVec)
            {
                sb.Append("new Vec3(")
                  .Append(Lit(c.V.X)).Append(", ")
                  .Append(Lit(c.V.Y)).Append(", ")
                  .Append(Lit(c.V.Z)).Append(')');
                return SbxEmitKind.Vec;
            }
            sb.Append(Lit(c.V.X));
            return SbxEmitKind.Real;
        }

        private SbxEmitKind EmitSlot(Sbx3Slot s, StringBuilder sb)
        {
            // Let-bound slots inline the value expression text directly.
            if (_letSubs.TryGetValue(s.Slot, out var subText))
            {
                sb.Append('(').Append(subText).Append(')');
                return _slotKinds.TryGetValue(s.Slot, out var k) ? k : SbxEmitKind.Real;
            }
            if (s.Slot == SandboxBulbExpression.SlotZ) { sb.Append('z'); return _quat ? SbxEmitKind.Quat : SbxEmitKind.Vec; }
            if (s.Slot == SandboxBulbExpression.SlotC) { sb.Append('c'); return _quat ? SbxEmitKind.Quat : SbxEmitKind.Vec; }
            if (s.Slot == SandboxBulbExpression.SlotN) { sb.Append('n'); return SbxEmitKind.Real; }
            // Named param or `t` — emit the local that ParamLocals declares.
            int extraIdx = s.Slot - SandboxBulbExpression.ReservedSlots;
            if (extraIdx < 0) throw new NotSupportedException($"Emit: bad slot {s.Slot}");
            if (extraIdx < _paramNames.Count) { sb.Append(_paramNames[extraIdx]); return SbxEmitKind.Real; }
            // `t` slot — comes immediately after named params.
            if (extraIdx == _paramNames.Count) { sb.Append('t'); return SbxEmitKind.Real; }
            // Beyond t — chain prior-step outputs (only in chain compile).
            if (_extraSlots != null && _extraSlots.TryGetValue(s.Slot, out var es))
            {
                sb.Append(es.Name);
                return es.Kind;
            }
            throw new NotSupportedException($"Emit: unbound slot {s.Slot}");
        }

        private SbxEmitKind EmitMember(Sbx3Member m, StringBuilder sb)
        {
            sb.Append('(');
            var tk = Emit(m.Target, sb);
            sb.Append(").");
            switch (m.Axis)
            {
                case 'x': sb.Append('X'); return SbxEmitKind.Real;
                case 'y': sb.Append('Y'); return SbxEmitKind.Real;
                case 'z': sb.Append('Z'); return SbxEmitKind.Real;
                case 'w':
                    if (tk != SbxEmitKind.Quat) throw new NotSupportedException("Emit: .w requires Quat operand.");
                    sb.Append('W');
                    return SbxEmitKind.Real;
                default: throw new NotSupportedException($"Emit: bad axis '{m.Axis}'");
            }
        }

        private SbxEmitKind EmitUnary(Sbx3Unary u, StringBuilder sb)
        {
            if (u.Op == '!')
            {
                sb.Append('(');
                Emit(u.A, sb);
                sb.Append(" == 0.0 ? 1.0 : 0.0)");
                return SbxEmitKind.Real;
            }
            sb.Append("-(");
            var k = Emit(u.A, sb);
            sb.Append(')');
            return k;
        }

        private SbxEmitKind EmitBinary(Sbx3Binary b, StringBuilder sb)
        {
            // Short-circuit + comparisons reduce to real.
            if (b.Op is "&&" or "||" or "<" or ">" or "<=" or ">=" or "==" or "!=")
            {
                sb.Append("((");
                Emit(b.A, sb);
                sb.Append(") ").Append(b.Op).Append(" (");
                Emit(b.B, sb);
                sb.Append(") ? 1.0 : 0.0)");
                return SbxEmitKind.Real;
            }

            // Buffer each side so we can choose the operator form by inferred kind.
            var sbA = new StringBuilder();
            var ak = Emit(b.A, sbA);
            var sbB = new StringBuilder();
            var bk = Emit(b.B, sbB);

            if (b.Op == "^")
            {
                if (ak == SbxEmitKind.Vec)
                {
                    sb.Append(V3).Append(".Pow(").Append(sbA).Append(", ").Append(sbB).Append(')');
                    return SbxEmitKind.Vec;
                }
                if (ak == SbxEmitKind.Quat)
                    throw new NotSupportedException("Emit: '^' on Quat not supported in emitter (use qpow).");
                sb.Append("Math.Pow(").Append(sbA).Append(", ").Append(sbB).Append(')');
                return SbxEmitKind.Real;
            }

            if (b.Op == "*")
                return EmitMul(ak, bk, sbA, sbB, sb);

            // +, -, /  — operator-overloaded for Vec3/Quat, scalar broadcast OK.
            var rk = Widen(ak, bk);
            sb.Append('(').Append(sbA).Append(' ').Append(b.Op).Append(' ').Append(sbB).Append(')');
            return rk;
        }

        private static SbxEmitKind EmitMul(SbxEmitKind ak, SbxEmitKind bk, StringBuilder sbA, StringBuilder sbB, StringBuilder sb)
        {
            // Vec3 has no `Vec3 * Vec3` — Hadamard via Vec3.Mul (added below if missing).
            if (ak == SbxEmitKind.Vec && bk == SbxEmitKind.Vec)
            {
                sb.Append("new Vec3(")
                  .Append('(').Append(sbA).Append(").X * (").Append(sbB).Append(").X, ")
                  .Append('(').Append(sbA).Append(").Y * (").Append(sbB).Append(").Y, ")
                  .Append('(').Append(sbA).Append(").Z * (").Append(sbB).Append(").Z)");
                return SbxEmitKind.Vec;
            }
            if (ak == SbxEmitKind.Quat && bk == SbxEmitKind.Quat)
            {
                sb.Append('(').Append(sbA).Append(") * (").Append(sbB).Append(')');
                return SbxEmitKind.Quat;
            }
            // Mixed — operator-overloads cover Vec*double, Quat*double, double*Vec, double*Quat.
            sb.Append('(').Append(sbA).Append(") * (").Append(sbB).Append(')');
            return Widen(ak, bk);
        }

        private SbxEmitKind EmitTernary(Sbx3Ternary t, StringBuilder sb)
        {
            var sbT = new StringBuilder(); var tk = Emit(t.Then, sbT);
            var sbE = new StringBuilder(); var ek = Emit(t.Else, sbE);
            if (tk != ek) throw new NotSupportedException("Emit: ternary branches must agree on kind.");
            sb.Append("((");
            Emit(t.Cond, sb);
            sb.Append(") != 0.0 ? ").Append(sbT).Append(" : ").Append(sbE).Append(')');
            return tk;
        }

        private SbxEmitKind EmitLet(Sbx3Let l, StringBuilder sb)
        {
            // Inline substitution: emit the value expression as a string, push
            // it onto _letSubs keyed by slot, then emit the body. EmitSlot
            // intercepts references and pastes the value text. No delegate
            // dispatch on the raymarch hot path.
            var sbVal = new StringBuilder();
            var vk = Emit(l.Value, sbVal);
            var prior = _slotKinds.TryGetValue(l.Slot, out var p) ? (SbxEmitKind?)p : null;
            _slotKinds[l.Slot] = vk;
            string? priorSub = _letSubs.TryGetValue(l.Slot, out var ps) ? ps : null;
            _letSubs[l.Slot] = sbVal.ToString();
            try
            {
                var bk = Emit(l.Body, sb);
                return bk;
            }
            finally
            {
                if (prior.HasValue) _slotKinds[l.Slot] = prior.Value; else _slotKinds.Remove(l.Slot);
                if (priorSub != null) _letSubs[l.Slot] = priorSub; else _letSubs.Remove(l.Slot);
            }
        }

        private SbxEmitKind EmitCall(Sbx3Call call, StringBuilder sb)
        {
            switch (call.Name)
            {
                case "vec":
                    sb.Append("new Vec3(");
                    EmitAsReal(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "qvec":
                    sb.Append("new Quat(");
                    EmitAsReal(call.Args[3], sb); sb.Append(", ");   // W first
                    EmitAsReal(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Quat;
                case "triplex":
                    sb.Append(V3).Append(".Pow(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "length":
                    sb.Append('(');
                    EmitAsVec(call.Args[0], sb);
                    sb.Append(").Length");
                    return SbxEmitKind.Real;
                case "dot":
                    sb.Append("Vec3.Dot(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsVec(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Real;
                case "cross":
                    sb.Append("Vec3.Cross(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsVec(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "normalize":
                    if (_gpu)
                    {
                        sb.Append("Vec3GpuOps.Normalized(");
                        EmitAsVec(call.Args[0], sb); sb.Append(')');
                    }
                    else
                    {
                        sb.Append('(');
                        EmitAsVec(call.Args[0], sb);
                        sb.Append(").Normalized()");
                    }
                    return SbxEmitKind.Vec;
                case "rot":
                    sb.Append(V3).Append(".Rot(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsVec(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "boxfold":
                    sb.Append(V3).Append(".BoxFold(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "spherefold":
                    sb.Append(V3).Append(".SphereFold(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "absx": case "absy": case "absz":
                    sb.Append(V3).Append('.').Append(char.ToUpper(call.Name[0])).Append(call.Name.AsSpan(1)).Append('(');
                    EmitAsVec(call.Args[0], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "mod":
                    sb.Append(V3).Append(".Mod(");
                    EmitAsVec(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Vec;
                case "smin":
                    sb.Append(V3).Append(".SMin(");
                    EmitAsReal(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Real;
                case "pow":
                    sb.Append("Math.Pow(");
                    EmitAsReal(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Real;
                case "floor": return EmitMath1(call.Args[0], sb, "Floor");
                case "sign":  return EmitMath1(call.Args[0], sb, "Sign");
                case "min":   return EmitMath2(call.Args[0], call.Args[1], sb, "Min");
                case "max":   return EmitMath2(call.Args[0], call.Args[1], sb, "Max");
                case "clamp":
                    // GPU mirror skips Math.Clamp's Throw branch for lo>hi.
                    sb.Append(_gpu ? "Vec3GpuOps.Clamp(" : "Math.Clamp(");
                    EmitAsReal(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(", ");
                    EmitAsReal(call.Args[2], sb); sb.Append(')');
                    return SbxEmitKind.Real;
                case "qmul":
                    sb.Append('(');
                    EmitAsQuat(call.Args[0], sb); sb.Append(") * (");
                    EmitAsQuat(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Quat;
                case "qconj":
                    sb.Append('(');
                    EmitAsQuat(call.Args[0], sb);
                    sb.Append(").Conjugate()");
                    return SbxEmitKind.Quat;
                case "qpow":
                {
                    // Literal int exponent → unfold to chained `*` (no loop, no
                    // helper call, no branch). Runtime exponent → Quat.Pow.
                    if (call.Args[1] is Sbx3Const c2 && !c2.V.IsVec && !c2.V.IsQuat)
                    {
                        double exp = c2.V.X;
                        int n = (int)Math.Round(exp);
                        if (Math.Abs(exp - n) < 1e-9 && n >= 0 && n <= 16)
                        {
                            if (n == 0) { sb.Append("Quat.Identity"); return SbxEmitKind.Quat; }
                            var sbBase = new StringBuilder();
                            EmitAsQuat(call.Args[0], sbBase);
                            string baseS = sbBase.ToString();
                            sb.Append('(').Append(baseS).Append(')');
                            for (int i = 1; i < n; i++) sb.Append(" * (").Append(baseS).Append(')');
                            return SbxEmitKind.Quat;
                        }
                    }
                    sb.Append(_gpu ? "QuatGpuOps.Pow(" : "Quat.Pow(");
                    EmitAsQuat(call.Args[0], sb); sb.Append(", ");
                    EmitAsReal(call.Args[1], sb); sb.Append(')');
                    return SbxEmitKind.Quat;
                }
                case "qexp": case "qlog": case "qsqrt": case "qinv":
                case "qsin": case "qcos": case "qtan":
                case "qsinh": case "qcosh": case "qtanh":
                case "qasin": case "qacos": case "qatan":
                case "qasinh": case "qacosh": case "qatanh":
                case "qcsc": case "qsec": case "qcot":
                case "qcsch": case "qsech": case "qcoth":
                    // Quaternion-algebra transcendental. Emits Quat.* directly:
                    // those are throw-free and Clamp-free, hence device-safe on
                    // GPU with no QuatGpuOps mirror needed.
                    sb.Append("Quat.").Append(QuatFuncMethod(call.Name)).Append('(');
                    EmitAsQuat(call.Args[0], sb);
                    sb.Append(')');
                    return SbxEmitKind.Quat;
                case "sin": case "cos": case "tan":
                case "sinh": case "cosh": case "tanh":
                case "exp": case "log": case "sqrt":
                    return EmitMathOrCompwise(call.Args[0], sb, ToProperCase(call.Name), allowQuat: false);
                case "abs":
                    return EmitMathOrCompwise(call.Args[0], sb, "Abs", allowQuat: true);
                default:
                    throw new NotSupportedException($"Emit: unknown function '{call.Name}'");
            }
        }

        private SbxEmitKind EmitMath1(Sbx3Node arg, StringBuilder sb, string fn)
        {
            sb.Append("Math.").Append(fn).Append('(');
            EmitAsReal(arg, sb);
            sb.Append(')');
            return SbxEmitKind.Real;
        }

        private SbxEmitKind EmitMath2(Sbx3Node a, Sbx3Node b, StringBuilder sb, string fn)
        {
            sb.Append("Math.").Append(fn).Append('(');
            EmitAsReal(a, sb); sb.Append(", ");
            EmitAsReal(b, sb);
            sb.Append(')');
            return SbxEmitKind.Real;
        }

        private SbxEmitKind EmitMathOrCompwise(Sbx3Node arg, StringBuilder sb, string fn, bool allowQuat)
        {
            var sbArg = new StringBuilder();
            var k = Emit(arg, sbArg);
            if (k == SbxEmitKind.Vec)
            {
                sb.Append("new Vec3(Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").X), Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").Y), Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").Z))");
                return SbxEmitKind.Vec;
            }
            if (k == SbxEmitKind.Quat)
            {
                if (!allowQuat)
                    throw new NotSupportedException(
                        $"Emit: '{fn.ToLowerInvariant()}' is not defined componentwise on Quat. Project to a Vec3 component first.");
                sb.Append("new Quat(Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").W), Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").X), Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").Y), Math.").Append(fn).Append("((")
                  .Append(sbArg).Append(").Z))");
                return SbxEmitKind.Quat;
            }
            sb.Append("Math.").Append(fn).Append('(').Append(sbArg).Append(')');
            return SbxEmitKind.Real;
        }

        private void EmitAsReal(Sbx3Node n, StringBuilder sb)
        {
            var sbInner = new StringBuilder();
            var k = Emit(n, sbInner);
            switch (k)
            {
                case SbxEmitKind.Real: sb.Append(sbInner); break;
                case SbxEmitKind.Vec:  sb.Append('(').Append(sbInner).Append(").Length"); break;
                case SbxEmitKind.Quat: sb.Append('(').Append(sbInner).Append(").Length"); break;
            }
        }

        private void EmitAsVec(Sbx3Node n, StringBuilder sb)
        {
            var sbInner = new StringBuilder();
            var k = Emit(n, sbInner);
            switch (k)
            {
                case SbxEmitKind.Vec: sb.Append(sbInner); break;
                case SbxEmitKind.Real:
                    sb.Append("new Vec3(").Append(sbInner).Append(", ").Append(sbInner).Append(", ").Append(sbInner).Append(')');
                    break;
                case SbxEmitKind.Quat:
                    sb.Append('(').Append(sbInner).Append(").ToVec3()");
                    break;
            }
        }

        private void EmitAsQuat(Sbx3Node n, StringBuilder sb)
        {
            var sbInner = new StringBuilder();
            var k = Emit(n, sbInner);
            switch (k)
            {
                case SbxEmitKind.Quat: sb.Append(sbInner); break;
                case SbxEmitKind.Vec:
                    sb.Append("Quat.FromVec3(").Append(sbInner).Append(')');
                    break;
                case SbxEmitKind.Real:
                    sb.Append("new Quat(").Append(sbInner).Append(", 0, 0, 0)");
                    break;
            }
        }

        private static SbxEmitKind Widen(SbxEmitKind a, SbxEmitKind b)
        {
            if (a == SbxEmitKind.Quat || b == SbxEmitKind.Quat) return SbxEmitKind.Quat;
            if (a == SbxEmitKind.Vec  || b == SbxEmitKind.Vec)  return SbxEmitKind.Vec;
            return SbxEmitKind.Real;
        }

        private static string TypeName(SbxEmitKind k) => k switch
        {
            SbxEmitKind.Vec  => "Vec3",
            SbxEmitKind.Quat => "Quat",
            _                => "double",
        };

        private static string ToProperCase(string s)
            => string.Create(s.Length, s, (span, src) =>
            {
                span[0] = char.ToUpper(src[0]);
                src.AsSpan(1).CopyTo(span[1..]);
            });

        private static string Lit(double d)
            => d.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Maps a "q"-prefixed DSL transcendental name to its Quat.*
        /// method name (e.g. "qsin" → "Sin", "qinv" → "Inverse").</summary>
        private static string QuatFuncMethod(string name) => name switch
        {
            "qexp"   => "Exp",   "qlog"   => "Log",   "qsqrt"  => "Sqrt",  "qinv"   => "Inverse",
            "qsin"   => "Sin",   "qcos"   => "Cos",   "qtan"   => "Tan",
            "qsinh"  => "Sinh",  "qcosh"  => "Cosh",  "qtanh"  => "Tanh",
            "qasin"  => "Asin",  "qacos"  => "Acos",  "qatan"  => "Atan",
            "qasinh" => "Asinh", "qacosh" => "Acosh", "qatanh" => "Atanh",
            "qcsc"   => "Csc",   "qsec"   => "Sec",   "qcot"   => "Cot",
            "qcsch"  => "Csch",  "qsech"  => "Sech",  "qcoth"  => "Coth",
            _ => throw new NotSupportedException($"Emit: unknown quat function '{name}'"),
        };
    }
}
