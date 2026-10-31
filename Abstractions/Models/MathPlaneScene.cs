// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneScene.cs
//
// #1548 (Math Plane S1, epic #1547) — the Math Plane scene: display settings plus
// a list of objects (points, vectors, segments, polylines), stored as ONE text
// parameter (FractalParameters.MathPlaneScene, the same route as
// BilliardCustomDisks). Being text in the region snapshot makes it
// `--param MathPlaneScene=...` for --batch and keeps region recall covering it.
// Design: Docs/Technical/MathPlane-DesignPlan.md §3.2.
//
// Format, version 1. Statements are separated by ';' or newlines, so the
// serialised form is one line (fits a --param value). Tokens are separated by
// spaces. Numbers are invariant-culture decimals. Names are ASCII identifiers
// (letters, digits, '_' and a trailing apostrophe allowed: v, v', p_1).
//
//   v1                                   format version (optional, first)
//   axes on|off   grid on|off   ticks on|off
//   labels auto|offset|fixed:N|sci:N     axis label precision (#1559 modes)
//   det on|off  eigen on|off  lengths on|off   map read-outs (#1552; default off):
//                                        the unit square's image with det, the
//                                        map's real eigenvectors, vector lengths
//   point    NAME x y                  [options]
//   vector   NAME x y [from x0 y0]     [options]
//   segment  NAME x1 y1 x2 y2          [options]
//   polyline NAME x1 y1 x2 y2 ...      [options]   (two or more points)
//   curve NAME explicit   "f(x)"            [xmin xmax]          [options]  (#1550)
//   curve NAME parametric "x(t)" "y(t)"     tmin tmax            [options]
//   curve NAME polar      "r(theta)"        [thetamin thetamax]  [options]
//   curve NAME implicit   "F(x, y)"                              [options]  (#1551: F = 0)
//   region NAME "condition(x, y)"                                [options]  (#1551: shaded where true)
//   options: #RRGGBB (colour)  hidden  nolabel
//   map "f(z)"                          #1553: the complex map (reads z and c); it applies
//                                        when MathPlaneMapMode is Complex
//   phase off|on|contours               #1553: phase-portrait background, colour by
//                                        arg f(z); contours adds |f| = 2^k steps
//   probe x y                           #1553: pinned f'(z) read-out at z = x + iy
//
// Curve expressions are the plot language (PlotExpression: the equation
// language with real x / t / theta). They are double-quoted, and ';' or a newline
// inside the quotes does not end the statement. An explicit curve reads only x, a
// parametric one only t, a polar one only theta, an implicit curve and a region
// x and y. With no domain, an explicit
// curve spans the view and a polar one theta 0..2pi.
//
// Unknown statements and options are errors with their position, never ignored.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FracturingFog.ViewState;

namespace FracturingFog.Models
{
    /// <summary>Object kinds. <see cref="Region"/> (#1551) shades where its condition
    /// holds (non-zero), e.g. <c>"x^2 + y^2 &lt; 4"</c>.</summary>
    public enum MathPlaneObjectKind { Point, Vector, Segment, Polyline, Curve, Region }

    /// <summary>#1550 — how a <see cref="MathPlaneObjectKind.Curve"/> is given.
    /// <see cref="Implicit"/> (#1551) is the zero set of F(x, y).</summary>
    public enum MathPlaneCurveKind { Explicit, Parametric, Polar, Implicit }

    /// <summary>#1553 — the phase-portrait background of a complex map:
    /// <see cref="On"/> colours each pixel z by arg f(z); <see cref="Contours"/> also
    /// steps the brightness where |f(z)| crosses a power of 2.</summary>
    public enum MathPlanePhase { Off, On, Contours }

    /// <summary>One plotted object. <see cref="Coords"/> holds x, y pairs: a point's
    /// position; a vector's tip then (if <see cref="HasFrom"/>) its tail; a segment's
    /// two ends; a polyline's vertices. A curve (#1550) keeps its expressions in
    /// <see cref="Sources"/> and its domain (min, max; empty = default) in
    /// <see cref="Coords"/>.</summary>
    public sealed record MathPlaneObject(
        MathPlaneObjectKind Kind,
        string Name,
        IReadOnlyList<double> Coords,
        uint Color,
        bool Visible = true,
        bool ShowLabel = true,
        bool HasFrom = false,
        MathPlaneCurveKind CurveKind = MathPlaneCurveKind.Explicit,
        IReadOnlyList<string>? Sources = null)
    {
        /// <summary>A curve's expressions (one; two for parametric); empty otherwise.</summary>
        public IReadOnlyList<string> Expressions => Sources ?? Array.Empty<string>();

        /// <summary>A curve's domain, or null for the default (see file header).</summary>
        public (double Min, double Max)? Domain => Kind == MathPlaneObjectKind.Curve && Coords.Count == 2
            ? (Coords[0], Coords[1]) : null;

        public (double X, double Y) Point(int i) => (Coords[2 * i], Coords[2 * i + 1]);
        public int PointCount => Coords.Count / 2;

        /// <summary>A vector's tail: its <c>from</c> point, else the origin.</summary>
        public (double X, double Y) Tail => HasFrom ? (Coords[2], Coords[3]) : (0.0, 0.0);

        public bool Equals(MathPlaneObject? o) =>
            o is not null && Kind == o.Kind && Name == o.Name && Color == o.Color
            && Visible == o.Visible && ShowLabel == o.ShowLabel && HasFrom == o.HasFrom
            && Coords.SequenceEqual(o.Coords)
            && CurveKind == o.CurveKind && Expressions.SequenceEqual(o.Expressions);

        public override int GetHashCode() => HashCode.Combine(Kind, Name, Color, Coords.Count);
    }

    /// <summary>A parse failure: the 1-based statement and column, and why.</summary>
    public sealed record MathPlaneSceneError(int Statement, int Column, string Message)
    {
        public override string ToString() => $"statement {Statement}, col {Column}: {Message}";
    }

    /// <summary>#1548 — the Math Plane scene. See file header for the text format.</summary>
    public sealed record MathPlaneScene
    {
        public const int FormatVersion = 1;

        public bool ShowAxes { get; init; } = true;
        public bool ShowGrid { get; init; } = true;
        public bool ShowTicks { get; init; } = true;
        /// <summary>#1552 — shade the unit square's image under the map and print det.</summary>
        public bool ShowDet { get; init; }
        /// <summary>#1552 — draw the map's real eigenvector lines with their eigenvalues.</summary>
        public bool ShowEigen { get; init; }
        /// <summary>#1552 — print each vector's length (after the map) at its tip.</summary>
        public bool ShowLengths { get; init; }
        public GridLabelSettings Labels { get; init; } = GridLabelSettings.Default;
        public IReadOnlyList<MathPlaneObject> Objects { get; init; } = Array.Empty<MathPlaneObject>();
        /// <summary>#1553 — the complex map's source, e.g. <c>z^2 + c</c>; null = none.</summary>
        public string? MapSource { get; init; }
        /// <summary>#1553 — the phase-portrait background.</summary>
        public MathPlanePhase Phase { get; init; }
        /// <summary>#1553 — the pinned f'(z) read-out point, or null.</summary>
        public (double X, double Y)? Probe { get; init; }

        /// <summary>Okabe–Ito colours (colour-blind safe), the default object colours
        /// in order; see the bibliography's Okabe–Ito entry.</summary>
        public static readonly uint[] DefaultPalette =
        {
            0xFFE69F00, 0xFF56B4E9, 0xFF009E73, 0xFFF0E442,
            0xFF0072B2, 0xFFD55E00, 0xFFCC79A7,
        };

        /// <summary>The scene a new Math Plane view starts with: the basis vectors.</summary>
        public const string DefaultText = "v1; vector i 1 0 #E69F00; vector j 0 1 #56B4E9";

        public bool Equals(MathPlaneScene? o) =>
            o is not null && ShowAxes == o.ShowAxes && ShowGrid == o.ShowGrid && ShowTicks == o.ShowTicks
            && ShowDet == o.ShowDet && ShowEigen == o.ShowEigen && ShowLengths == o.ShowLengths
            && Labels == o.Labels && Objects.SequenceEqual(o.Objects)
            && MapSource == o.MapSource && Phase == o.Phase && Probe == o.Probe;

        public override int GetHashCode() => HashCode.Combine(ShowAxes, ShowGrid, ShowTicks, Labels, Objects.Count);

        // ── Serialise ────────────────────────────────────────────────────────

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>One-line text form; <see cref="TryParse"/> reads it back to an equal scene.</summary>
        public string Serialize()
        {
            var parts = new List<string> { "v" + FormatVersion };
            if (!ShowAxes) parts.Add("axes off");
            if (!ShowGrid) parts.Add("grid off");
            if (!ShowTicks) parts.Add("ticks off");
            if (ShowDet) parts.Add("det on");
            if (ShowEigen) parts.Add("eigen on");
            if (ShowLengths) parts.Add("lengths on");
            if (Labels != GridLabelSettings.Default) parts.Add("labels " + LabelsToken(Labels));
            if (MapSource != null) parts.Add("map \"" + MapSource + "\"");
            if (Phase != MathPlanePhase.Off) parts.Add("phase " + Phase.ToString().ToLowerInvariant());
            if (Probe is { } pr) parts.Add("probe " + Num(pr.X) + " " + Num(pr.Y));
            foreach (var o in Objects) parts.Add(SerializeObject(o));
            return string.Join("; ", parts);
        }

        private static string LabelsToken(GridLabelSettings s) => s.Mode switch
        {
            GridLabelMode.Fixed => "fixed:" + s.ClampedDigits.ToString(Inv),
            GridLabelMode.Scientific => "sci:" + s.ClampedDigits.ToString(Inv),
            GridLabelMode.Offset => "offset",
            _ => "auto",
        };

        private static string SerializeObject(MathPlaneObject o)
        {
            var sb = new StringBuilder();
            if (o.Kind == MathPlaneObjectKind.Region)
            {
                sb.Append("region ").Append(o.Name).Append(" \"").Append(o.Expressions.Count > 0 ? o.Expressions[0] : "").Append('"');
                AppendOptions(sb, o);
                return sb.ToString();
            }
            if (o.Kind == MathPlaneObjectKind.Curve)
            {
                sb.Append("curve ").Append(o.Name).Append(' ').Append(o.CurveKind.ToString().ToLowerInvariant());
                foreach (var s in o.Expressions) sb.Append(" \"").Append(s).Append('"');
                foreach (var v in o.Coords) sb.Append(' ').Append(Num(v));
                AppendOptions(sb, o);
                return sb.ToString();
            }
            sb.Append(o.Kind.ToString().ToLowerInvariant()).Append(' ').Append(o.Name);
            int n = o.Kind == MathPlaneObjectKind.Vector ? 2 : o.Coords.Count;
            for (int i = 0; i < n; i++) sb.Append(' ').Append(Num(o.Coords[i]));
            if (o.Kind == MathPlaneObjectKind.Vector && o.HasFrom)
                sb.Append(" from ").Append(Num(o.Coords[2])).Append(' ').Append(Num(o.Coords[3]));
            AppendOptions(sb, o);
            return sb.ToString();
        }

        private static void AppendOptions(StringBuilder sb, MathPlaneObject o)
        {
            sb.Append(" #").Append((o.Color & 0xFFFFFF).ToString("X6", Inv));
            if (!o.Visible) sb.Append(" hidden");
            if (!o.ShowLabel) sb.Append(" nolabel");
        }

        private static string Num(double v) => v.ToString("R", Inv);

        // ── Parse ────────────────────────────────────────────────────────────

        /// <summary>Parse the text form. Empty or blank text is the empty scene.
        /// On failure returns false with the first error and an empty scene.</summary>
        public static bool TryParse(string? text, out MathPlaneScene scene, out MathPlaneSceneError? error)
        {
            scene = new MathPlaneScene();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            bool axes = true, grid = true, ticks = true;
            bool det = false, eigen = false, lengths = false;
            var labels = GridLabelSettings.Default;
            string? mapSource = null;
            var phase = MathPlanePhase.Off;
            (double, double)? probe = null;
            var objects = new List<MathPlaneObject>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            int stmtNo = 0;
            foreach (var (stmtText, stmtStart) in SplitStatements(text))
            {
                var toks = Tokenize(stmtText, stmtStart);
                if (toks.Count == 0) continue;
                stmtNo++;
                MathPlaneSceneError Err(int tokIndex, string msg)
                    => new(stmtNo, toks[Math.Min(tokIndex, toks.Count - 1)].Col + 1, msg);

                string head = toks[0].Text.ToLowerInvariant();
                if (stmtNo == 1 && head.Length > 1 && head[0] == 'v' && char.IsDigit(head[1]))
                {
                    if (toks.Count != 1) { error = Err(1, "unexpected text after the version"); return false; }
                    if (head != "v" + FormatVersion)
                    { error = Err(0, $"unsupported scene version '{toks[0].Text}' (this build reads v{FormatVersion})"); return false; }
                    continue;
                }

                switch (head)
                {
                    case "axes":
                    case "grid":
                    case "ticks":
                    case "det":
                    case "eigen":
                    case "lengths":
                    {
                        if (toks.Count != 2 || !TryOnOff(toks[1].Text, out bool on))
                        { error = Err(1, $"'{head}' takes on or off"); return false; }
                        switch (head)
                        {
                            case "axes": axes = on; break;
                            case "grid": grid = on; break;
                            case "ticks": ticks = on; break;
                            case "det": det = on; break;
                            case "eigen": eigen = on; break;
                            default: lengths = on; break;
                        }
                        break;
                    }
                    case "labels":
                    {
                        if (toks.Count != 2 || !TryLabels(toks[1].Text, out labels))
                        { error = Err(1, "'labels' takes auto, offset, fixed:N or sci:N (N 0-30; sci needs 1+)"); return false; }
                        break;
                    }
                    case "map":
                    {
                        if (mapSource != null) { error = Err(0, "'map' is given twice"); return false; }
                        if (toks.Count < 2 || !toks[1].Text.StartsWith('"'))
                        { error = Err(1, "'map' needs a quoted expression in z (and c), e.g. map \"z^2 + c\""); return false; }
                        if (toks.Count > 2) { error = Err(2, "unexpected text after the map's expression"); return false; }
                        if (!TryExpression(toks[1].Text, new[] { "z", "c" }, "the map", out string src, out string msg))
                        { error = Err(1, msg); return false; }
                        mapSource = src;
                        break;
                    }
                    case "phase":
                    {
                        string v = toks.Count == 2 ? toks[1].Text.ToLowerInvariant() : "";
                        if (v == "on") phase = MathPlanePhase.On;
                        else if (v == "off") phase = MathPlanePhase.Off;
                        else if (v == "contours") phase = MathPlanePhase.Contours;
                        else { error = Err(1, "'phase' takes off, on or contours"); return false; }
                        break;
                    }
                    case "probe":
                    {
                        if (probe != null) { error = Err(0, "'probe' is given twice"); return false; }
                        if (toks.Count != 3 || !TryNum(toks[1].Text, out double px) || !TryNum(toks[2].Text, out double py))
                        { error = Err(1, "'probe' takes x y"); return false; }
                        probe = (px, py);
                        break;
                    }
                    case "point":
                    case "vector":
                    case "segment":
                    case "polyline":
                    case "curve":
                    case "region":
                    {
                        bool ok = head == "curve" ? TryCurve(toks, objects.Count, out var obj, out int badTok, out string msg)
                                : head == "region" ? TryRegion(toks, objects.Count, out obj, out badTok, out msg)
                                : TryObject(head, toks, objects.Count, out obj, out badTok, out msg);
                        if (!ok)
                        { error = Err(badTok, msg); return false; }
                        if (!names.Add(obj!.Name))
                        { error = Err(1, $"name '{obj.Name}' is already used"); return false; }
                        objects.Add(obj);
                        break;
                    }
                    default:
                        error = Err(0, $"unknown statement '{toks[0].Text}' (expected point, vector, segment, polyline, curve, region, axes, grid, ticks, labels, det, eigen, lengths, map, phase or probe)");
                        return false;
                }
            }

            scene = new MathPlaneScene
            {
                ShowAxes = axes, ShowGrid = grid, ShowTicks = ticks, Labels = labels, Objects = objects,
                ShowDet = det, ShowEigen = eigen, ShowLengths = lengths,
                MapSource = mapSource, Phase = phase, Probe = probe,
            };
            return true;
        }

        private static bool TryObject(string head, List<(string Text, int Col)> toks, int index,
                                      out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            var kind = head switch
            {
                "point" => MathPlaneObjectKind.Point,
                "vector" => MathPlaneObjectKind.Vector,
                "segment" => MathPlaneObjectKind.Segment,
                _ => MathPlaneObjectKind.Polyline,
            };
            if (toks.Count < 2 || !IsName(toks[1].Text))
            { badTok = 1; msg = $"'{head}' needs a name (letters, digits, '_', optional trailing ')"; return false; }

            int i = 2;
            var coords = new List<double>();
            while (i < toks.Count && TryNum(toks[i].Text, out double v)) { coords.Add(v); i++; }

            bool hasFrom = false;
            if (kind == MathPlaneObjectKind.Vector && i < toks.Count && toks[i].Text.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                if (coords.Count != 2) { badTok = i; msg = "'from' comes after the vector's x y"; return false; }
                if (i + 2 >= toks.Count || !TryNum(toks[i + 1].Text, out double fx) || !TryNum(toks[i + 2].Text, out double fy))
                { badTok = Math.Min(i + 1, toks.Count - 1); msg = "'from' needs x y"; return false; }
                coords.Add(fx); coords.Add(fy); hasFrom = true; i += 3;
            }

            int need = kind switch { MathPlaneObjectKind.Segment => 4, _ => 2 };
            bool countOk = kind switch
            {
                MathPlaneObjectKind.Point => coords.Count == 2,
                MathPlaneObjectKind.Vector => coords.Count == (hasFrom ? 4 : 2),
                MathPlaneObjectKind.Segment => coords.Count == 4,
                _ => coords.Count >= 4 && coords.Count % 2 == 0,
            };
            int fixedCount = kind switch
            {
                MathPlaneObjectKind.Point => 2,
                MathPlaneObjectKind.Segment => 4,
                MathPlaneObjectKind.Vector => hasFrom ? 4 : 2,
                _ => -1,
            };
            if (fixedCount > 0 && coords.Count > fixedCount)
            {
                // The first surplus number: name + the expected count (+ "from" if any).
                badTok = 2 + fixedCount + (hasFrom ? 1 : 0);
                msg = "too many numbers";
                return false;
            }
            if (!countOk)
            {
                badTok = Math.Min(i, toks.Count - 1);
                msg = kind == MathPlaneObjectKind.Polyline
                    ? "a polyline needs two or more x y points"
                    : $"'{head}' needs {need} numbers, got {coords.Count}";
                return false;
            }

            if (!TryOptions(toks, i, index, out uint color, out bool visible, out bool showLabel, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(kind, toks[1].Text, coords, color, visible, showLabel, hasFrom);
            return true;
        }

        private static bool TryOptions(List<(string Text, int Col)> toks, int i, int index,
                                       out uint color, out bool visible, out bool showLabel, out int badTok, out string msg)
        {
            color = DefaultPalette[index % DefaultPalette.Length];
            visible = true; showLabel = true; badTok = 0; msg = "";
            for (; i < toks.Count; i++)
            {
                string t = toks[i].Text;
                if (t.StartsWith('#'))
                {
                    if (t.Length != 7 || !uint.TryParse(t.AsSpan(1), NumberStyles.HexNumber, Inv, out uint rgb))
                    { badTok = i; msg = $"colour '{t}' is not #RRGGBB"; return false; }
                    color = 0xFF000000 | rgb;
                }
                else if (t.Equals("hidden", StringComparison.OrdinalIgnoreCase)) visible = false;
                else if (t.Equals("nolabel", StringComparison.OrdinalIgnoreCase)) showLabel = false;
                else
                {
                    badTok = i;
                    msg = TryNum(t, out _) ? "too many numbers" : $"unknown option '{t}' (expected #RRGGBB, hidden or nolabel)";
                    return false;
                }
            }
            return true;
        }

        /// <summary>#1550 — <c>curve NAME explicit|parametric|polar "expr" ["expr"] [min max] [options]</c>.</summary>
        private static bool TryCurve(List<(string Text, int Col)> toks, int index,
                                     out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            if (toks.Count < 2 || !IsName(toks[1].Text))
            { badTok = 1; msg = "'curve' needs a name (letters, digits, '_', optional trailing ')"; return false; }
            if (toks.Count < 3)
            { badTok = 1; msg = "'curve' needs a kind: explicit, parametric, polar or implicit"; return false; }
            MathPlaneCurveKind kind;
            switch (toks[2].Text.ToLowerInvariant())
            {
                case "explicit": kind = MathPlaneCurveKind.Explicit; break;
                case "parametric": kind = MathPlaneCurveKind.Parametric; break;
                case "polar": kind = MathPlaneCurveKind.Polar; break;
                case "implicit": kind = MathPlaneCurveKind.Implicit; break;
                default: badTok = 2; msg = $"unknown curve kind '{toks[2].Text}' (expected explicit, parametric, polar or implicit)"; return false;
            }

            int need = kind == MathPlaneCurveKind.Parametric ? 2 : 1;
            string[] allowed = kind switch
            {
                MathPlaneCurveKind.Explicit => new[] { "x" },
                MathPlaneCurveKind.Parametric => new[] { "t" },
                MathPlaneCurveKind.Implicit => new[] { "x", "y" },
                _ => new[] { "theta" },
            };
            string what = $"a {kind.ToString().ToLowerInvariant()} curve";
            var sources = new List<string>();
            int i = 3;
            for (int k = 0; k < need; k++, i++)
            {
                if (i >= toks.Count || !toks[i].Text.StartsWith('"'))
                {
                    badTok = Math.Min(i, toks.Count - 1);
                    msg = kind == MathPlaneCurveKind.Parametric
                        ? "a parametric curve needs two quoted expressions, \"x(t)\" \"y(t)\""
                        : $"{what} needs a quoted expression";
                    return false;
                }
                if (!TryExpression(toks[i].Text, allowed, what, out string src, out msg)) { badTok = i; return false; }
                sources.Add(src);
            }

            var domain = new List<double>();
            while (i < toks.Count && TryNum(toks[i].Text, out double v)) { domain.Add(v); i++; }
            if (kind == MathPlaneCurveKind.Implicit && domain.Count > 0)
            { badTok = i - domain.Count; msg = "an implicit curve has no domain (it is drawn across the view)"; return false; }
            if (domain.Count > 2) { badTok = i - domain.Count + 2; msg = "too many numbers"; return false; }
            if (domain.Count == 1 || (domain.Count == 0 && kind == MathPlaneCurveKind.Parametric))
            { badTok = Math.Min(i, toks.Count - 1); msg = kind == MathPlaneCurveKind.Parametric ? "a parametric curve needs tmin tmax" : "the domain needs min and max"; return false; }
            if (domain.Count == 2 && !(domain[1] > domain[0]))
            { badTok = i - 1; msg = "the domain's max must be above its min"; return false; }

            if (!TryOptions(toks, i, index, out uint color, out bool visible, out bool showLabel, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(MathPlaneObjectKind.Curve, toks[1].Text, domain, color, visible, showLabel,
                                      CurveKind: kind, Sources: sources);
            return true;
        }

        /// <summary>#1551 — <c>region NAME "condition(x, y)" [options]</c>.</summary>
        private static bool TryRegion(List<(string Text, int Col)> toks, int index,
                                      out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            if (toks.Count < 2 || !IsName(toks[1].Text))
            { badTok = 1; msg = "'region' needs a name (letters, digits, '_', optional trailing ')"; return false; }
            if (toks.Count < 3 || !toks[2].Text.StartsWith('"'))
            { badTok = Math.Min(2, toks.Count - 1); msg = "a region needs a quoted condition, e.g. \"x^2 + y^2 < 4\""; return false; }
            if (!TryExpression(toks[2].Text, new[] { "x", "y" }, "a region", out string src, out msg)) { badTok = 2; return false; }
            if (!TryOptions(toks, 3, index, out uint color, out bool visible, out bool showLabel, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(MathPlaneObjectKind.Region, toks[1].Text, Array.Empty<double>(), color, visible, showLabel,
                                      Sources: new[] { src });
            return true;
        }

        /// <summary>A quoted plot-language token: closed quotes, one line, parses, and
        /// reads only <paramref name="allowed"/> variables.</summary>
        private static bool TryExpression(string token, string[] allowed, string what, out string src, out string msg)
        {
            src = ""; msg = "";
            if (token.Length < 2 || !token.EndsWith('"')) { msg = "unterminated quote"; return false; }
            src = token[1..^1];
            if (src.Contains('\n') || src.Contains('\r')) { msg = "an expression must be on one line"; return false; }
            if (!PlotExpression.TryParse(src, out var expr, out string? err)) { msg = "expression: " + err; return false; }
            var wrong = expr!.UsedVariables().Where(v => !allowed.Contains(v)).ToList();
            if (wrong.Count > 0)
            { msg = $"{what}'s expression reads only {string.Join(", ", allowed)}, not {string.Join(", ", wrong)}"; return false; }
            return true;
        }

        private static IEnumerable<(string Text, int Start)> SplitStatements(string text)
        {
            int start = 0;
            bool quoted = false;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] == '"') quoted = !quoted;
                if (i == text.Length || (!quoted && (text[i] == ';' || text[i] == '\n' || text[i] == '\r')))
                {
                    yield return (text[start..i], start);
                    start = i + 1;
                }
            }
        }

        private static List<(string Text, int Col)> Tokenize(string stmt, int offset)
        {
            var list = new List<(string, int)>();
            int i = 0;
            while (i < stmt.Length)
            {
                while (i < stmt.Length && char.IsWhiteSpace(stmt[i])) i++;
                int s = i;
                if (i < stmt.Length && stmt[i] == '"')
                {
                    // A quoted token runs to the closing quote (kept, so callers
                    // can tell it is quoted and whether it was closed).
                    int close = stmt.IndexOf('"', i + 1);
                    i = close < 0 ? stmt.Length : close + 1;
                }
                else
                    while (i < stmt.Length && !char.IsWhiteSpace(stmt[i])) i++;
                if (i > s) list.Add((stmt[s..i], offset + s));
            }
            return list;
        }

        private static bool TryNum(string t, out double v)
            => double.TryParse(t, NumberStyles.Float, Inv, out v) && double.IsFinite(v);

        private static bool TryOnOff(string t, out bool on)
        {
            on = t.Equals("on", StringComparison.OrdinalIgnoreCase);
            return on || t.Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryLabels(string t, out GridLabelSettings s)
        {
            s = GridLabelSettings.Default;
            string l = t.ToLowerInvariant();
            if (l == "auto") return true;
            if (l == "offset") { s = new GridLabelSettings(GridLabelMode.Offset); return true; }
            int colon = l.IndexOf(':');
            if (colon < 0 || !int.TryParse(l[(colon + 1)..], NumberStyles.None, Inv, out int n)) return false;
            string mode = l[..colon];
            if (mode == "fixed" && n is >= GridLabelSettings.MinDigits and <= GridLabelSettings.MaxDigits)
            { s = new GridLabelSettings(GridLabelMode.Fixed, n); return true; }
            if (mode == "sci" && n is >= 1 and <= GridLabelSettings.MaxDigits)
            { s = new GridLabelSettings(GridLabelMode.Scientific, n); return true; }
            return false;
        }

        /// <summary>ASCII identifier: a letter or '_', then letters, digits or '_',
        /// optionally ending in apostrophes (v').</summary>
        public static bool IsName(string t)
        {
            if (t.Length == 0 || !(IsAsciiLetter(t[0]) || t[0] == '_')) return false;
            int end = t.Length;
            while (end > 1 && t[end - 1] == '\'') end--;
            for (int i = 1; i < end; i++)
                if (!(IsAsciiLetter(t[i]) || char.IsAsciiDigit(t[i]) || t[i] == '_')) return false;
            return true;
        }

        private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }
}
