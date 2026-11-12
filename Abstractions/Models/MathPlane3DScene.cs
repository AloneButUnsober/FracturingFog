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
// #1714 (T2) adds surfaces, space curves, shading and translucent grid fills:
//   curve    NAME "x(t)" "y(t)" "z(t)" tmin tmax                 [options]
//   surface  NAME explicit "f(x, y)" [xmin xmax ymin ymax]        [options] [mesh N]
//   surface  NAME parametric "x(u,v)" "y(u,v)" "z(u,v)" umin umax vmin vmax  [options] [mesh N]
//   surface  NAME implicit "F(x, y, z)" [xmin xmax ymin ymax zmin zmax]       [options] [mesh N]   (#1717)
//   shading smooth|flat                  Gouraud (default) or flat surface shading
//   fill xy|xz|yz ... | fill off         translucent filled grid planes (default off)
// An explicit surface without a domain spans [-3, 3] x [-3, 3]; an implicit one without a
// box spans [-3, 3]^3 (#1717), meshed by marching cubes on mesh N cells a side. mesh N is the cells a
// side (2-256, default 64). A #RRGGBBAA surface is drawn in the translucent pass.
//
// #1715 (T3) adds the map read-outs (the map itself is params, MathPlane3DMap):
//   det on|off       the unit cube's image as a shaded parallelepiped, with det
//   eigen on|off     the real eigenvectors as lines through the origin, with eigenvalues
//   lengths on|off   vector lengths after the map
// Design: Docs/Technical/MathPlane3D-DesignPlan.md §3.2.
//
// #1731 (fractal bridge B1) adds a 3D fractal as a surface, meshed from its distance estimate:
//   fractal NAME TYPE [KEY=VALUE ...] [options] [mesh N]
// TYPE is a mesh-exportable 3D type (MeshExportCapabilities). KEY=VALUE are --param keys (the
// region snapshot's), checked by the Engine when the mesh is built; values have no spaces,
// quotes, ';' or a leading '@' / '#'. Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.1.

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
        /// <summary>#1714 — flat shading instead of Gouraud.</summary>
        public bool FlatShading { get; init; }
        /// <summary>#1715 — map read-outs.</summary>
        public bool ShowDet { get; init; }
        public bool ShowEigen { get; init; }
        public bool ShowLengths { get; init; }
        /// <summary>#1714 — grid planes filled with a translucent tint.</summary>
        public MathPlane3DGrid Fill { get; init; } = MathPlane3DGrid.None;
        public IReadOnlyList<MathPlaneObject> Objects { get; init; } = Array.Empty<MathPlaneObject>();

        /// <summary>#1714 — surface resolution: cells a side.</summary>
        public const int DefaultMesh = 64, MinMesh = 2, MaxMesh = 256;
        /// <summary>#1714 — half-size of an explicit surface's default domain.</summary>
        public const double DefaultSurfaceHalfSize = 3;

        public bool Equals(MathPlane3DScene? o) =>
            o is not null && ShowAxes == o.ShowAxes && ShowTicks == o.ShowTicks && Grid == o.Grid
            && Labels == o.Labels && FlatShading == o.FlatShading && Fill == o.Fill
            && ShowDet == o.ShowDet && ShowEigen == o.ShowEigen && ShowLengths == o.ShowLengths && Objects.SequenceEqual(o.Objects);

        public override int GetHashCode() => HashCode.Combine(ShowAxes, ShowTicks, Grid, Objects.Count);

        /// <summary>Point <paramref name="i"/> of an object (x, y, z).</summary>
        /// <summary>#1714 — a surface's cells a side.</summary>
        public static int MeshOf(MathPlaneObject o) => o.Mesh > 0 ? o.Mesh : DefaultMesh;

        /// <summary>#1714 — a surface's parameter domain (x, y for explicit; u, v for parametric).</summary>
        public static (double A0, double A1, double B0, double B1) SurfaceDomain(MathPlaneObject o)
            => o.Coords.Count == 4 ? (o.Coords[0], o.Coords[1], o.Coords[2], o.Coords[3])
             : (-DefaultSurfaceHalfSize, DefaultSurfaceHalfSize, -DefaultSurfaceHalfSize, DefaultSurfaceHalfSize);

        /// <summary>#1717 — an implicit surface's box (xmin, xmax, ymin, ymax, zmin, zmax).</summary>
        public static (double X0, double X1, double Y0, double Y1, double Z0, double Z1) ImplicitBox(MathPlaneObject o)
        {
            const double h = DefaultSurfaceHalfSize;
            return o.Coords.Count == 6 ? (o.Coords[0], o.Coords[1], o.Coords[2], o.Coords[3], o.Coords[4], o.Coords[5])
                                       : (-h, h, -h, h, -h, h);
        }

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
            if (FlatShading) parts.Add("shading flat");
            if (ShowDet) parts.Add("det on");
            if (ShowEigen) parts.Add("eigen on");
            if (ShowLengths) parts.Add("lengths on");
            if (Fill != MathPlane3DGrid.None) parts.Add("fill " + GridToken(Fill));
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
            if (o.Kind == MathPlaneObjectKind.Surface && o.CurveKind == MathPlaneCurveKind.Fractal)
                return SerializeFractal(o);
            var sb = new StringBuilder(o.Kind.ToString().ToLowerInvariant()).Append(' ').Append(o.Name);
            if (o.Kind == MathPlaneObjectKind.Surface)
                sb.Append(' ').Append(o.CurveKind switch
                {
                    MathPlaneCurveKind.Parametric => "parametric",
                    MathPlaneCurveKind.Implicit => "implicit",
                    _ => "explicit",
                });
            foreach (var src in o.Expressions) sb.Append(" \"").Append(src).Append('"');
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
            if (o.Mesh > 0) sb.Append(" mesh ").Append(o.Mesh.ToString(Inv));
            return sb.ToString();
        }

        /// <summary>#1731 — <c>fractal NAME TYPE KEY=VALUE ... options</c> (no quotes).</summary>
        private static string SerializeFractal(MathPlaneObject o)
        {
            var sb = new StringBuilder("fractal ").Append(o.Name);
            foreach (var src in o.Expressions) sb.Append(' ').Append(src);
            uint a = o.Color >> 24;
            sb.Append(" #").Append((o.Color & 0xFFFFFF).ToString("X6", Inv));
            if (a != 0xFF) sb.Append(a.ToString("X2", Inv));
            if (!o.Visible) sb.Append(" hidden");
            if (!o.ShowLabel) sb.Append(" nolabel");
            if (o.Mesh > 0) sb.Append(" mesh ").Append(o.Mesh.ToString(Inv));
            return sb.ToString();
        }

        /// <summary>#1731 — a fractal object's type (null when it is not one).</summary>
        public static FractalType? FractalTypeOf(MathPlaneObject o)
            => o.Kind == MathPlaneObjectKind.Surface && o.CurveKind == MathPlaneCurveKind.Fractal && o.Expressions.Count > 0
               && FractalTypeNames.TryParse(o.Expressions[0], true, out var t) ? t : null;

        /// <summary>#1731 — a fractal object's KEY=VALUE pairs, in order.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> FractalKeys(MathPlaneObject o)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int k = 1; k < o.Expressions.Count; k++)
            {
                string s = o.Expressions[k];
                int eq = s.IndexOf('=');
                if (eq > 0) list.Add(new(s[..eq], s[(eq + 1)..]));
            }
            return list;
        }

        /// <summary>#1731 — the types a fractal object can be, in the menu's order.</summary>
        public static IReadOnlyList<FractalType> FractalTypes => MeshExportCapabilities.Types;

        /// <summary>#1731 — true when <paramref name="token"/> is a KEY=VALUE pair a scene can hold.</summary>
        public static bool IsFractalKeyToken(string token)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1 || !char.IsLetter(token[0])) return false;
            for (int k = 0; k < eq; k++) if (!char.IsLetterOrDigit(token[k])) return false;
            char v0 = token[eq + 1];
            if (v0 is '@' or '#') return false;
            for (int k = eq + 1; k < token.Length; k++)
                if (token[k] is '"' or ';' or '=' || char.IsWhiteSpace(token[k])) return false;
            return true;
        }

        // ── Parse ────────────────────────────────────────────────────────────

        /// <summary>Parse the text form. Empty or blank text is the empty scene. On failure
        /// returns false with the first error (statement and column) and an empty scene.</summary>
        public static bool TryParse(string? text, out MathPlane3DScene scene, out MathPlaneSceneError? error)
        {
            scene = new MathPlane3DScene();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            bool axes = true, ticks = true, flat = false, det = false, eigen = false, lengths = false;
            var grid = MathPlane3DGrid.XY;
            var fill = MathPlane3DGrid.None;
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
                    case "det":
                    case "eigen":
                    case "lengths":
                    {
                        if (toks.Count != 2 || !MathPlaneScene.TryOnOff(toks[1].Text, out bool on))
                        { error = Err(1, $"'{head}' takes on or off"); return false; }
                        switch (head)
                        {
                            case "axes": axes = on; break;
                            case "ticks": ticks = on; break;
                            case "det": det = on; break;
                            case "eigen": eigen = on; break;
                            default: lengths = on; break;
                        }
                        break;
                    }
                    case "labels":
                        if (toks.Count != 2 || !MathPlaneScene.TryLabels(toks[1].Text, out labels))
                        { error = Err(1, "'labels' takes auto, offset, fixed:N or sci:N (N 0-30; sci needs 1+)"); return false; }
                        break;
                    case "grid":
                    case "fill":
                    {
                        string usage = $"'{head}' takes xy, xz and / or yz, or off";
                        if (toks.Count < 2) { error = Err(1, usage); return false; }
                        var g = MathPlane3DGrid.None;
                        for (int k = 1; k < toks.Count; k++)
                        {
                            string v = toks[k].Text.ToLowerInvariant();
                            if (v == "off" && toks.Count == 2) { g = MathPlane3DGrid.None; break; }
                            var plane = v switch { "xy" => MathPlane3DGrid.XY, "xz" => MathPlane3DGrid.XZ, "yz" => MathPlane3DGrid.YZ, _ => (MathPlane3DGrid?)null };
                            if (plane is not { } pl) { error = Err(k, usage); return false; }
                            g |= pl;
                        }
                        if (head == "grid") grid = g; else fill = g;
                        break;
                    }
                    case "shading":
                    {
                        string v = toks.Count == 2 ? toks[1].Text.ToLowerInvariant() : "";
                        if (v != "smooth" && v != "flat") { error = Err(1, "'shading' takes smooth or flat"); return false; }
                        flat = v == "flat";
                        break;
                    }
                    case "point":
                    case "vector":
                    case "segment":
                    case "polyline":
                    case "curve":
                    case "surface":
                    case "fractal":
                    {
                        MathPlaneObject? obj; int badTok; string msg;
                        bool ok = head == "curve" ? TryCurve(toks, objects.Count, out obj, out badTok, out msg)
                                : head == "surface" ? TrySurface(toks, objects.Count, out obj, out badTok, out msg)
                                : head == "fractal" ? TryFractal(toks, objects.Count, out obj, out badTok, out msg)
                                : TryObject(head, toks, objects.Count, out obj, out badTok, out msg);
                        if (!ok) { error = Err(badTok, msg); return false; }
                        if (!names.Add(obj!.Name)) { error = Err(1, $"name '{obj.Name}' is already used"); return false; }
                        objects.Add(obj);
                        break;
                    }
                    default:
                        error = Err(0, $"unknown statement '{toks[0].Text}' (expected point, vector, segment, polyline, curve, surface, fractal, axes, grid, fill, shading, ticks, labels, det, eigen or lengths)");
                        return false;
                }
            }

            scene = new MathPlane3DScene { ShowAxes = axes, ShowTicks = ticks, Grid = grid, Labels = labels, FlatShading = flat, Fill = fill,
                                       ShowDet = det, ShowEigen = eigen, ShowLengths = lengths, Objects = objects };
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

            if (!TryOptions(toks, i, index, allowMesh: false, out uint color, out bool visible, out bool label, out _, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(kind, toks[1].Text, coords, color, visible, label, hasFrom);
            return true;
        }

        /// <summary>Options from token <paramref name="i"/> on: colour, hidden, nolabel and,
        /// for a surface, <c>mesh N</c> (0 = not given).</summary>
        private static bool TryOptions(List<(string Text, int Col)> toks, int i, int index, bool allowMesh,
                                       out uint color, out bool visible, out bool label, out int mesh,
                                       out int badTok, out string msg)
        {
            color = MathPlaneScene.DefaultPalette[index % MathPlaneScene.DefaultPalette.Length];
            visible = true; label = true; mesh = 0; badTok = 0; msg = "";
            string expected = allowMesh ? "#RRGGBB, #RRGGBBAA, hidden, nolabel or mesh N" : "#RRGGBB, #RRGGBBAA, hidden or nolabel";
            for (; i < toks.Count; i++)
            {
                string t = toks[i].Text;
                if (t.StartsWith('#') && (t.Length == 7 || t.Length == 9)
                    && uint.TryParse(t.AsSpan(1), NumberStyles.HexNumber, Inv, out uint rgb))
                    color = t.Length == 7 ? 0xFF000000u | rgb : ((rgb & 0xFF) << 24) | (rgb >> 8);
                else if (t.Equals("hidden", StringComparison.OrdinalIgnoreCase)) visible = false;
                else if (t.Equals("nolabel", StringComparison.OrdinalIgnoreCase)) label = false;
                else if (allowMesh && t.Equals("mesh", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= toks.Count || !int.TryParse(toks[i + 1].Text, NumberStyles.None, Inv, out mesh)
                        || mesh < MinMesh || mesh > MaxMesh)
                    { badTok = Math.Min(i + 1, toks.Count - 1); msg = $"'mesh' takes a whole number of cells, {MinMesh}-{MaxMesh}"; return false; }
                    i++;
                }
                else
                {
                    badTok = i;
                    msg = MathPlaneScene.TryNum(t, out _) ? "too many numbers" : $"unknown option '{t}' (expected {expected})";
                    return false;
                }
            }
            return true;
        }

        /// <summary>#1714 — quoted expressions from token <paramref name="i"/>.</summary>
        private static bool TryExpressions(List<(string Text, int Col)> toks, ref int i, int count, string[] allowed,
                                           string what, string usage, List<string> sources, out int badTok, out string msg)
        {
            badTok = 0; msg = "";
            for (int k = 0; k < count; k++, i++)
            {
                if (i >= toks.Count || !toks[i].Text.StartsWith('"')) { badTok = Math.Min(i, toks.Count - 1); msg = usage; return false; }
                if (!MathPlaneScene.TryExpression(toks[i].Text, allowed, what, out string src, out msg)) { badTok = i; return false; }
                sources.Add(src);
            }
            return true;
        }

        /// <summary>#1714 — <c>curve NAME "x(t)" "y(t)" "z(t)" tmin tmax [options]</c>.</summary>
        private static bool TryCurve(List<(string Text, int Col)> toks, int index,
                                     out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            if (toks.Count < 2 || !MathPlaneScene.IsName(toks[1].Text))
            { badTok = 1; msg = "'curve' needs a name (letters, digits, '_', optional trailing ')"; return false; }
            int i = 2;
            var sources = new List<string>();
            if (!TryExpressions(toks, ref i, 3, new[] { "t" }, "a space curve",
                                "a space curve needs three quoted expressions, \"x(t)\" \"y(t)\" \"z(t)\"", sources, out badTok, out msg))
                return false;
            if (!TryRange(toks, ref i, 1, "a space curve needs tmin tmax", out var domain, out badTok, out msg)) return false;
            if (!TryOptions(toks, i, index, allowMesh: false, out uint color, out bool visible, out bool label, out _, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(MathPlaneObjectKind.Curve, toks[1].Text, domain, color, visible, label,
                                      CurveKind: MathPlaneCurveKind.Parametric, Sources: sources);
            return true;
        }

        /// <summary>#1714 — <c>surface NAME explicit "f(x, y)" [xmin xmax ymin ymax]</c>,
        /// <c>surface NAME parametric "x(u,v)" "y(u,v)" "z(u,v)" umin umax vmin vmax</c> or (#1717)
        /// <c>surface NAME implicit "F(x, y, z)" [xmin xmax ymin ymax zmin zmax]</c>, then options.</summary>
        private static bool TrySurface(List<(string Text, int Col)> toks, int index,
                                       out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            if (toks.Count < 2 || !MathPlaneScene.IsName(toks[1].Text))
            { badTok = 1; msg = "'surface' needs a name (letters, digits, '_', optional trailing ')"; return false; }
            string kindText = toks.Count > 2 ? toks[2].Text.ToLowerInvariant() : "";
            if (kindText != "explicit" && kindText != "parametric" && kindText != "implicit")
            { badTok = Math.Min(2, toks.Count - 1); msg = "'surface' needs a kind: explicit, parametric or implicit"; return false; }
            bool param = kindText == "parametric", implicitF = kindText == "implicit";
            int i = 3;
            var sources = new List<string>();
            bool ok = param
                ? TryExpressions(toks, ref i, 3, new[] { "u", "v" }, "a parametric surface",
                                 "a parametric surface needs three quoted expressions, \"x(u,v)\" \"y(u,v)\" \"z(u,v)\"", sources, out badTok, out msg)
                : implicitF
                ? TryExpressions(toks, ref i, 1, new[] { "x", "y", "z" }, "an implicit surface",
                                 "an implicit surface needs a quoted expression F(x, y, z), e.g. \"x^2 + y^2 + z^2 - 1\"", sources, out badTok, out msg)
                : TryExpressions(toks, ref i, 1, new[] { "x", "y" }, "an explicit surface",
                                 "an explicit surface needs a quoted expression in x and y, e.g. \"x^2 - y^2\"", sources, out badTok, out msg);
            if (!ok) return false;
            var domain = new List<double>();
            bool hasRange = i < toks.Count && MathPlaneScene.TryNum(toks[i].Text, out _);
            if (param || hasRange)
            {
                string need = param ? "a parametric surface needs umin umax vmin vmax"
                            : implicitF ? "the box needs xmin xmax ymin ymax zmin zmax"
                            : "the domain needs xmin xmax ymin ymax";
                if (!TryRange(toks, ref i, implicitF ? 3 : 2, need, out domain, out badTok, out msg)) return false;
            }
            if (!TryOptions(toks, i, index, allowMesh: true, out uint color, out bool visible, out bool label, out int mesh, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(MathPlaneObjectKind.Surface, toks[1].Text, domain, color, visible, label,
                                      CurveKind: param ? MathPlaneCurveKind.Parametric
                                               : implicitF ? MathPlaneCurveKind.Implicit : MathPlaneCurveKind.Explicit,
                                      Sources: sources, Mesh: mesh);
            return true;
        }

        /// <summary>#1731 — <c>fractal NAME TYPE [KEY=VALUE ...] [options] [mesh N]</c>.</summary>
        private static bool TryFractal(List<(string Text, int Col)> toks, int index,
                                       out MathPlaneObject? obj, out int badTok, out string msg)
        {
            obj = null; badTok = 0; msg = "";
            if (toks.Count < 2 || !MathPlaneScene.IsName(toks[1].Text))
            { badTok = 1; msg = "'fractal' needs a name (letters, digits, '_', optional trailing ')"; return false; }
            string typeList = string.Join(", ", FractalTypes);
            if (toks.Count < 3 || !FractalTypeNames.TryParse(toks[2].Text, true, out var type)
                || !MeshExportCapabilities.IsMeshExportable(type))
            { badTok = Math.Min(2, toks.Count - 1); msg = $"'fractal' needs a 3D type: {typeList}"; return false; }
            var sources = new List<string> { type.ToString() };
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 3;
            for (; i < toks.Count && toks[i].Text.Contains('='); i++)
            {
                string t = toks[i].Text;
                if (!IsFractalKeyToken(t))
                { badTok = i; msg = "a fractal setting is KEY=VALUE, e.g. BulbPower=8 (no spaces, quotes or ';')"; return false; }
                string key = t[..t.IndexOf('=')];
                if (!keys.Add(key)) { badTok = i; msg = $"'{key}' is given twice"; return false; }
                if (key.StartsWith("Cam3D", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MathPlane", StringComparison.OrdinalIgnoreCase))
                { badTok = i; msg = $"'{key}' is a view setting, not one of the fractal's"; return false; }
                sources.Add(t);
            }
            if (!TryOptions(toks, i, index, allowMesh: true, out uint color, out bool visible, out bool label, out int mesh, out badTok, out msg))
                return false;
            obj = new MathPlaneObject(MathPlaneObjectKind.Surface, toks[1].Text, Array.Empty<double>(), color, visible, label,
                                      CurveKind: MathPlaneCurveKind.Fractal, Sources: sources, Mesh: mesh);
            return true;
        }

        /// <summary>#1714 — <paramref name="pairs"/> (min, max) pairs, each max above its min.</summary>
        private static bool TryRange(List<(string Text, int Col)> toks, ref int i, int pairs, string need,
                                     out List<double> range, out int badTok, out string msg)
        {
            range = new List<double>(); badTok = 0; msg = "";
            for (int k = 0; k < 2 * pairs; k++, i++)
            {
                if (i >= toks.Count || !MathPlaneScene.TryNum(toks[i].Text, out double v))
                { badTok = Math.Min(i, toks.Count - 1); msg = need; return false; }
                range.Add(v);
                if (k % 2 == 1 && !(v > range[k - 1])) { badTok = i; msg = "a range's max must be above its min"; return false; }
            }
            return true;
        }
    }
}
