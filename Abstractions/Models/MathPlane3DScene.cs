// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlane3DScene.cs
//
// #1713 (Math Plane 3D T1, #1556) — the Math Plane 3D scene: display settings and
// objects in space, stored as ONE text parameter (FractalParameters.MathPlane3DScene),
// so a region or `--param MathPlane3DScene=...` carries it. The tokenizer, name rules,
// number and label parsing are the 2D format's (MathPlaneScene); the statements are:
//
//   v1                                   format version (optional, first)
//   axes on|off   ticks on|off   labels auto|offset|fixed:N|sci:N
//   grid xy|xz|yz ... | grid off         which grid planes are drawn (default xy)
//   point    NAME x y z                       [options]
//   vector   NAME x y z [from x0 y0 z0]       [options]
//   segment  NAME x1 y1 z1 x2 y2 z2           [options]
//   polyline NAME x1 y1 z1 x2 y2 z2 ...       [options]   (two or more points)
//   options: #RRGGBB  #RRGGBBAA  hidden  nolabel
//
// Surfaces, space curves (T2, #1714) and maps (T3, #1715) come later.
// Design: Docs/Technical/MathPlane3D-DesignPlan.md §3.2.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FracturingFog.Models
{
    /// <summary>#1713 — which grid planes a Math Plane 3D scene draws.</summary>
    [Flags]
    public enum MathPlane3DGrid { None = 0, XY = 1, XZ = 2, YZ = 4 }

    /// <summary>#1713 — the Math Plane 3D scene. See the file header for the text format.
    /// Objects reuse <see cref="MathPlaneObject"/> with (x, y, z) triples in
    /// <see cref="MathPlaneObject.Coords"/>.</summary>
    public sealed record MathPlane3DScene
    {
        public const int FormatVersion = 1;

        /// <summary>A new Math Plane 3D view: the three basis vectors.</summary>
        public const string DefaultText = "v1; vector i 1 0 0 #E69F00; vector j 0 1 0 #56B4E9; vector k 0 0 1 #009E73";

        public bool ShowAxes { get; init; } = true;
        public bool ShowTicks { get; init; } = true;
        public MathPlane3DGrid Grid { get; init; } = MathPlane3DGrid.XY;
        public FracturingFog.ViewState.GridLabelSettings Labels { get; init; } = FracturingFog.ViewState.GridLabelSettings.Default;
        public IReadOnlyList<MathPlaneObject> Objects { get; init; } = Array.Empty<MathPlaneObject>();

        public bool Equals(MathPlane3DScene? o) =>
            o is not null && ShowAxes == o.ShowAxes && ShowTicks == o.ShowTicks && Grid == o.Grid
            && Labels == o.Labels && Objects.SequenceEqual(o.Objects);

        public override int GetHashCode() => HashCode.Combine(ShowAxes, ShowTicks, Grid, Objects.Count);

        /// <summary>Point <paramref name="i"/> of an object (x, y, z).</summary>
        public static (double X, double Y, double Z) Point(MathPlaneObject o, int i)
            => (o.Coords[3 * i], o.Coords[3 * i + 1], o.Coords[3 * i + 2]);

        /// <summary>A vector's tail: its <c>from</c> point, else the origin.</summary>
        public static (double X, double Y, double Z) Tail(MathPlaneObject o)
            => o.HasFrom ? (o.Coords[3], o.Coords[4], o.Coords[5]) : (0.0, 0.0, 0.0);

        /// <summary>A vector's tip (tail + components).</summary>
        public static (double X, double Y, double Z) Tip(MathPlaneObject o)
        {
            var (tx, ty, tz) = Tail(o);
            return (tx + o.Coords[0], ty + o.Coords[1], tz + o.Coords[2]);
        }

        // ── Serialise ────────────────────────────────────────────────────────

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>One-line text form; <see cref="TryParse"/> reads it back to an equal scene.</summary>
        public string Serialize()
        {
            var parts = new List<string> { "v" + FormatVersion };
            if (!ShowAxes) parts.Add("axes off");
            if (!ShowTicks) parts.Add("ticks off");
            if (Grid != MathPlane3DGrid.XY) parts.Add("grid " + GridToken(Grid));
            if (Labels != FracturingFog.ViewState.GridLabelSettings.Default) parts.Add("labels " + MathPlaneScene.LabelsToken(Labels));
            foreach (var o in Objects) parts.Add(SerializeObject(o));
            return string.Join("; ", parts);
        }

        private static string GridToken(MathPlane3DGrid g)
        {
            if (g == MathPlane3DGrid.None) return "off";
            var t = new List<string>();
            if ((g & MathPlane3DGrid.XY) != 0) t.Add("xy");
            if ((g & MathPlane3DGrid.XZ) != 0) t.Add("xz");
            if ((g & MathPlane3DGrid.YZ) != 0) t.Add("yz");
            return string.Join(' ', t);
        }

        private static string SerializeObject(MathPlaneObject o)
        {
            var sb = new StringBuilder(o.Kind.ToString().ToLowerInvariant()).Append(' ').Append(o.Name);
            int n = o.Kind == MathPlaneObjectKind.Vector ? 3 : o.Coords.Count;
            for (int i = 0; i < n; i++) sb.Append(' ').Append(MathPlaneScene.Num(o.Coords[i]));
            if (o.Kind == MathPlaneObjectKind.Vector && o.HasFrom)
                sb.Append(" from ").Append(MathPlaneScene.Num(o.Coords[3])).Append(' ')
                  .Append(MathPlaneScene.Num(o.Coords[4])).Append(' ').Append(MathPlaneScene.Num(o.Coords[5]));
            uint a = o.Color >> 24;
            sb.Append(" #").Append((o.Color & 0xFFFFFF).ToString("X6", Inv));
            if (a != 0xFF) sb.Append(a.ToString("X2", Inv));
            if (!o.Visible) sb.Append(" hidden");
            if (!o.ShowLabel) sb.Append(" nolabel");
            return sb.ToString();
        }

        // ── Parse ────────────────────────────────────────────────────────────

        /// <summary>Parse the text form. Empty or blank text is the empty scene. On failure
        /// returns false with the first error (statement and column) and an empty scene.</summary>
        public static bool TryParse(string? text, out MathPlane3DScene scene, out MathPlaneSceneError? error)
        {
            scene = new MathPlane3DScene();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            bool axes = true, ticks = true;
            var grid = MathPlane3DGrid.XY;
            var labels = FracturingFog.ViewState.GridLabelSettings.Default;
            var objects = new List<MathPlaneObject>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            int stmtNo = 0;
            foreach (var (stmtText, stmtStart) in MathPlaneScene.SplitStatements(text))
            {
                var toks = MathPlaneScene.Tokenize(stmtText, stmtStart);
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
                    case "ticks":
                    {
                        if (toks.Count != 2 || !MathPlaneScene.TryOnOff(toks[1].Text, out bool on))
                        { error = Err(1, $"'{head}' takes on or off"); return false; }
                        if (head == "axes") axes = on; else ticks = on;
                        break;
                    }
                    case "labels":
                        if (toks.Count != 2 || !MathPlaneScene.TryLabels(toks[1].Text, out labels))
                        { error = Err(1, "'labels' takes auto, offset, fixed:N or sci:N (N 0-30; sci needs 1+)"); return false; }
                        break;
                    case "grid":
                    {
                        if (toks.Count < 2) { error = Err(1, "'grid' takes xy, xz and / or yz, or off"); return false; }
                        var g = MathPlane3DGrid.None;
                        for (int k = 1; k < toks.Count; k++)
                        {
                            string v = toks[k].Text.ToLowerInvariant();
                            if (v == "off" && toks.Count == 2) { g = MathPlane3DGrid.None; break; }
                            var plane = v switch { "xy" => MathPlane3DGrid.XY, "xz" => MathPlane3DGrid.XZ, "yz" => MathPlane3DGrid.YZ, _ => (MathPlane3DGrid?)null };
                            if (plane is not { } pl) { error = Err(k, "'grid' takes xy, xz and / or yz, or off"); return false; }
                            g |= pl;
                        }
                        grid = g;
                        break;
                    }
                    case "point":
                    case "vector":
                    case "segment":
                    case "polyline":
                    {
                        if (!TryObject(head, toks, objects.Count, out var obj, out int badTok, out string msg))
                        { error = Err(badTok, msg); return false; }
                        if (!names.Add(obj!.Name)) { error = Err(1, $"name '{obj.Name}' is already used"); return false; }
                        objects.Add(obj);
                        break;
                    }
                    default:
                        error = Err(0, $"unknown statement '{toks[0].Text}' (expected point, vector, segment, polyline, axes, grid, ticks or labels)");
                        return false;
                }
            }

            scene = new MathPlane3DScene { ShowAxes = axes, ShowTicks = ticks, Grid = grid, Labels = labels, Objects = objects };
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
            if (toks.Count < 2 || !MathPlaneScene.IsName(toks[1].Text))
            { badTok = 1; msg = $"'{head}' needs a name (letters, digits, '_', optional trailing ')"; return false; }

            int i = 2;
            var coords = new List<double>();
            while (i < toks.Count && MathPlaneScene.TryNum(toks[i].Text, out double v)) { coords.Add(v); i++; }

            bool hasFrom = false;
            if (kind == MathPlaneObjectKind.Vector && i < toks.Count && toks[i].Text.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                if (coords.Count != 3) { badTok = i; msg = "'from' comes after the vector's x y z"; return false; }
                var f = new List<double>();
                for (int k = 1; k <= 3; k++)
                {
                    if (i + k >= toks.Count || !MathPlaneScene.TryNum(toks[i + k].Text, out double fv))
                    { badTok = Math.Min(i + k, toks.Count - 1); msg = "'from' needs x y z"; return false; }
                    f.Add(fv);
                }
                coords.AddRange(f); hasFrom = true; i += 4;
            }

            int fixedCount = kind switch
            {
                MathPlaneObjectKind.Point => 3,
                MathPlaneObjectKind.Vector => hasFrom ? 6 : 3,
                MathPlaneObjectKind.Segment => 6,
                _ => -1,
            };
            bool countOk = fixedCount > 0 ? coords.Count == fixedCount : coords.Count >= 6 && coords.Count % 3 == 0;
            if (!countOk)
            {
                badTok = Math.Min(i, toks.Count - 1);
                msg = kind == MathPlaneObjectKind.Polyline
                    ? "a polyline needs two or more x y z points"
                    : $"'{head}' needs {(kind == MathPlaneObjectKind.Segment ? 6 : 3)} numbers (x y z{(kind == MathPlaneObjectKind.Segment ? " x y z" : "")}), got {coords.Count}";
                return false;
            }

            uint color = MathPlaneScene.DefaultPalette[index % MathPlaneScene.DefaultPalette.Length];
            bool visible = true, label = true;
            for (; i < toks.Count; i++)
            {
                string t = toks[i].Text;
                if (t.StartsWith('#') && (t.Length == 7 || t.Length == 9)
                    && uint.TryParse(t.AsSpan(1), NumberStyles.HexNumber, Inv, out uint rgb))
                    color = t.Length == 7 ? 0xFF000000u | rgb : ((rgb & 0xFF) << 24) | (rgb >> 8);
                else if (t.Equals("hidden", StringComparison.OrdinalIgnoreCase)) visible = false;
                else if (t.Equals("nolabel", StringComparison.OrdinalIgnoreCase)) label = false;
                else
                {
                    badTok = i;
                    msg = MathPlaneScene.TryNum(t, out _) ? "too many numbers" : $"unknown option '{t}' (expected #RRGGBB, #RRGGBBAA, hidden or nolabel)";
                    return false;
                }
            }
            obj = new MathPlaneObject(kind, toks[1].Text, coords, color, visible, label, hasFrom);
            return true;
        }
    }
}
