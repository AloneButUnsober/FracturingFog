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

public sealed class InterpretedColorMap :
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

    private InterpretedColorMap(
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

        var opts = options ?? new GenerateOptions();
        return new InterpretedColorMap(prog, opts.ThemeName, opts.Category, opts.Description,
                                       hlslBody, hlslPrelude, paletteId);
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
        // Input adapter — identical to ColorMap.template.cs's in_* locals.
        var inp = new In
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
        _ => throw new InvalidOperationException($"Unknown built-in input '{name}'."),
    };

    // Mutable (populated via object initializer in Map), then passed by `in`.
    private struct In
    {
        public double Smooth, Dist, Iter, MaxIter, T, Nx, Ny, Zr, Zi, Dzr, Dzi, Arg, Mag, IsInSet, PxScale;
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
