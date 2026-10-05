// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Cli/CommandComposer.cs
// #994 (CB2 of #64) — the Command panel's model: a set of chosen --batch flags
// plus a mode, turned into argument tokens and validated by the real parser.
//
// Pure (no UI): the panel binds rows to it, and the flag metadata (which mode a
// flag applies in, what it implies / needs / conflicts with) comes from
// BatchFlagCatalog, so the panel never hand-codes compatibility rules.
// Selections that do not apply to the current mode are kept ("parked") but not
// emitted, so flipping modes back and forth loses nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Models;

namespace FracturingFog.Cli
{
    /// <summary>How a flag relates to the current selections.</summary>
    public sealed record CommandFlagState(
        bool Applicable,
        IReadOnlyList<string> ImpliedBy,
        IReadOnlyList<string> BlockedBy,
        IReadOnlyList<string> Missing)
    {
        public bool IsImplied => ImpliedBy.Count > 0;
        public bool IsBlocked => BlockedBy.Count > 0;
    }

    public sealed class CommandComposer
    {
        /// <summary>Default --out value until the user picks a real path.</summary>
        public const string OutputPlaceholder = "<OUTPUT.png>";

        // Flags the composer emits itself from Mode / Remote rather than as rows.
        private static readonly HashSet<string> s_modeFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            BatchFlags.Mode, BatchFlags.Slideshow, BatchFlags.Scene,
            BatchFlags.RegradeExr, BatchFlags.RelightFrom, BatchFlags.Remote, BatchFlags.Help,
        };

        // Groups that describe WHAT is rendered (as opposed to how the job runs).
        // "Seed from live view" replaces exactly these.
        private static readonly HashSet<BatchFlagGroup> s_lookGroups = new()
        {
            BatchFlagGroup.Source, BatchFlagGroup.PostFx, BatchFlagGroup.FractalParams,
            BatchFlagGroup.DomainWarp, BatchFlagGroup.Relief, BatchFlagGroup.ReliefCamera,
            BatchFlagGroup.Froxel, BatchFlagGroup.Glass, BatchFlagGroup.Denoise,
            BatchFlagGroup.Relight, BatchFlagGroup.Volumetric, BatchFlagGroup.Isolate,
            BatchFlagGroup.Lights, BatchFlagGroup.Stereo,
        };

        /// <summary>True when <paramref name="g"/> describes what is rendered, so
        /// "Seed from live view" replaces it (a group left out here is silently
        /// dropped from the seed — #1012 missed Stereo; a guard test now checks
        /// every group is classified).</summary>
        public static bool IsLookGroup(BatchFlagGroup g) => s_lookGroups.Contains(g);

        private readonly Dictionary<string, string?> _sel = new(StringComparer.OrdinalIgnoreCase);
        private BatchMode _mode = BatchMode.Image;
        private bool _remote;
        private int _suspend;
        private bool _pending;

        public CommandComposer()
        {
            _sel[BatchFlags.Out] = OutputPlaceholder;
        }

        /// <summary>Raised after any change (coalesced inside <see cref="Batch"/>).</summary>
        public event Action? Changed;

        public BatchMode Mode
        {
            get => _mode;
            set { if (_mode != value) { _mode = value; Raise(); } }
        }

        /// <summary>Route through a saved remote connection (<c>--remote</c>).</summary>
        public bool Remote
        {
            get => _remote;
            set { if (_remote != value) { _remote = value; Raise(); } }
        }

        /// <summary>True for flags the composer emits from <see cref="Mode"/> /
        /// <see cref="Remote"/> (not shown as ordinary rows).</summary>
        public static bool IsModeFlag(string flag) => s_modeFlags.Contains(flag);

        // ── Selections ───────────────────────────────────────────────────────

        public bool IsSelected(string flag) => _sel.ContainsKey(Canonical(flag));

        public string? ValueOf(string flag) => _sel.TryGetValue(Canonical(flag), out var v) ? v : null;

        /// <summary>Select a flag (a switch ignores <paramref name="value"/>).</summary>
        public void Set(string flag, string? value = null)
        {
            var spec = Spec(flag);
            string? v = spec.TakesValue ? (value ?? "") : null;
            if (_sel.TryGetValue(spec.Name, out var old) && old == v) return;
            _sel[spec.Name] = v;
            Raise();
        }

        public void Clear(string flag)
        {
            if (_sel.Remove(Canonical(flag))) Raise();
        }

        /// <summary>Drop every selection and return to an image render with a
        /// placeholder output.</summary>
        public void Reset()
        {
            Batch(() =>
            {
                _sel.Clear();
                _sel[BatchFlags.Out] = OutputPlaceholder;
                Mode = BatchMode.Image;
                Remote = false;
                Raise();
            });
        }

        /// <summary>Apply several edits and raise <see cref="Changed"/> once.</summary>
        public void Batch(Action edits)
        {
            _suspend++;
            try { edits(); }
            finally
            {
                if (--_suspend == 0 && _pending) { _pending = false; Changed?.Invoke(); }
            }
        }

        /// <summary>Replace the "look" (fractal, view, colour, fx, lights) with the
        /// flags in <paramref name="args"/> — typically the live view's command —
        /// keeping the job settings (mode, output, sequence options). Width /
        /// height are taken from <paramref name="args"/> only when not already set,
        /// and <c>--out</c> is never overwritten.</summary>
        public void SeedLook(IReadOnlyList<string> args)
        {
            var parsed = Tokenize(args);
            Batch(() =>
            {
                foreach (var name in _sel.Keys.ToList())
                    if (s_lookGroups.Contains(Spec(name).Group)) _sel.Remove(name);
                foreach (var (spec, value) in parsed)
                {
                    if (s_lookGroups.Contains(spec.Group)) Put(spec, value);
                    else if ((spec.Name == BatchFlags.Width || spec.Name == BatchFlags.Height) && !_sel.ContainsKey(spec.Name))
                        _sel[spec.Name] = value;
                }
                Raise();
            });
        }

        /// <summary>Load a complete argument list (everything after <c>--batch</c>),
        /// replacing all selections and deriving the mode.</summary>
        public void Load(IReadOnlyList<string> args)
        {
            var parsed = Tokenize(args);
            Batch(() =>
            {
                _sel.Clear();
                var mode = BatchMode.Image;
                bool remote = false;
                foreach (var (spec, value) in parsed)
                {
                    if (spec.Name == BatchFlags.Remote) { remote = true; continue; }
                    if (spec.Name == BatchFlags.Mode && value != null
                        && Enum.TryParse<BatchMode>(value, ignoreCase: true, out var m)) { mode = m; continue; }
                    if (spec.SelectsMode is BatchMode sm) mode = sm;
                    Put(spec, value);
                }
                if (!_sel.ContainsKey(BatchFlags.Out)) _sel[BatchFlags.Out] = OutputPlaceholder;
                Mode = mode;
                Remote = remote;
                Raise();
            });
        }

        /// <summary>Store a parsed value; a repeatable flag (--param) accumulates
        /// one value per line.</summary>
        private void Put(BatchFlagSpec spec, string? value)
        {
            if (spec.Repeatable && _sel.TryGetValue(spec.Name, out var prev) && !string.IsNullOrEmpty(prev))
                _sel[spec.Name] = prev + "\n" + value;
            else
                _sel[spec.Name] = value;
        }

        /// <summary>The individual values of a flag: one per non-blank line for a
        /// repeatable flag, else the single value.</summary>
        public static IEnumerable<string> ValuesOf(BatchFlagSpec spec, string? stored)
        {
            if (!spec.Repeatable) { yield return stored ?? ""; yield break; }
            foreach (var line in (stored ?? "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) yield return line.Trim();
        }

        private static List<(BatchFlagSpec spec, string? value)> Tokenize(IReadOnlyList<string> args)
        {
            var list = new List<(BatchFlagSpec, string?)>();
            for (int i = 0; i < args.Count; i++)
            {
                var spec = BatchFlagCatalog.Find(args[i])
                    ?? throw new ArgumentException($"Unknown batch flag '{args[i]}'.", nameof(args));
                string? value = null;
                if (spec.TakesValue)
                {
                    if (i + 1 >= args.Count) throw new ArgumentException($"{args[i]} is missing its value.", nameof(args));
                    value = args[++i];
                }
                list.Add((spec, value));
            }
            return list;
        }

        // ── State ────────────────────────────────────────────────────────────

        /// <summary>The fractal the command renders, or null when it comes from
        /// a region (unknown here).</summary>
        public FractalType? Fractal
        {
            get
            {
                if (ValueOf(BatchFlags.Fractal) is string f && FractalTypeNames.TryParse(f, true, out var ft)) return ft;
                return IsSelected(BatchFlags.Region) ? null : FractalType.Mandelbrot;
            }
        }

        /// <summary>True when <paramref name="spec"/> takes effect in the current
        /// mode and for the current fractal.</summary>
        public bool AppliesNow(BatchFlagSpec spec)
        {
            if (!BatchFlagCatalog.AppliesIn(spec, _mode, _remote)) return false;
            return spec.Fractals.Length == 0 || Fractal is not FractalType ft || spec.Fractals.Contains(ft);
        }

        /// <summary>Selected flags that are emitted (apply now), in catalog order.</summary>
        public IEnumerable<BatchFlagSpec> Emitted
            => BatchFlagCatalog.All.Where(s => _sel.ContainsKey(s.Name) && !IsModeFlag(s.Name) && AppliesNow(s));

        /// <summary>Selected flags kept but not emitted because they do not apply
        /// to the current mode / fractal.</summary>
        public IEnumerable<BatchFlagSpec> Parked
            => BatchFlagCatalog.All.Where(s => _sel.ContainsKey(s.Name) && !IsModeFlag(s.Name) && !AppliesNow(s));

        private IEnumerable<string> ImpliedBy(BatchFlagSpec s)
        {
            var direct = s.Implies.AsEnumerable();
            if (ValueOf(s.Name) is string v && s.ChoiceImplies.TryGetValue(v, out var byValue)) direct = direct.Concat(byValue);
            var seen = new HashSet<string>();
            var stack = new Stack<string>(direct);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (!seen.Add(t)) continue;
                foreach (var u in Spec(t).Implies) stack.Push(u);
            }
            return seen;
        }

        /// <summary>Every flag in effect: the emitted selections plus everything
        /// they imply.</summary>
        public HashSet<string> Effective()
        {
            var e = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Emitted) { e.Add(s.Name); e.UnionWith(ImpliedBy(s)); }
            if (_remote) e.Add(BatchFlags.Remote);
            return e;
        }

        public CommandFlagState StateOf(BatchFlagSpec spec)
        {
            var emitted = Emitted.ToList();
            var effective = Effective();
            var impliedBy = emitted.Where(s => s.Name != spec.Name && ImpliedBy(s).Contains(spec.Name))
                                   .Select(s => s.Name).ToList();
            var blockedBy = spec.ConflictsWith.Where(effective.Contains).ToList();
            var missing = new List<string>();
            if (_sel.ContainsKey(spec.Name))
            {
                missing.AddRange(spec.Requires.Where(r => !effective.Contains(r)));
                if (spec.RequiresChoice is { } rc)
                {
                    // An absent choice flag counts as its default (a light is
                    // directional unless --lightN-type says otherwise).
                    string? current = IsSelected(rc.Flag) ? ValueOf(rc.Flag) : Spec(rc.Flag).Default;
                    if (!rc.Values.Contains(current ?? "", StringComparer.OrdinalIgnoreCase))
                        missing.Add(rc.Flag + " " + string.Join("|", rc.Values));
                }
            }
            return new CommandFlagState(AppliesNow(spec), impliedBy, blockedBy, missing);
        }

        // ── Output ───────────────────────────────────────────────────────────

        /// <summary>The argument tokens after <c>--batch</c>.</summary>
        public IReadOnlyList<string> Args()
        {
            var args = new List<string>();
            if (_remote) args.Add(BatchFlags.Remote);
            switch (_mode)
            {
                case BatchMode.Video:
                    args.Add(BatchFlags.Mode); args.Add("video"); break;
                case BatchMode.Slideshow:
                    // No name = the active slideshow preset.
                    if (string.IsNullOrWhiteSpace(ValueOf(BatchFlags.Slideshow))) { args.Add(BatchFlags.Mode); args.Add("slideshow"); }
                    else { args.Add(BatchFlags.Slideshow); args.Add(ValueOf(BatchFlags.Slideshow)!); }
                    break;
                case BatchMode.Scene:
                    args.Add(BatchFlags.Scene); args.Add(ValueOf(BatchFlags.Scene) ?? ""); break;
                case BatchMode.Regrade:
                    args.Add(BatchFlags.RegradeExr); args.Add(ValueOf(BatchFlags.RegradeExr) ?? ""); break;
                case BatchMode.Relight:
                    args.Add(BatchFlags.RelightFrom); args.Add(ValueOf(BatchFlags.RelightFrom) ?? ""); break;
            }
            foreach (var s in Emitted)
            {
                if (!s.TakesValue) { args.Add(s.Name); continue; }
                foreach (var v in ValuesOf(s, _sel[s.Name])) { args.Add(s.Name); args.Add(v); }
            }
            return args;
        }

        /// <summary>The copy/paste command line.</summary>
        public string Command(string executableName = "FracturingFog")
            => BatchCommandBuilder.Join(executableName, Args());

        /// <summary>Run the arguments through the real parser. Null when valid,
        /// otherwise the parser's error message.</summary>
        public string? Validate() => Validate(out _);

        public string? Validate(out BatchOptions? options)
        {
            bool ok = BatchOptions.TryParse(Args().ToArray(), 0, out var o, out var error);
            options = ok ? o : null;
            return ok ? null : error;
        }

        /// <summary>True while --out is still the placeholder.</summary>
        public bool HasPlaceholderOutput
            => string.IsNullOrWhiteSpace(ValueOf(BatchFlags.Out)) || ValueOf(BatchFlags.Out) == OutputPlaceholder;

        // ── Helpers ──────────────────────────────────────────────────────────

        private static BatchFlagSpec Spec(string flag)
            => BatchFlagCatalog.Find(flag) ?? throw new ArgumentException($"Unknown batch flag '{flag}'.", nameof(flag));

        private static string Canonical(string flag) => BatchFlagCatalog.Find(flag)?.Name ?? flag;

        private void Raise()
        {
            if (_suspend > 0) { _pending = true; return; }
            Changed?.Invoke();
        }
    }
}
