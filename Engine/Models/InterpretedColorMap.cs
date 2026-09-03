// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/InterpretedColorMap.cs
//
// #27 Phase 4 — the ColorGen DSL runs as an INTERPRETER, not Roslyn codegen.
//
// An InterpretedColorMap parses a ColorGen DSL source into a CgProgram once and
// evaluates it per pixel by walking the typed AST (CgNode) over a scalar/CgRgb
// value union — mirroring ColorGenEmitter's per-node C# exactly, against the
// ported CgRgb/CgMath runtime. No Roslyn, no AssemblyLoadContext, no assembly
// load: a custom theme is now a data object, not a compiled type.
//
// It still implements IGpuHlslPalette (reusing ColorGenHlslEmitter — HLSL text,
// not Roslyn) so the GPU single-precision palette path is unaffected, and
// IColorMapHandlesInSet so the DSL inputs `iter` / `isInSet` keep their
// documented meaning.
//
// Parity with the old compiled path is pinned by ColorGenInterpreterParityTests
// (interpret == Roslyn-compile, per pixel). Keep this evaluator and
// ColorMap.template.cs / ColorGenEmitter in lockstep.

using System;
using System.Collections.Generic;
using FracturingFog.ColorGen;
using FracturingFog.ColorGen.Emitters;
using FracturingFog.ColorGen.Parser;
using FracturingFog.Interefaces;

namespace FracturingFog.Models;

public class InterpretedColorMap :
    IColorMap, INamedColorMap, IColorMapWithPixelScale, IColorMapHandlesInSet, IGpuHlslPalette
{
    private readonly CgProgram _prog;
    private readonly Dictionary<string, int> _localSlot;
    private readonly int _slotCount;

    private readonly string _name;
    private readonly string _category;
    private readonly string _description;

    // Reusable per-thread scratch for let-binding values (Map runs on many
    // threads; slots are small — one array per thread, never per pixel).
    [ThreadStatic] private static CgVal[]? _scratch;

    protected InterpretedColorMap(
        CgProgram prog, string name, string category, string description,
        string hlslBody, string hlslPrelude, string paletteId)
    {
        _prog = prog;
        _name = name;
        _category = category;
        _description = description;
        HlslPaletteBody = hlslBody;
        HlslPrelude = hlslPrelude;
        PaletteId = paletteId;

        _localSlot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in prog.Statements)
            if (s is CgLet let)
                _localSlot[let.Name] = _localSlot.Count;
        _slotCount = _localSlot.Count;
    }

    // #615 Phase 1 — optional beyond-escape-radius surround colour for the
    // ColorGen theme. Packed ARGB; null ⇒ paint the escape gradient as before
    // (byte-identical). Set from GenerateOptions.OutOfBoundsColor in TryCreate;
    // inherited by InterpretedOrbitColorMap so both interpreter paths carry it.
    public uint? OutOfBoundsColorOverride { get; init; }
    uint? IColorMap.OutOfBoundsColor => OutOfBoundsColorOverride;

    /// <summary>Parse + prepare a theme for interpretation. Returns null map +
    /// an error string on a DSL parse error (no exception thrown).</summary>
    public static InterpretedColorMap? TryCreate(
        string source, GenerateOptions? options, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(source)) { error = "Source is empty."; return null; }

        CgProgram prog;
        try { prog = ColorGenParser.Parse(source); }
        catch (Exception ex) { error = $"Parse error: {ex.Message}"; return null; }

        var opts = options ?? new GenerateOptions();

        // F15 (#591) — a program referencing an orbit-accumulator input needs
        // per-iteration orbit sampling, so it runs on the orbit-aware interpreter
        // (CPU) and advertises NO GPU palette (the HLSL escape-only path can't
        // produce those accumulators). Only the referenced accumulators are
        // actually computed per iteration (see InterpretedOrbitColorMap).
        var orbitInputs = CollectOrbitInputs(prog);
        if (orbitInputs.Count > 0)
        {
            // F16 (#603) — GPU orbit is opt-in (default off): only when enabled do
            // we emit HLSL + a non-None orbit mask so the kernel builds the
            // orbit-accumulating variant; otherwise the map advertises no GPU
            // palette and renders on the CPU exactly as before.
            string oBody = "", oPrelude = "", oId = "Interp_none";
            var mask = FracturingFog.Interefaces.GpuOrbitInputs.None;
            // #611 — the shape-selectable `trap` input has no HLSL SDF for the 14
            // non-legacy shapes, so a `trap` theme stays CPU-only (advertises no
            // GPU palette) exactly as all orbit did pre-F16. GPU parity for the
            // full shape set is a follow-up (needs the 19 SDFs in HLSL).
            bool usesSelectableTrap = orbitInputs.Contains("trap");
            if (InterpretedOrbitColorMap.GpuEnabled && !usesSelectableTrap)
            {
                try
                {
                    var hlsl = new ColorGenHlslEmitter(indent: "    ");
                    oBody = hlsl.EmitBody(prog);
                    oPrelude = ColorGenHlslPrelude.Build(hlsl.PaletteArities);
                    mask = InterpretedOrbitColorMap.MaskFrom(orbitInputs);
                    oId = "InterpOrbit_" + ShortHash(oBody + "\0" + oPrelude + "\0" + (int)mask);
                }
                catch { oBody = ""; oPrelude = ""; oId = "Interp_none"; mask = FracturingFog.Interefaces.GpuOrbitInputs.None; }
            }
            return new InterpretedOrbitColorMap(prog, opts.ThemeName, opts.Category, opts.Description,
                                                orbitInputs, oBody, oPrelude, oId, mask,
                                                InterpretedOrbitColorMap.ParseTrapShape(opts.TrapShape))
            {
                OutOfBoundsColorOverride = opts.OutOfBoundsColor,   // #615
            };
        }

        // Same HLSL the compiled path emits (text generation — not Roslyn).
        string hlslBody, hlslPrelude, paletteId;
        try
        {
            var hlsl = new ColorGenHlslEmitter(indent: "    ");
            hlslBody = hlsl.EmitBody(prog);
            hlslPrelude = ColorGenHlslPrelude.Build(hlsl.PaletteArities);
            paletteId = "Interp_" + ShortHash(hlslBody + "\0" + hlslPrelude);
        }
        catch
        {
            // HLSL is a GPU-only convenience; a failure here must not stop the
            // CPU theme from working. Fall back to no GPU palette.
            hlslBody = ""; hlslPrelude = ""; paletteId = "Interp_none";
        }

        return new InterpretedColorMap(prog, opts.ThemeName, opts.Category, opts.Description,
                                       hlslBody, hlslPrelude, paletteId)
        {
            OutOfBoundsColorOverride = opts.OutOfBoundsColor,   // #615
        };
    }

    /// <summary>F15 — the set of orbit-accumulator inputs the program actually
    /// references (empty ⇒ not orbit-aware). Only these are computed per
    /// iteration by the orbit-aware map.</summary>
    private static HashSet<string> CollectOrbitInputs(CgProgram prog)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in prog.Statements)
        {
            CgNode? node = s switch { CgLet l => l.Value, CgReturn r => r.Value, _ => null };
            if (node != null) CollectOrbit(node, found);
        }
        return found;
    }

    private static void CollectOrbit(CgNode n, HashSet<string> found)
    {
        switch (n)
        {
            case CgVar v: if (v.IsBuiltIn && CgInputs.OrbitScalars.Contains(v.Name)) found.Add(v.Name); break;
            case CgChannel ch: CollectOrbit(ch.Target, found); break;
            case CgUnary u: CollectOrbit(u.Operand, found); break;
            case CgBinary b: CollectOrbit(b.Lhs, found); CollectOrbit(b.Rhs, found); break;
            case CgTernary t: CollectOrbit(t.Cond, found); CollectOrbit(t.IfTrue, found); CollectOrbit(t.IfFalse, found); break;
            case CgCall c: foreach (var a in c.Args) CollectOrbit(a, found); break;
        }
    }

    // ── IColorMap / INamedColorMap metadata ─────────────────────────────────

    public ColorPaletteType Type => ColorPaletteType.Algorithmic;
    public int MaxIterations { get; set; } = 512;

    public string DisplayName => _name;
    public string DisplayCategory => _category;
    public string DisplayDescription => _description;

    // ── IColorMapWithPixelScale ─────────────────────────────────────────────
    private double _pixelScale = 1.0;
    public double PixelScale { set => _pixelScale = value; }

    // ── IGpuHlslPalette ─────────────────────────────────────────────────────
    public string HlslPaletteBody { get; }
    public string HlslPrelude { get; }
    public string PaletteId { get; }

    // ── Map overloads (mirror the template's adapter + delegation) ──────────

    public int Map(float smooth, float distance, int iterations)
        => Map(smooth, distance, iterations, 0f, 0f, 0f, 0f, 0f, 0f);

    public int Map(float smooth, float distance, int iterations, float nx, float ny)
        => Map(smooth, distance, iterations, nx, ny, 0f, 0f, 0f, 0f);

    public int Map(float smooth, float distance, int iterations,
                   float nx, float ny,
                   float finalZr, float finalZi, float dzdcR, float dzdcI)
    {
        In inp = BuildIn(smooth, distance, iterations, nx, ny, finalZr, finalZi, dzdcR, dzdcI);
        return Evaluate(in inp);
    }

    /// <summary>Build the built-in input record from the escape-final Map args.
    /// Orbit-accumulator fields (TrapMin/StripeAvg/TiaAvg) default to 0 here; the
    /// orbit-aware subclass fills them in <c>MapWithOrbit</c>.</summary>
    protected In BuildIn(float smooth, float distance, int iterations,
                         float nx, float ny,
                         float finalZr, float finalZi, float dzdcR, float dzdcI)
        => new In
        {
            Smooth  = smooth,
            Dist    = distance,
            Iter    = iterations,
            MaxIter = MaxIterations,
            T       = MaxIterations > 0 ? smooth / (double)MaxIterations : 0.0,
            Nx      = nx,
            Ny      = ny,
            Zr      = finalZr,
            Zi      = finalZi,
            Dzr     = dzdcR,
            Dzi     = dzdcI,
            Arg     = Math.Atan2((double)finalZi, (double)finalZr),
            Mag     = Math.Sqrt((double)finalZr * finalZr + (double)finalZi * finalZi),
            IsInSet = iterations >= MaxIterations ? 1.0 : 0.0,
            PxScale = _pixelScale,
        };

    /// <summary>Walk the program's statements over the given inputs and pack the
    /// returned Vec3. Shared by <see cref="Map"/> and the orbit-aware subclass.</summary>
    protected int Evaluate(in In inp)
    {
        var slots = _scratch;
        if (slots == null || slots.Length < _slotCount)
            slots = _scratch = new CgVal[Math.Max(_slotCount, 4)];

        foreach (var s in _prog.Statements)
        {
            switch (s)
            {
                case CgLet let:
                    slots[_localSlot[let.Name]] = Eval(let.Value, slots, in inp);
                    break;
                case CgReturn ret:
                    return CgMath.PackArgb(Eval(ret.Value, slots, in inp).V);
            }
        }
        return unchecked((int)0xFF000000); // unreachable — parser guarantees a return
    }

    // ── Value union + evaluator ─────────────────────────────────────────────

    private readonly struct CgVal
    {
        public readonly double S;
        public readonly CgRgb V;
        private CgVal(double s, CgRgb v) { S = s; V = v; }
        public static CgVal Scalar(double s) => new(s, default);
        public static CgVal Vec(CgRgb v) => new(0.0, v);
    }

    private CgVal Eval(CgNode n, CgVal[] slots, in In inp)
    {
        switch (n)
        {
            case CgNumber num:
                return CgVal.Scalar(num.Value);

            case CgVar v:
                return v.IsBuiltIn
                    ? CgVal.Scalar(Input(v.Name, in inp))
                    : slots[_localSlot[v.Name]];

            case CgChannel ch:
            {
                CgRgb t = Eval(ch.Target, slots, in inp).V;
                return CgVal.Scalar(ch.Channel switch { 'r' => t.R, 'g' => t.G, _ => t.B });
            }

            case CgUnary u:
            {
                CgVal a = Eval(u.Operand, slots, in inp);
                return u.Op switch
                {
                    CgUnaryOp.Neg => u.Type == CgType.Scalar ? CgVal.Scalar(-a.S) : CgVal.Vec(CgRgb.Neg(a.V)),
                    CgUnaryOp.Pos => a,
                    CgUnaryOp.Not => CgVal.Scalar(a.S == 0.0 ? 1.0 : 0.0),
                    _ => throw new InvalidOperationException(),
                };
            }

            case CgBinary b:
                return EvalBinary(b, slots, in inp);

            case CgTernary tern:
                return Eval(tern.Cond, slots, in inp).S != 0.0
                    ? Eval(tern.IfTrue, slots, in inp)
                    : Eval(tern.IfFalse, slots, in inp);

            case CgCall c:
                return EvalCall(c, slots, in inp);

            default:
                throw new InvalidOperationException($"Unhandled node {n.GetType().Name}");
        }
    }

    private CgVal EvalBinary(CgBinary b, CgVal[] slots, in In inp)
    {
        // Comparisons + logical: scalar, short-circuit for && / ||.
        switch (b.Op)
        {
            case CgBinOp.And:
                return CgVal.Scalar(Eval(b.Lhs, slots, in inp).S != 0.0 && Eval(b.Rhs, slots, in inp).S != 0.0 ? 1.0 : 0.0);
            case CgBinOp.Or:
                return CgVal.Scalar(Eval(b.Lhs, slots, in inp).S != 0.0 || Eval(b.Rhs, slots, in inp).S != 0.0 ? 1.0 : 0.0);
        }

        CgVal l = Eval(b.Lhs, slots, in inp);
        CgVal r = Eval(b.Rhs, slots, in inp);

        switch (b.Op)
        {
            case CgBinOp.Lt: return CgVal.Scalar(l.S <  r.S ? 1.0 : 0.0);
            case CgBinOp.Le: return CgVal.Scalar(l.S <= r.S ? 1.0 : 0.0);
            case CgBinOp.Gt: return CgVal.Scalar(l.S >  r.S ? 1.0 : 0.0);
            case CgBinOp.Ge: return CgVal.Scalar(l.S >= r.S ? 1.0 : 0.0);
            case CgBinOp.Eq: return CgVal.Scalar(l.S == r.S ? 1.0 : 0.0);
            case CgBinOp.Ne: return CgVal.Scalar(l.S != r.S ? 1.0 : 0.0);
        }

        // Arithmetic — scalar when the node is Scalar, else broadcast on operand
        // types exactly as ColorGenEmitter.WrapAdd/…/WrapPow do.
        if (b.Type == CgType.Scalar)
        {
            double a = l.S, bb = r.S;
            return CgVal.Scalar(b.Op switch
            {
                CgBinOp.Add => a + bb,
                CgBinOp.Sub => a - bb,
                CgBinOp.Mul => a * bb,
                CgBinOp.Div => a / bb,
                CgBinOp.Mod => CgMath.Mod(a, bb),
                CgBinOp.Pow => Math.Pow(a, bb),
                _ => throw new InvalidOperationException(),
            });
        }

        (CgType, CgType) kinds = (b.Lhs.Type, b.Rhs.Type);
        return CgVal.Vec(b.Op switch
        {
            CgBinOp.Add => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Add(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.AddVS(l.V, r.S),
                _                            => CgRgb.AddSV(l.S, r.V),
            },
            CgBinOp.Sub => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Sub(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.SubVS(l.V, r.S),
                _                            => CgRgb.SubSV(l.S, r.V),
            },
            CgBinOp.Mul => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Mul(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.MulVS(l.V, r.S),
                _                            => CgRgb.MulSV(l.S, r.V),
            },
            CgBinOp.Div => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Div(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.DivVS(l.V, r.S),
                _                            => CgRgb.DivSV(l.S, r.V),
            },
            CgBinOp.Mod => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Mod(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.ModVS(l.V, r.S),
                _                            => CgRgb.ModSV(l.S, r.V),
            },
            CgBinOp.Pow => kinds switch
            {
                (CgType.Vec3, CgType.Vec3)   => CgRgb.Pow(l.V, r.V),
                (CgType.Vec3, CgType.Scalar) => CgRgb.PowVS(l.V, r.S),
                _                            => CgRgb.PowSV(l.S, r.V),
            },
            _ => throw new InvalidOperationException(),
        });
    }

    private CgVal EvalCall(CgCall c, CgVal[] slots, in In inp)
    {
        // Evaluate args once (no side effects). Indexed directly — local helper
        // functions can't capture a Span (ref struct), so read a[i].S / a[i].V.
        int argc = c.Args.Count;
        Span<CgVal> a = argc <= 8 ? stackalloc CgVal[argc] : new CgVal[argc];
        for (int i = 0; i < argc; i++) a[i] = Eval(c.Args[i], slots, in inp);

        switch (c.Name)
        {
            case "sin":      return CgVal.Scalar(Math.Sin(a[0].S));
            case "cos":      return CgVal.Scalar(Math.Cos(a[0].S));
            case "tan":      return CgVal.Scalar(Math.Tan(a[0].S));
            case "asin":     return CgVal.Scalar(Math.Asin(a[0].S));
            case "acos":     return CgVal.Scalar(Math.Acos(a[0].S));
            case "atan":     return CgVal.Scalar(Math.Atan(a[0].S));
            case "sinh":     return CgVal.Scalar(Math.Sinh(a[0].S));
            case "cosh":     return CgVal.Scalar(Math.Cosh(a[0].S));
            case "tanh":     return CgVal.Scalar(Math.Tanh(a[0].S));
            case "exp":      return CgVal.Scalar(Math.Exp(a[0].S));
            case "log":      return CgVal.Scalar(Math.Log(a[0].S));
            case "log2":     return CgVal.Scalar(Math.Log2(a[0].S));
            case "log10":    return CgVal.Scalar(Math.Log10(a[0].S));
            case "sqrt":     return CgVal.Scalar(Math.Sqrt(a[0].S));
            case "abs":      return CgVal.Scalar(Math.Abs(a[0].S));
            case "sign":     return CgVal.Scalar(Math.Sign(a[0].S));
            case "floor":    return CgVal.Scalar(Math.Floor(a[0].S));
            case "ceil":     return CgVal.Scalar(Math.Ceiling(a[0].S));
            case "round":    return CgVal.Scalar(Math.Round(a[0].S));
            case "fract":    return CgVal.Scalar(CgMath.Fract(a[0].S));
            case "saturate": return CgVal.Scalar(Math.Clamp(a[0].S, 0.0, 1.0));
            case "radians":  return CgVal.Scalar(a[0].S * (Math.PI / 180.0));
            case "degrees":  return CgVal.Scalar(a[0].S * (180.0 / Math.PI));
            case "atan2":    return CgVal.Scalar(Math.Atan2(a[0].S, a[1].S));
            case "hypot":    return CgVal.Scalar(CgMath.Hypot(a[0].S, a[1].S));
            case "min":      return CgVal.Scalar(Math.Min(a[0].S, a[1].S));
            case "max":      return CgVal.Scalar(Math.Max(a[0].S, a[1].S));
            case "mod":      return CgVal.Scalar(CgMath.Mod(a[0].S, a[1].S));
            case "pow":      return CgVal.Scalar(Math.Pow(a[0].S, a[1].S));
            case "step":     return CgVal.Scalar(a[1].S < a[0].S ? 0.0 : 1.0);
            case "clamp":    return CgVal.Scalar(Math.Clamp(a[0].S, a[1].S, a[2].S));
            case "smoothstep": return CgVal.Scalar(CgMath.Smoothstep(a[0].S, a[1].S, a[2].S));
            case "mix":      return CgVal.Scalar(a[0].S + (a[1].S - a[0].S) * a[2].S);
            case "mix_v":    return CgVal.Vec(CgRgb.Mix(a[0].V, a[1].V, a[2].S));
            case "hash":     return CgVal.Scalar(CgMath.Hash(a[0].S));
            case "hash2":    return CgVal.Scalar(CgMath.Hash2(a[0].S, a[1].S));
            case "rgb":      return CgVal.Vec(new CgRgb(a[0].S, a[1].S, a[2].S));
            case "hsv":      return CgVal.Vec(CgRgb.FromHsv(a[0].S, a[1].S, a[2].S));
            case "hsl":      return CgVal.Vec(CgRgb.FromHsl(a[0].S, a[1].S, a[2].S));
            case "oklab":    return CgVal.Vec(CgRgb.FromOkLab(a[0].S, a[1].S, a[2].S));
            case "oklch":    return CgVal.Vec(CgRgb.FromOkLch(a[0].S, a[1].S, a[2].S));
            case "mix_oklab":return CgVal.Vec(CgRgb.MixOkLab(a[0].V, a[1].V, a[2].S));
            case "palette":
            {
                var stops = new CgRgb[argc - 1];
                for (int i = 1; i < argc; i++) stops[i - 1] = a[i].V;
                return CgVal.Vec(CgRgb.Palette(a[0].S, stops));
            }
            case "cosine":     return CgVal.Vec(CgRgb.Cosine(a[0].S, a[1].V, a[2].V, a[3].V, a[4].V));
            case "brightness": return CgVal.Vec(CgRgb.Brightness(a[0].V, a[1].S));
            case "contrast":   return CgVal.Vec(CgRgb.Contrast(a[0].V, a[1].S));
            case "gamma":      return CgVal.Vec(CgRgb.Gamma(a[0].V, a[1].S));
            default: throw new InvalidOperationException($"Interpreter missing case for '{c.Name}'.");
        }
    }

    private static double Input(string name, in In inp) => name switch
    {
        "smooth"  => inp.Smooth,
        "dist"    => inp.Dist,
        "iter"    => inp.Iter,
        "maxIter" => inp.MaxIter,
        "t"       => inp.T,
        "nx"      => inp.Nx,
        "ny"      => inp.Ny,
        "zr"      => inp.Zr,
        "zi"      => inp.Zi,
        "dzr"     => inp.Dzr,
        "dzi"     => inp.Dzi,
        "arg"     => inp.Arg,
        "mag"     => inp.Mag,
        "isInSet" => inp.IsInSet,
        "pxScale" => inp.PxScale,
        // F15 orbit-accumulator inputs (0 on the non-orbit path).
        // #611 — shape-selectable primary trap; reads the same slot-1 value as
        // trapMin (the shape is chosen by the theme, not the input name).
        "trap"          => inp.TrapMin,
        "trapMin"       => inp.TrapMin,
        "trapCross"     => inp.TrapCross,
        "trapRing"      => inp.TrapRing,
        "trapHyperbola" => inp.TrapHyperbola,
        "trapHexagon"   => inp.TrapHexagon,
        "stripeAvg" => inp.StripeAvg,
        "tiaAvg"    => inp.TiaAvg,
        "curvature" => inp.Curvature,
        "lyapunov"  => inp.Lyapunov,
        "gaussian"  => inp.Gaussian,
        "expSmooth" => inp.ExpSmooth,
        _ => throw new InvalidOperationException($"Unknown built-in input '{name}'."),
    };

    // Mutable (populated via object initializer in BuildIn), then passed by `in`.
    protected struct In
    {
        public double Smooth, Dist, Iter, MaxIter, T, Nx, Ny, Zr, Zi, Dzr, Dzi, Arg, Mag, IsInSet, PxScale;
        // F15 — orbit-accumulator inputs, filled by the orbit-aware subclass.
        public double TrapMin, TrapCross, TrapRing, TrapHyperbola, TrapHexagon;
        public double StripeAvg, TiaAvg, Curvature, Lyapunov, Gaussian, ExpSmooth;
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
        var sb = new System.Text.StringBuilder(10);
        for (int i = 0; i < 5; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// F15 (#591) — orbit-aware ColorGen theme. Produced by
/// <see cref="InterpretedColorMap.TryCreate"/> when the program references an
/// orbit-accumulator input (trapMin / stripeAvg / tiaAvg). The calculator routes
/// it through the per-iteration orbit-sampling path (CPU); the orbit values are
/// bound at escape and the same interpreter body evaluates the colour.
///
/// Scope: exterior orbit colouring (the escaping structure). It advertises no GPU
/// palette (the HLSL escape-only path can't compute these) so rendering falls to
/// CPU. Trap uses the origin point-trap (min |z_n|); stripe uses the classic
/// UF density 7; TIA is the Mandelbrot triangle-inequality average.
/// </summary>
public sealed class InterpretedOrbitColorMap : InterpretedColorMap, IOrbitAwareColorMap, IGpuOrbitPalette
{
    /// <summary>Stripe-average sin multiplier (classic Ultra Fractal default).</summary>
    private const double StripeDensity = 7.0;

    // ── F16 (#603) — GPU orbit support (opt-in). ─────────────────────────────
    /// <summary>Master switch for GPU orbit rendering. Default ON (on-device
    /// parity confirmed — see F16 <c>--vulkanorbitprobe</c>); set <c>FF_GPU_ORBIT</c>
    /// to a falsy value (0 / false / off / no) to force the CPU orbit path, or
    /// set the property directly (tests). When off, an orbit theme advertises no
    /// GPU palette and renders on the CPU. The GPU path itself still only engages
    /// when GPU compute is on, the zoom is shallow, and the theme is exterior —
    /// see <see cref="MandelbrotCalculator.TryRunGpuOrbit"/>.</summary>
    public static bool GpuEnabled { get; set; } = ReadGpuOrbitEnv();

    private static bool ReadGpuOrbitEnv()
    {
        string? v = Environment.GetEnvironmentVariable("FF_GPU_ORBIT");
        if (string.IsNullOrEmpty(v)) return true;   // default ON
        return v is not ("0" or "false" or "False" or "off" or "OFF" or "no");
    }

    /// <summary>Map the referenced-input name set to the kernel's orbit mask.</summary>
    internal static GpuOrbitInputs MaskFrom(HashSet<string> orbitInputs)
    {
        var m = GpuOrbitInputs.None;
        var names = GpuOrbitInputOrder.DslNames;
        var bits = GpuOrbitInputOrder.Bits;
        for (int i = 0; i < names.Length; i++)
            if (orbitInputs.Contains(names[i])) m |= bits[i];
        return m;
    }

    /// <summary>F16 — the orbit accumulators the HLSL body reads. None when GPU
    /// orbit is disabled (⇒ CPU render).</summary>
    public GpuOrbitInputs OrbitInputs { get; }

    // Which accumulators this program actually references — only these are
    // computed per iteration (the transcendentals in Sample are not free).
    private readonly bool _trapMin, _trapCross, _ring, _hyperbola, _hexagon,
                          _stripe, _tia, _curvature, _lyapunov, _gaussian, _exp;
    // #611 — the shape-selectable primary trap. `_trap` = the DSL references the
    // `trap` input; `TrapShape` = the theme's chosen shape; `_trapShapeImpl` = the
    // built-in SDF sampler for a non-Point shape (null ⇒ inline point path, which
    // stays byte-identical to pre-#611 for the default shape).
    private readonly bool _trap;
    private readonly OrbitTrapBaseMap? _trapShapeImpl;

    /// <summary>#611 — the trap shape the <c>trap</c> input is measured against.</summary>
    public OrbitTrapShape TrapShape { get; }

    /// <summary>Parse a trap-shape name (from <c>GenerateOptions.TrapShape</c>)
    /// to the enum; unknown / empty ⇒ Point (the byte-identical default).</summary>
    internal static OrbitTrapShape ParseTrapShape(string? name)
        => Enum.TryParse<OrbitTrapShape>(name, ignoreCase: true, out var s) ? s : OrbitTrapShape.Point;

    // Ring trap geometry (matches the built-in OrbitTrapRingMap).
    private const double RingCx = -1.0, RingCy = 0.0, RingR = 0.3;

    internal InterpretedOrbitColorMap(CgProgram prog, string name, string category, string description,
                                      HashSet<string> orbitInputs,
                                      string hlslBody, string hlslPrelude, string paletteId,
                                      GpuOrbitInputs orbitMask,
                                      OrbitTrapShape trapShape = OrbitTrapShape.Point)
        : base(prog, name, category, description, hlslBody: hlslBody, hlslPrelude: hlslPrelude, paletteId: paletteId)
    {
        OrbitInputs = orbitMask;
        TrapShape  = trapShape;
        _trap      = orbitInputs.Contains("trap");
        // A non-Point selectable trap delegates slot-1 sampling to the built-in
        // SDF (same factory F13 uses). Point keeps the inline path below, so an
        // existing trapMin theme is byte-identical.
        _trapShapeImpl = (_trap && trapShape != OrbitTrapShape.Point)
            ? DataDrivenOrbitTrap.ShapeImpl(trapShape)
            : null;
        _trapMin   = orbitInputs.Contains("trapMin");
        _trapCross = orbitInputs.Contains("trapCross");
        _ring      = orbitInputs.Contains("trapRing");
        _hyperbola = orbitInputs.Contains("trapHyperbola");
        _hexagon   = orbitInputs.Contains("trapHexagon");
        _stripe    = orbitInputs.Contains("stripeAvg");
        _tia       = orbitInputs.Contains("tiaAvg");
        _curvature = orbitInputs.Contains("curvature");
        _lyapunov  = orbitInputs.Contains("lyapunov");
        _gaussian  = orbitInputs.Contains("gaussian");
        _exp       = orbitInputs.Contains("expSmooth");
    }

    public void InitOrbit(out OrbitAccumulator acc)
    {
        acc = default;
        acc.TrapMin = float.MaxValue;    // trapMin  — origin point-trap
        acc.TrapMin2 = float.MaxValue;   // trapCross — nearest-axis trap
        acc.TrapMin3 = float.MaxValue;   // trapRing
        acc.TrapMin4 = float.MaxValue;   // trapHyperbola
        acc.TrapMin5 = float.MaxValue;   // trapHexagon
    }

    public void Sample(ref OrbitAccumulator acc, double zr, double zi, double cr, double ci, int iter)
    {
        // trap / trapMin — slot-1 primary trap. #611: the shape is theme-selected.
        // Point (default) uses the inline origin point-trap (byte-identical to
        // pre-#611); any other shape delegates to the matching built-in trap SDF.
        if (_trapMin || _trap)
        {
            if (_trapShapeImpl != null)
            {
                _trapShapeImpl.Sample(ref acc, zr, zi, cr, ci, iter);
            }
            else
            {
                double d = Math.Sqrt(zr * zr + zi * zi);
                if ((float)d < acc.TrapMin) acc.TrapMin = (float)d;
            }
        }

        // trapCross — min distance to the nearer coordinate axis.
        if (_trapCross)
        {
            double d = Math.Min(Math.Abs(zr), Math.Abs(zi));
            if ((float)d < acc.TrapMin2) acc.TrapMin2 = (float)d;
        }

        // trapRing — min distance to a circle of radius 0.3 centred at (-1, 0).
        if (_ring)
        {
            double dx = zr - RingCx, dy = zi - RingCy;
            double d = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - RingR);
            if ((float)d < acc.TrapMin3) acc.TrapMin3 = (float)d;
        }

        // trapHyperbola — min distance to the curve |zr·zi| = 1.
        if (_hyperbola)
        {
            double f = Math.Abs(zr * zi) - 1.0;
            double gradMag = Math.Sqrt(zr * zr + zi * zi);
            if (gradMag < 1e-6) gradMag = 1e-6;
            double d = Math.Abs(f) / gradMag;
            if ((float)d < acc.TrapMin4) acc.TrapMin4 = (float)d;
        }

        // trapHexagon — min distance to a regular hexagon's edges (folded SDF,
        // matches the built-in OrbitTrapHexagonMap).
        if (_hexagon)
        {
            const double kx = -0.8660254037844387;   // −√3/2
            const double ky = 0.5;
            const double kz = 0.5773502691896257;    // tan(π/6)
            double px = Math.Abs(zr), py = Math.Abs(zi);
            double dot2 = 2.0 * Math.Min(kx * px + ky * py, 0.0);
            px -= dot2 * kx;
            py -= dot2 * ky;
            px -= Math.Clamp(px, -kz, kz);
            py -= 1.0;
            double d = Math.Sqrt(px * px + py * py);
            if ((float)d < acc.TrapMin5) acc.TrapMin5 = (float)d;
        }

        // stripeAvg — mean of 0.5 + 0.5·sin(density·arg(z_n)).
        if (_stripe)
        {
            double s = 0.5 + 0.5 * Math.Sin(StripeDensity * Math.Atan2(zi, zr));
            acc.LastStripe = s;
            acc.StripeSum += s;
            acc.StripeCount++;
        }

        // tiaAvg — triangle-inequality average (needs a predecessor, iter >= 2).
        // |z_{n-1}^2| = |z_n − c| for the z²+c map.
        if (_tia && iter >= 2)
        {
            double zMcR = zr - cr, zMcI = zi - ci;
            double absZprev2 = Math.Sqrt(zMcR * zMcR + zMcI * zMcI);
            double absC = Math.Sqrt(cr * cr + ci * ci);
            double absZ = Math.Sqrt(zr * zr + zi * zi);
            double m = Math.Abs(absZprev2 - absC);
            double M = absZprev2 + absC;
            if (M - m > 1e-12)
            {
                double t = (absZ - m) / (M - m);
                acc.LastTia = t;
                acc.TiaSum += t;
                acc.TiaCount++;
            }
        }

        // lyapunov — mean log|2 z_n| (local divergence rate).
        if (_lyapunov)
        {
            double a = Math.Sqrt(zr * zr + zi * zi);
            if (a > 1e-12) { acc.LyapunovSum += Math.Log(2.0 * a); acc.LyapunovCount++; }
        }

        // gaussian — mean distance to the nearest Gaussian integer.
        if (_gaussian)
        {
            double dr = zr - Math.Round(zr), di = zi - Math.Round(zi);
            acc.GaussianSum += Math.Sqrt(dr * dr + di * di);
            acc.GaussianCount++;
        }

        // expSmooth — mean of e^{−|z_n|} (Kerry Mitchell).
        if (_exp)
        {
            double a = Math.Sqrt(zr * zr + zi * zi);
            acc.ExpSum += Math.Exp(-a);
            acc.ExpCount++;
        }

        // curvature — mean |Δarg| between successive orbit segments. State
        // machine: iter 1 records z₁; iter 2 the first segment; iter ≥ 3 the
        // angle change. (Doesn't early-return — other accumulators run too.)
        if (_curvature)
        {
            if (iter == 1) { acc.PrevZr = zr; acc.PrevZi = zi; }
            else
            {
                double segR = zr - acc.PrevZr, segI = zi - acc.PrevZi;
                if (iter == 2) { acc.PrevSegR = segR; acc.PrevSegI = segI; }
                else
                {
                    double cross = acc.PrevSegR * segI - acc.PrevSegI * segR;
                    double dot = acc.PrevSegR * segR + acc.PrevSegI * segI;
                    if (cross != 0.0 || dot != 0.0)
                    {
                        acc.CurvatureSum += Math.Abs(Math.Atan2(cross, dot));
                        acc.CurvatureCount++;
                    }
                    acc.PrevSegR = segR; acc.PrevSegI = segI;
                }
                acc.PrevZr = zr; acc.PrevZi = zi;
            }
        }
    }

    public int MapWithOrbit(float smooth, float distance, int iterations,
                            float nx, float ny, in OrbitAccumulator acc)
    {
        In inp = BuildIn(smooth, distance, iterations, nx, ny, 0f, 0f, 0f, 0f);
        // Bind the accumulated means (raw — the DSL scales them). Unreferenced
        // ones stay 0 (never accumulated); the program doesn't read them.
        inp.TrapMin       = acc.TrapMin  == float.MaxValue ? 0.0 : acc.TrapMin;
        inp.TrapCross     = acc.TrapMin2 == float.MaxValue ? 0.0 : acc.TrapMin2;
        inp.TrapRing      = acc.TrapMin3 == float.MaxValue ? 0.0 : acc.TrapMin3;
        inp.TrapHyperbola = acc.TrapMin4 == float.MaxValue ? 0.0 : acc.TrapMin4;
        inp.TrapHexagon   = acc.TrapMin5 == float.MaxValue ? 0.0 : acc.TrapMin5;
        inp.StripeAvg = acc.StripeCount    > 0 ? acc.StripeSum    / acc.StripeCount    : 0.0;
        inp.TiaAvg    = acc.TiaCount       > 0 ? acc.TiaSum       / acc.TiaCount       : 0.0;
        inp.Curvature = acc.CurvatureCount > 0 ? acc.CurvatureSum / acc.CurvatureCount : 0.0;
        inp.Lyapunov  = acc.LyapunovCount  > 0 ? acc.LyapunovSum  / acc.LyapunovCount  : 0.0;
        inp.Gaussian  = acc.GaussianCount  > 0 ? acc.GaussianSum  / acc.GaussianCount  : 0.0;
        inp.ExpSmooth = acc.ExpCount       > 0 ? acc.ExpSum       / acc.ExpCount       : 0.0;
        return Evaluate(in inp);
    }
    // MapInteriorWithOrbit uses the IOrbitAwareColorMap default (delegates to
    // MapWithOrbit at smooth=0) — so a theme that opts into interior colouring
    // via the calculator gate still evaluates with the orbit inputs bound.
}
