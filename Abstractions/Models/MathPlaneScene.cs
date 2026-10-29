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
//   point    NAME x y                  [options]
//   vector   NAME x y [from x0 y0]     [options]
//   segment  NAME x1 y1 x2 y2          [options]
//   polyline NAME x1 y1 x2 y2 ...      [options]   (two or more points)
//   options: #RRGGBB (colour)  hidden  nolabel
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
    public enum MathPlaneObjectKind { Point, Vector, Segment, Polyline }

    /// <summary>One plotted object. <see cref="Coords"/> holds x, y pairs: a point's
    /// position; a vector's tip then (if <see cref="HasFrom"/>) its tail; a segment's
    /// two ends; a polyline's vertices.</summary>
    public sealed record MathPlaneObject(
        MathPlaneObjectKind Kind,
        string Name,
        IReadOnlyList<double> Coords,
        uint Color,
        bool Visible = true,
        bool ShowLabel = true,
        bool HasFrom = false)
    {
        public (double X, double Y) Point(int i) => (Coords[2 * i], Coords[2 * i + 1]);
        public int PointCount => Coords.Count / 2;

        /// <summary>A vector's tail: its <c>from</c> point, else the origin.</summary>
        public (double X, double Y) Tail => HasFrom ? (Coords[2], Coords[3]) : (0.0, 0.0);

        public bool Equals(MathPlaneObject? o) =>
            o is not null && Kind == o.Kind && Name == o.Name && Color == o.Color
            && Visible == o.Visible && ShowLabel == o.ShowLabel && HasFrom == o.HasFrom
            && Coords.SequenceEqual(o.Coords);

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
        public GridLabelSettings Labels { get; init; } = GridLabelSettings.Default;
        public IReadOnlyList<MathPlaneObject> Objects { get; init; } = Array.Empty<MathPlaneObject>();

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
            && Labels == o.Labels && Objects.SequenceEqual(o.Objects);

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
            if (Labels != GridLabelSettings.Default) parts.Add("labels " + LabelsToken(Labels));
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
            sb.Append(o.Kind.ToString().ToLowerInvariant()).Append(' ').Append(o.Name);
            int n = o.Kind == MathPlaneObjectKind.Vector ? 2 : o.Coords.Count;
            for (int i = 0; i < n; i++) sb.Append(' ').Append(Num(o.Coords[i]));
            if (o.Kind == MathPlaneObjectKind.Vector && o.HasFrom)
                sb.Append(" from ").Append(Num(o.Coords[2])).Append(' ').Append(Num(o.Coords[3]));
            sb.Append(" #").Append((o.Color & 0xFFFFFF).ToString("X6", Inv));
            if (!o.Visible) sb.Append(" hidden");
            if (!o.ShowLabel) sb.Append(" nolabel");
            return sb.ToString();
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
            var labels = GridLabelSettings.Default;
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
                    {
                        if (toks.Count != 2 || !TryOnOff(toks[1].Text, out bool on))
                        { error = Err(1, $"'{head}' takes on or off"); return false; }
                        if (head == "axes") axes = on; else if (head == "grid") grid = on; else ticks = on;
                        break;
                    }
                    case "labels":
                    {
                        if (toks.Count != 2 || !TryLabels(toks[1].Text, out labels))
                        { error = Err(1, "'labels' takes auto, offset, fixed:N or sci:N (N 0-30; sci needs 1+)"); return false; }
                        break;
                    }
                    case "point":
                    case "vector":
                    case "segment":
                    case "polyline":
                    {
                        if (!TryObject(head, toks, objects.Count, out var obj, out int badTok, out string msg))
                        { error = Err(badTok, msg); return false; }
                        if (!names.Add(obj!.Name))
                        { error = Err(1, $"name '{obj.Name}' is already used"); return false; }
                        objects.Add(obj);
                        break;
                    }
                    default:
                        error = Err(0, $"unknown statement '{toks[0].Text}' (expected point, vector, segment, polyline, axes, grid, ticks or labels)");
                        return false;
                }
            }

            scene = new MathPlaneScene { ShowAxes = axes, ShowGrid = grid, ShowTicks = ticks, Labels = labels, Objects = objects };
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

            uint color = DefaultPalette[index % DefaultPalette.Length];
            bool visible = true, showLabel = true;
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
            obj = new MathPlaneObject(kind, toks[1].Text, coords, color, visible, showLabel, hasFrom);
            return true;
        }

        private static IEnumerable<(string Text, int Start)> SplitStatements(string text)
        {
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == ';' || text[i] == '\n' || text[i] == '\r')
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
