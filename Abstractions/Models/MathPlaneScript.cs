// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneScript.cs
//
// #1695 (scripted scenes SS1, #1557) — the Math Plane script: timed steps in the
// scene text, evaluated as a pure function of script time s
// (FractalParameters.MathPlaneScriptTime). Design:
// Docs/Technical/MathPlane-ScriptedScenes-Plan.md §3.
//
//   step DURATION [ease none|in|out|inout] ACTION ARGS...   starts when the previous step ends
//   with [DURATION] [ease ...] ACTION ARGS...               runs alongside the step before it
//
//   wait | show NAME... | hide NAME... | t VALUE | matrix A B C D | c RE IM | move NAME X Y
//   draw NAME | orbit N | cobweb N | phase on|contours|off | caption "TEXT"   (#1696, SS2)
//
// #1696 reveals: `draw` grows a curve, segment, polyline or vector along its drawn
// length (Manim's "Create"); it makes the object visible when it begins, so it is
// meant for objects written `hidden` (a visible object jumps to undrawn as the draw
// starts). `orbit N` / `cobweb N` reveal steps up to N, one at a time, the newest
// fading in; they start from the scene's `orbit` / `cobweb` statement (0 without an
// orbit statement; cobweb needs its statement for x0 and f). `phase` fades the phase
// portrait in or out. `caption` cross-fades a screen-space caption with the last
// one; `caption ""` fades it out.
//
// A `with` without a duration / ease takes its step's. A step lasts as long as its
// longest member; the script's duration D is the end of the last step. Each member
// moves its properties from the value they have at its start (the authored value,
// or what an earlier member left) to its target; progress is
// ease(clamp((s - start) / duration)). Two members animating one property at
// overlapping times are a parse error, so the fold is unambiguous.
//
// The step model (members in succession, groups played together, each with a run
// time and an easing) follows Manim (see Docs/Resources-Bibliography.md#manim); no
// Manim code is used. The eases are CameraKey.ApplyEase (u², smoothstep), with
// inout the default.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Render;

namespace FracturingFog.Models
{
    /// <summary>#1695 — what a script member does.</summary>
    public enum MathPlaneStepAction { Wait, Show, Hide, T, Matrix, C, Move, Draw, Orbit, Cobweb, Phase, Caption }

    /// <summary>#1695 — one script member: a <c>step</c> (<see cref="With"/> false) or a
    /// <c>with</c>. <see cref="Duration"/> / <see cref="Ease"/> null on a <c>with</c> =
    /// its step's; on a <c>step</c> <see cref="Ease"/> null = inout.</summary>
    public sealed record MathPlaneStep(
        bool With,
        double? Duration,
        CameraEase? Ease,
        MathPlaneStepAction Action,
        IReadOnlyList<string> Names,
        IReadOnlyList<double> Values,
        string? Text = null)
    {
        /// <summary>#1696 — <c>phase</c>'s mode (on / contours / off) or <c>caption</c>'s text.</summary>
        public string? Text { get; init; } = Text;

        public bool Equals(MathPlaneStep? o) =>
            o is not null && With == o.With && Duration == o.Duration && Ease == o.Ease && Action == o.Action
            && Names.SequenceEqual(o.Names) && Values.SequenceEqual(o.Values) && Text == o.Text;

        public override int GetHashCode() => HashCode.Combine(With, Duration, Ease, Action, Names.Count, Values.Count);
    }

    /// <summary>#1695 — a member placed on the timeline.</summary>
    public readonly record struct MathPlaneTimedStep(int Index, MathPlaneStep Step, double Start, double Duration, CameraEase Ease)
    {
        public double End => Start + Duration;
    }

    /// <summary>#1696 — what the reveal actions leave for the drawer: how much of each
    /// object is drawn (0..1; absent = all), the orbit / cobweb steps shown (fractional:
    /// the newest step fades in; null = the scene's own count), the phase portrait's
    /// opacity, and the caption (and the one it replaces) with their opacities.</summary>
    public sealed record MathPlaneReveal(
        IReadOnlyDictionary<string, double> Draw,
        double? Orbit,
        double? Cobweb,
        double PhaseAlpha,
        string? Caption,
        double CaptionAlpha,
        string? PreviousCaption,
        double PreviousCaptionAlpha)
    {
        public static readonly MathPlaneReveal None = new(
            new Dictionary<string, double>(), null, null, 1, null, 0, null, 0);

        /// <summary>How much of <paramref name="name"/> is drawn (1 = all).</summary>
        public double DrawnFraction(string name) => Draw.TryGetValue(name, out double f) ? f : 1;
    }

    /// <summary>#1695 — the scene and params at one script time; #1696 adds the reveal state.</summary>
    public sealed record MathPlaneScriptState(MathPlaneScene Scene, FractalParameters Params, double Time, double Duration,
                                              MathPlaneReveal? Reveal = null);

    public static class MathPlaneScript
    {
        public const double MaxStepSeconds = 600;
        public const CameraEase DefaultEase = CameraEase.EaseInOut;

        /// <summary>Place every member on the timeline, in script order.</summary>
        public static IReadOnlyList<MathPlaneTimedStep> Layout(IReadOnlyList<MathPlaneStep> steps, out double duration)
        {
            var list = new List<MathPlaneTimedStep>(steps.Count);
            double cursor = 0, start = 0, stepDur = 0, groupEnd = 0;
            var stepEase = DefaultEase;
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (!s.With)
                {
                    cursor = groupEnd;
                    start = cursor;
                    stepDur = s.Duration ?? 0;
                    stepEase = s.Ease ?? DefaultEase;
                    groupEnd = start + stepDur;
                    list.Add(new MathPlaneTimedStep(i, s, start, stepDur, stepEase));
                }
                else
                {
                    double d = s.Duration ?? stepDur;
                    list.Add(new MathPlaneTimedStep(i, s, start, d, s.Ease ?? stepEase));
                    groupEnd = Math.Max(groupEnd, start + d);
                }
            }
            duration = groupEnd;
            return list;
        }

        /// <summary>The script's duration (0 without steps).</summary>
        public static double Duration(MathPlaneScene scene)
        {
            Layout(scene.Steps, out double d);
            return d;
        }

        /// <summary>The properties a member animates (for the overlap check and the fold).</summary>
        public static IEnumerable<string> Properties(MathPlaneStep s) => s.Action switch
        {
            MathPlaneStepAction.T => new[] { "t" },
            MathPlaneStepAction.Matrix => new[] { "matrix" },
            MathPlaneStepAction.C => new[] { "c" },
            MathPlaneStepAction.Show or MathPlaneStepAction.Hide => s.Names.Select(n => "alpha:" + n),
            MathPlaneStepAction.Move => new[] { "pos:" + s.Names[0] },
            MathPlaneStepAction.Draw => new[] { "reveal:" + s.Names[0], "alpha:" + s.Names[0] },
            MathPlaneStepAction.Orbit => new[] { "orbit" },
            MathPlaneStepAction.Cobweb => new[] { "cobweb" },
            MathPlaneStepAction.Phase => new[] { "phase" },
            MathPlaneStepAction.Caption => new[] { "caption" },
            _ => Array.Empty<string>(),
        };

        /// <summary>Two members animating one property at overlapping times, or null.
        /// Zero-length members (cuts) overlap only a member running across them.</summary>
        public static (MathPlaneTimedStep First, MathPlaneTimedStep Second, string Property)? FindOverlap(
            IReadOnlyList<MathPlaneStep> steps)
        {
            var timed = Layout(steps, out _);
            var last = new Dictionary<string, List<MathPlaneTimedStep>>(StringComparer.Ordinal);
            foreach (var m in timed)
                foreach (var prop in Properties(m.Step))
                {
                    if (!last.TryGetValue(prop, out var seen)) last[prop] = seen = new List<MathPlaneTimedStep>();
                    foreach (var o in seen)
                        if (m.Start < o.End && o.Start < m.End) return (o, m, prop);
                    seen.Add(m);
                }
            return null;
        }

        private static string PropertyLabel(string prop) => prop switch
        {
            "t" => "t", "matrix" => "the matrix", "c" => "c",
            "orbit" => "the orbit", "cobweb" => "the cobweb", "phase" => "the phase portrait", "caption" => "the caption",
            _ when prop.StartsWith("alpha:", StringComparison.Ordinal) => "the visibility of " + prop[6..],
            _ when prop.StartsWith("reveal:", StringComparison.Ordinal) => "the drawing of " + prop[7..],
            _ => "the position of " + prop[4..],
        };

        /// <summary>Why the steps don't fit the scene's objects, or null. Reports the member index.</summary>
        public static (int Index, string Message)? Validate(IReadOnlyList<MathPlaneStep> steps, IReadOnlyList<MathPlaneObject> objects,
                                                            bool hasMap = true, bool hasCobweb = true)
        {
            var byName = objects.ToDictionary(o => o.Name, StringComparer.Ordinal);
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (i == 0 && s.With) return (i, "'with' runs alongside a step, so a step must come first");
                if (s.Action == MathPlaneStepAction.Cobweb && !hasCobweb)
                    return (i, "the 'cobweb' action reveals the scene's cobweb: add a statement such as cobweb 0.1 0");
                if (s.Action == MathPlaneStepAction.Phase && s.Text != "off" && !hasMap)
                    return (i, "the phase portrait colours by the scene's map: add a statement such as map \"z^2 + c\"");
                foreach (var n in s.Names)
                {
                    if (!byName.TryGetValue(n, out var o)) return (i, $"no object called '{n}'");
                    if (s.Action == MathPlaneStepAction.Move && o.Kind is MathPlaneObjectKind.Curve or MathPlaneObjectKind.Region)
                        return (i, $"'move' takes a point, vector, segment or polyline; '{n}' is a {o.Kind.ToString().ToLowerInvariant()}");
                    if (s.Action == MathPlaneStepAction.Draw && !CanDraw(o))
                        return (i, $"'draw' reveals a curve, segment, polyline or vector along its length; use 'show' for '{n}'");
                }
            }
            if (FindOverlap(steps) is { } ov)
                return (ov.Second.Index, $"{PropertyLabel(ov.Property)} is already animated by an overlapping member (member {ov.First.Index + 1})");
            return null;
        }

        /// <summary>#1696 — objects <c>draw</c> can reveal along their length (not points, regions
        /// or implicit curves, whose pieces have no order).</summary>
        public static bool CanDraw(MathPlaneObject o) => o.Kind switch
        {
            MathPlaneObjectKind.Curve => o.CurveKind != MathPlaneCurveKind.Implicit,
            MathPlaneObjectKind.Segment or MathPlaneObjectKind.Polyline or MathPlaneObjectKind.Vector => true,
            _ => false,
        };

        /// <summary>Progress of a member at time <paramref name="s"/>: 0 before it starts, 1 after
        /// it ends, eased in between. A zero-length member is a cut at its start.</summary>
        public static double Progress(in MathPlaneTimedStep m, double s)
        {
            if (s < m.Start) return 0;
            if (m.Duration <= 0 || s >= m.End) return 1;
            return CameraKey.ApplyEase(m.Ease, (s - m.Start) / m.Duration);
        }

        /// <summary>The scene and params at script time <paramref name="s"/> (clamped to ≥ 0).
        /// Without steps returns the inputs unchanged. Never mutates <paramref name="p"/>.</summary>
        public static MathPlaneScriptState Evaluate(MathPlaneScene scene, FractalParameters p, double s)
        {
            if (!double.IsFinite(s) || s < 0) s = 0;
            var timed = Layout(scene.Steps, out double duration);
            if (timed.Count == 0) return new MathPlaneScriptState(scene, p, s, 0);

            // Current (end-of-last-member) values, and the values drawn at s.
            double t = p.MathPlaneT, tAt = t;
            var m = new[] { p.MathPlaneM11, p.MathPlaneM12, p.MathPlaneM21, p.MathPlaneM22 };
            var mAt = (double[])m.Clone();
            double cRe = p.MathPlaneCRe, cIm = p.MathPlaneCIm, cReAt = cRe, cImAt = cIm;
            bool touchesParams = false;

            var objects = scene.Objects;
            var alpha = new Dictionary<string, double>(StringComparer.Ordinal);
            var alphaAt = new Dictionary<string, double>(StringComparer.Ordinal);
            var pos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            var posAt = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            var byName = objects.ToDictionary(o => o.Name, StringComparer.Ordinal);

            // #1696 — reveals.
            var drawAt = new Dictionary<string, double>(StringComparer.Ordinal);
            double orbit = scene.OrbitSteps, cobweb = scene.Cobweb?.Steps ?? 0;
            double? orbitAt = null, cobwebAt = null;
            var phaseMode = scene.Phase;
            double phase = scene.Phase != MathPlanePhase.Off ? 1 : 0, phaseAt = phase;
            string? caption = null, captionAt = null, prevCaptionAt = null;
            double captionAlphaAt = 0, prevCaptionAlphaAt = 0;
            bool reveals = false;

            double Lerp(double a, double b, double u) => a + (b - a) * u;

            // Members in start order (stable: script order breaks ties).
            foreach (var mem in timed.OrderBy(x => x.Start))
            {
                var st = mem.Step;
                double u = Progress(mem, s);
                bool begun = s >= mem.Start;
                switch (st.Action)
                {
                    case MathPlaneStepAction.T:
                        touchesParams = true;
                        if (begun) tAt = Lerp(t, st.Values[0], u);
                        t = st.Values[0];
                        break;
                    case MathPlaneStepAction.Matrix:
                        touchesParams = true;
                        for (int k = 0; k < 4; k++)
                        {
                            if (begun) mAt[k] = Lerp(m[k], st.Values[k], u);
                            m[k] = st.Values[k];
                        }
                        break;
                    case MathPlaneStepAction.C:
                        touchesParams = true;
                        if (begun) { cReAt = Lerp(cRe, st.Values[0], u); cImAt = Lerp(cIm, st.Values[1], u); }
                        cRe = st.Values[0]; cIm = st.Values[1];
                        break;
                    case MathPlaneStepAction.Show:
                    case MathPlaneStepAction.Hide:
                        double target = st.Action == MathPlaneStepAction.Show ? 1 : 0;
                        foreach (var n in st.Names)
                        {
                            double from = alpha.TryGetValue(n, out double a0) ? a0 : (byName[n].Visible ? 1 : 0);
                            if (begun) alphaAt[n] = Lerp(from, target, u);
                            alpha[n] = target;
                        }
                        break;
                    case MathPlaneStepAction.Move:
                    {
                        string n = st.Names[0];
                        var from = pos.TryGetValue(n, out var p0) ? p0 : Anchor(byName[n]);
                        var to = (st.Values[0], st.Values[1]);
                        if (begun) posAt[n] = (Lerp(from.X, to.Item1, u), Lerp(from.Y, to.Item2, u));
                        pos[n] = to;
                        break;
                    }
                    case MathPlaneStepAction.Draw:
                    {
                        // Begins strictly after its start, so at s = start the object is as
                        // the earlier members left it (s = 0 stays the authored scene).
                        string n = st.Names[0];
                        reveals = true;
                        if (s > mem.Start) { drawAt[n] = u; alphaAt[n] = 1; }
                        alpha[n] = 1;
                        break;
                    }
                    case MathPlaneStepAction.Orbit:
                        reveals = true;
                        if (begun) orbitAt = Lerp(orbit, st.Values[0], u);
                        orbit = st.Values[0];
                        break;
                    case MathPlaneStepAction.Cobweb:
                        reveals = true;
                        if (begun) cobwebAt = Lerp(cobweb, st.Values[0], u);
                        cobweb = st.Values[0];
                        break;
                    case MathPlaneStepAction.Phase:
                    {
                        reveals = true;
                        double phaseTarget = st.Text == "off" ? 0 : 1;
                        if (begun)
                        {
                            if (st.Text != "off") phaseMode = st.Text == "contours" ? MathPlanePhase.Contours : MathPlanePhase.On;
                            phaseAt = Lerp(phase, phaseTarget, u);
                        }
                        phase = phaseTarget;
                        break;
                    }
                    case MathPlaneStepAction.Caption:
                    {
                        reveals = true;
                        string? text = string.IsNullOrEmpty(st.Text) ? null : st.Text;
                        if (begun)
                        {
                            prevCaptionAt = caption; prevCaptionAlphaAt = caption == null ? 0 : 1 - u;
                            captionAt = text; captionAlphaAt = text == null ? 0 : u;
                        }
                        caption = text;
                        break;
                    }
                }
            }

            var outObjects = new List<MathPlaneObject>(objects.Count);
            bool changed = false;
            foreach (var o in objects)
            {
                var e = o;
                // Unchanged values leave the object as authored (bit-identical at s = 0).
                if (posAt.TryGetValue(o.Name, out var at) && at != Anchor(o)) e = MoveTo(e, at);
                if (alphaAt.TryGetValue(o.Name, out double a))
                {
                    a = Math.Clamp(a, 0, 1);
                    uint baseA = o.Color >> 24;
                    uint na = (uint)Math.Round(baseA * a);
                    var color = (o.Color & 0x00FFFFFFu) | (na << 24);
                    if (na > 0 != o.Visible || color != o.Color) e = e with { Visible = na > 0, Color = color };
                }
                changed |= !ReferenceEquals(e, o);
                outObjects.Add(e);
            }
            var outScene = changed ? scene with { Objects = outObjects } : scene;

            // #1696 — the reveal counts the drawer needs: whole steps to compute, the phase
            // mode while it is visible. Values equal to the authored ones change nothing.
            MathPlaneReveal? reveal = null;
            if (reveals)
            {
                if (orbitAt is double k && k != scene.OrbitSteps)
                    outScene = outScene with { OrbitSteps = (int)Math.Ceiling(Math.Max(0, k)) };
                else orbitAt = null;
                if (cobwebAt is double kc && scene.Cobweb is { } cw && kc != cw.Steps)
                    outScene = outScene with { Cobweb = cw with { Steps = Math.Max(1, (int)Math.Ceiling(Math.Max(0, kc))) } };
                else cobwebAt = null;
                var mode = phaseAt > 0 ? phaseMode : MathPlanePhase.Off;
                if (mode != outScene.Phase) outScene = outScene with { Phase = mode };
                reveal = new MathPlaneReveal(drawAt, orbitAt, cobwebAt, Math.Clamp(phaseAt, 0, 1),
                                             captionAt, captionAlphaAt, prevCaptionAt, prevCaptionAlphaAt);
            }

            var outParams = p;
            if (touchesParams)
            {
                outParams = p.Clone();
                outParams.MathPlaneT = tAt;
                outParams.MathPlaneM11 = mAt[0]; outParams.MathPlaneM12 = mAt[1];
                outParams.MathPlaneM21 = mAt[2]; outParams.MathPlaneM22 = mAt[3];
                outParams.MathPlaneCRe = cReAt; outParams.MathPlaneCIm = cImAt;
            }
            return new MathPlaneScriptState(outScene, outParams, s, duration, reveal);
        }

        /// <summary>The point <c>move</c> drives: a point itself, a vector's tip (tail +
        /// components), a segment's or polyline's first vertex.</summary>
        public static (double X, double Y) Anchor(MathPlaneObject o)
        {
            if (o.Kind == MathPlaneObjectKind.Vector)
            {
                var (tx, ty) = o.Tail;
                return (tx + o.Coords[0], ty + o.Coords[1]);
            }
            return (o.Coords[0], o.Coords[1]);
        }

        /// <summary><paramref name="o"/> moved so its <see cref="Anchor"/> is at <paramref name="at"/>.
        /// A vector keeps its tail; a segment or polyline moves rigidly.</summary>
        public static MathPlaneObject MoveTo(MathPlaneObject o, (double X, double Y) at)
        {
            var c = o.Coords.ToArray();
            if (o.Kind == MathPlaneObjectKind.Vector)
            {
                var (tx, ty) = o.Tail;
                c[0] = at.X - tx; c[1] = at.Y - ty;
            }
            else if (o.Kind == MathPlaneObjectKind.Point)
            {
                c[0] = at.X; c[1] = at.Y;
            }
            else
            {
                double dx = at.X - c[0], dy = at.Y - c[1];
                for (int i = 0; i + 1 < c.Length; i += 2) { c[i] += dx; c[i + 1] += dy; }
            }
            return o with { Coords = c };
        }
    }
}
