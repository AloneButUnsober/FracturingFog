// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Batch/BatchFlagCatalog.cs
// #993 (CB1 of #64) — machine-readable description of every --batch flag.
//
// BatchOptions.TryParse remains the grammar's authority; this catalog is a
// sidecar that describes each flag as DATA (value kind, range, default, the
// modes it takes effect in, and its implies / requires / conflicts relations)
// so the Command Builder panel can generate option rows and grey out
// incompatible choices, and so `--batch --help` is generated rather than
// hand-maintained. Guard tests (BatchFlagCatalogTests) hold the catalog and
// the parser in lock-step: every parser case is catalogued, every catalogued
// flag parses, enforced ranges match the validator, and every declared
// implication is one the parser actually performs.
//
// Adding a flag: const in BatchFlags → parser case → entry here. The guard
// tests fail until all three agree.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FracturingFog.Models;

namespace FracturingFog.Batch
{
    /// <summary>How a flag's value is written on the command line.</summary>
    public enum BatchFlagKind
    {
        /// <summary>Bare flag, no value.</summary>
        Switch,
        Int,
        Double,
        /// <summary>One of <see cref="BatchFlagSpec.Choices"/>.</summary>
        Choice,
        /// <summary>Free text (often a library name — see <see cref="BatchFlagSpec.Source"/>).</summary>
        Text,
        /// <summary>Hex colour, <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
        Color,
        /// <summary>File or folder path.</summary>
        Path,
        /// <summary>Comma-separated doubles, <see cref="BatchFlagSpec.Arity"/> of them.</summary>
        Vector,
    }

    /// <summary>Batch modes a flag takes effect in. <see cref="Remote"/> is the
    /// <c>--remote</c> overlay, which ignores the local render flags.</summary>
    [Flags]
    public enum BatchModes
    {
        None      = 0,
        Image     = 1 << 0,
        Video     = 1 << 1,
        Slideshow = 1 << 2,
        Scene     = 1 << 3,
        Regrade   = 1 << 4,
        Relight   = 1 << 5,
        Remote    = 1 << 6,

        /// <summary>Modes that build <c>FractalParameters</c> from the flags
        /// (fractal params, relief, lights, fog, glass, ...).</summary>
        FractalRender = Image | Video | Slideshow,
        /// <summary>Animated multi-frame output with a previous frame.</summary>
        Sequence      = Video | Slideshow,
        /// <summary>Every mode that encodes a frame sequence.</summary>
        Encoded       = Video | Slideshow | Scene,
        /// <summary>Every local (non-remote) mode.</summary>
        AllLocal      = Image | Video | Slideshow | Scene | Regrade | Relight,
        All           = AllLocal | Remote,
    }

    /// <summary>Logical grouping, in the order the panel and --help present them.</summary>
    public enum BatchFlagGroup
    {
        Mode,
        Source,
        Output,
        Video,
        Slideshow,
        PostFx,
        Exr,
        FractalParams,
        DomainWarp,
        Relief,
        ReliefCamera,
        Froxel,
        Glass,
        Denoise,
        Relight,
        Volumetric,
        Isolate,
        Lights,
        Remote,
        Misc,
    }

    /// <summary>Where a Text / Choice value comes from, so the panel can offer a
    /// picker instead of a free-text box.</summary>
    public enum BatchFlagSource
    {
        None,
        FractalType,
        Region,
        Theme,
        Quality,
        SlideshowConfig,
        Scene,
        RemoteConnection,
        RemotePreset,
        LSystemPreset,
        FlamePreset,
    }

    /// <summary>Description of one batch flag. Relations name other flags by
    /// their canonical spelling.</summary>
    public sealed record BatchFlagSpec
    {
        /// <summary>Canonical spelling (a <see cref="BatchFlags"/> const).</summary>
        public required string Name { get; init; }
        public string[] Aliases { get; init; } = Array.Empty<string>();
        public required BatchFlagKind Kind { get; init; }
        public required BatchFlagGroup Group { get; init; }
        /// <summary>Modes in which the flag has an effect. <see cref="BatchModes.None"/>
        /// = meta flag (e.g. --help), not offered by the panel.</summary>
        public required BatchModes Modes { get; init; }
        public required string Help { get; init; }

        /// <summary>Placeholder shown after the flag in usage text (N, F, NAME, ...).</summary>
        public string? ValueHint { get; init; }
        /// <summary>Display form of the value the parser uses when the flag is absent.</summary>
        public string? Default { get; init; }

        public double? Min { get; init; }
        public double? Max { get; init; }
        /// <summary>The minimum itself is rejected (value must be &gt; Min).</summary>
        public bool MinExclusive { get; init; }
        /// <summary>The parser rejects values outside [Min, Max]. When false the
        /// range is only a UI hint.</summary>
        public bool RangeEnforced { get; init; }
        /// <summary>Mode whose validation enforces the range, when it is not the
        /// flag's first applicable mode (e.g. --motion-blur is checked in scene mode).</summary>
        public BatchMode? RangeMode { get; init; }

        /// <summary>Accepted values for <see cref="BatchFlagKind.Choice"/>.</summary>
        public string[] Choices { get; init; } = Array.Empty<string>();
        /// <summary>Component count for <see cref="BatchFlagKind.Vector"/>.</summary>
        public int Arity { get; init; }
        public BatchFlagSource Source { get; init; }
        /// <summary>A <see cref="BatchFlagKind.Path"/> the batch reads (open
        /// dialog) rather than writes (save dialog).</summary>
        public bool PathIsInput { get; init; }

        /// <summary>Only meaningful for these fractal types (empty = any).</summary>
        public FractalType[] Fractals { get; init; } = Array.Empty<FractalType>();

        /// <summary>Switch flags this flag turns on by itself (the parser sets
        /// them; emitting them too is redundant).</summary>
        public string[] Implies { get; init; } = Array.Empty<string>();
        /// <summary>For a <see cref="BatchFlagKind.Choice"/> flag: switches implied
        /// only when the flag takes a particular value (e.g. a point / spot light
        /// type implies the relief raymarch; directional does not).</summary>
        public IReadOnlyDictionary<string, string[]> ChoiceImplies { get; init; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Flags that must also be present for this one to have an effect.</summary>
        public string[] Requires { get; init; } = Array.Empty<string>();
        /// <summary>Flags that contradict this one or leave it without effect.
        /// Declared on both sides.</summary>
        public string[] ConflictsWith { get; init; } = Array.Empty<string>();

        /// <summary>Supplying this flag switches the batch into this mode.</summary>
        public BatchMode? SelectsMode { get; init; }

        /// <summary>1..3 for a <c>--lightN-*</c> flag, 0 otherwise.</summary>
        public int LightNumber { get; init; }

        public bool TakesValue => Kind != BatchFlagKind.Switch;
    }

    public static class BatchFlagCatalog
    {
        private static readonly Lazy<IReadOnlyList<BatchFlagSpec>> s_all = new(Build);
        private static readonly Lazy<Dictionary<string, BatchFlagSpec>> s_byName = new(() =>
        {
            var d = new Dictionary<string, BatchFlagSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in All)
            {
                d.Add(s.Name, s);
                foreach (var a in s.Aliases) d.Add(a, s);
            }
            return d;
        });

        /// <summary>Every flag, in presentation order.</summary>
        public static IReadOnlyList<BatchFlagSpec> All => s_all.Value;

        /// <summary>Look a flag up by canonical spelling or alias (case-insensitive).</summary>
        public static BatchFlagSpec? Find(string flag)
            => s_byName.Value.TryGetValue(flag, out var s) ? s : null;

        public static IEnumerable<BatchFlagSpec> InGroup(BatchFlagGroup group)
            => All.Where(s => s.Group == group);

        public static BatchModes MaskOf(BatchMode mode) => mode switch
        {
            BatchMode.Image     => BatchModes.Image,
            BatchMode.Video     => BatchModes.Video,
            BatchMode.Slideshow => BatchModes.Slideshow,
            BatchMode.Scene     => BatchModes.Scene,
            BatchMode.Regrade   => BatchModes.Regrade,
            BatchMode.Relight   => BatchModes.Relight,
            _                   => BatchModes.None,
        };

        /// <summary>True when <paramref name="spec"/> has an effect in the given
        /// mode (or under <c>--remote</c>).</summary>
        public static bool AppliesIn(BatchFlagSpec spec, BatchMode mode, bool remote = false)
            => (spec.Modes & (remote ? BatchModes.Remote : MaskOf(mode))) != 0;

        public static string GroupTitle(BatchFlagGroup g) => g switch
        {
            BatchFlagGroup.Mode          => "Mode",
            BatchFlagGroup.Source        => "Fractal + view source (a --region, or all of --x --y --zoom)",
            BatchFlagGroup.Output        => "Output",
            BatchFlagGroup.Video         => "Video (--mode video; also video-type slideshows and --scene)",
            BatchFlagGroup.Slideshow     => "Slideshow + scene encode (--slideshow NAME / --scene NAME)",
            BatchFlagGroup.PostFx        => "Post-FX (parity with the interactive sliders)",
            BatchFlagGroup.Exr           => "OpenEXR",
            BatchFlagGroup.FractalParams => "Fractal-specific parameters",
            BatchFlagGroup.DomainWarp    => "Domain warp (any fractal)",
            BatchFlagGroup.Relief        => "2D relief (heightfield shading; any relief flag implies --relief)",
            BatchFlagGroup.ReliefCamera  => "Relief raymarch camera",
            BatchFlagGroup.Froxel        => "Froxel volumetrics (relief raymarch)",
            BatchFlagGroup.Glass         => "Refractive glass (relief raymarch)",
            BatchFlagGroup.Denoise       => "Guided denoise (relief raymarch)",
            BatchFlagGroup.Relight       => "Relight in post (relief raymarch, or --relight-from)",
            BatchFlagGroup.Volumetric    => "Volumetric lighting (3D fractals + relief raymarch)",
            BatchFlagGroup.Isolate       => "Relief isolate masking",
            BatchFlagGroup.Lights        => "Lights (N = 1, 2 or 3; directional fields do not force relief)",
            BatchFlagGroup.Remote        => "Remote rendering (saved FFClient connection + render preset)",
            BatchFlagGroup.Misc          => "Miscellaneous",
            _                            => g.ToString(),
        };

        // ── Usage text ────────────────────────────────────────────────────────

        private const int UsageColumn = 30;
        private const int UsageWidth  = 100;

        /// <summary>The flag reference printed by <c>--batch --help</c>, generated
        /// from the catalog. Light flags are shown once as <c>--lightN-*</c>.</summary>
        public static string FormatUsage()
        {
            var sb = new StringBuilder();
            foreach (BatchFlagGroup g in Enum.GetValues<BatchFlagGroup>())
            {
                var specs = InGroup(g).Where(s => s.LightNumber <= 1).ToList();
                if (specs.Count == 0) continue;
                sb.Append(GroupTitle(g)).Append(':').AppendLine();
                foreach (var s in specs) AppendUsage(sb, s);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static void AppendUsage(StringBuilder sb, BatchFlagSpec s)
        {
            string Shown(string n) => s.LightNumber > 0 ? n.Replace("--light1-", "--lightN-", StringComparison.Ordinal) : n;
            var head = new StringBuilder("  ").Append(Shown(s.Name));
            if (s.ValueHint != null) head.Append(' ').Append(s.ValueHint);
            foreach (var a in s.Aliases) head.Append(", ").Append(a);
            AppendWrapped(sb, head.ToString(), Wrap(Describe(s, Shown), UsageWidth - UsageColumn));
        }

        /// <summary>The flag's help sentence followed by its choices, range,
        /// default, implications, needs and fractal filter — the text shown in
        /// --help and as the Command panel's tooltip.</summary>
        public static string Describe(BatchFlagSpec s) => Describe(s, n => n);

        private static string Describe(BatchFlagSpec s, Func<string, string> shown)
        {
            var text = new StringBuilder(s.Help.TrimEnd());
            if (s.Kind == BatchFlagKind.Choice && s.Choices.Length > 0)
                text.Append(" One of: ").Append(string.Join("|", s.Choices)).Append('.');
            if (s.Min.HasValue || s.Max.HasValue)
                text.Append(" Range ").Append(RangeText(s)).Append('.');
            if (s.Default != null && !LightDefaultVaries(s))
                text.Append(" Default ").Append(s.Default).Append('.');
            if (s.Implies.Length > 0)
                text.Append(" Implies ").Append(string.Join(", ", s.Implies.Select(shown))).Append('.');
            if (s.Requires.Length > 0)
                text.Append(" Needs ").Append(string.Join(", ", s.Requires.Select(shown))).Append('.');
            if (s.Fractals.Length > 0)
                text.Append(" For --fractal ").Append(string.Join("|", s.Fractals)).Append('.');
            return text.ToString();
        }

        private static void AppendWrapped(StringBuilder sb, string h, List<string> lines)
        {
            if (h.Length >= UsageColumn - 1)
            {
                sb.AppendLine(h);
                foreach (var l in lines) sb.Append(' ', UsageColumn).AppendLine(l);
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                    sb.Append(i == 0 ? h.PadRight(UsageColumn) : new string(' ', UsageColumn)).AppendLine(lines[i]);
            }
        }

        // A --lightN-* default that differs between the three slots (e.g. the key
        // light is on, fill / rim are off) is not shown on the shared usage line.
        private static bool LightDefaultVaries(BatchFlagSpec s)
        {
            if (s.LightNumber == 0) return false;
            string field = s.Name.Substring(s.Name.IndexOf('-', 2) + 1);
            return Enumerable.Range(1, 3)
                .Select(n => Find(BatchFlags.LightFlag(n, field))?.Default)
                .Distinct().Count() > 1;
        }

        private static string RangeText(BatchFlagSpec s)
        {
            string lo = s.Min.HasValue ? s.Min.Value.ToString(CultureInfo.InvariantCulture) : "";
            string hi = s.Max.HasValue ? s.Max.Value.ToString(CultureInfo.InvariantCulture) : "";
            if (s.Min.HasValue && !s.Max.HasValue) return (s.MinExclusive ? "> " : ">= ") + lo;
            if (!s.Min.HasValue) return "<= " + hi;
            return (s.MinExclusive ? "(" + lo + ", " + hi + "]" : lo + ".." + hi);
        }

        private static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // Long '|'-joined choice lists break at the separators.
                foreach (var piece in SplitLong(word, width))
                {
                    if (line.Length > 0 && line.Length + 1 + piece.Length > width)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    if (line.Length > 0 && !line.ToString().EndsWith("|", StringComparison.Ordinal)) line.Append(' ');
                    line.Append(piece);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }

        private static IEnumerable<string> SplitLong(string word, int width)
        {
            if (word.Length <= width || word.IndexOf('|') < 0) { yield return word; yield break; }
            var parts = word.Split('|');
            for (int i = 0; i < parts.Length; i++)
                yield return i < parts.Length - 1 ? parts[i] + "|" : parts[i];
        }

        // ── Catalog ───────────────────────────────────────────────────────────

        private const BatchModes FR = BatchModes.FractalRender;
        private static readonly string[] ReliefOn = { BatchFlags.Relief };
        private static readonly string[] RaymarchOn = { BatchFlags.ReliefRaymarch, BatchFlags.Relief };

        private static BatchFlagSpec Sw(string name, BatchFlagGroup g, BatchModes m, string help)
            => new() { Name = name, Kind = BatchFlagKind.Switch, Group = g, Modes = m, Help = help };

        private static BatchFlagSpec Int(string name, BatchFlagGroup g, BatchModes m, string help,
            double? min = null, double? max = null, bool enforced = false, string? def = null)
            => new() { Name = name, Kind = BatchFlagKind.Int, Group = g, Modes = m, Help = help, ValueHint = "N",
                       Min = min, Max = max, RangeEnforced = enforced, Default = def };

        private static BatchFlagSpec Dbl(string name, BatchFlagGroup g, BatchModes m, string help,
            double? min = null, double? max = null, bool enforced = false, string? def = null)
            => new() { Name = name, Kind = BatchFlagKind.Double, Group = g, Modes = m, Help = help, ValueHint = "F",
                       Min = min, Max = max, RangeEnforced = enforced, Default = def };

        private static BatchFlagSpec Txt(string name, BatchFlagGroup g, BatchModes m, string help,
            BatchFlagSource src = BatchFlagSource.None, string hint = "NAME", string? def = null)
            => new() { Name = name, Kind = BatchFlagKind.Text, Group = g, Modes = m, Help = help, ValueHint = hint,
                       Source = src, Default = def };

        private static BatchFlagSpec Pick(string name, BatchFlagGroup g, BatchModes m, string help,
            string[] choices, string? def = null, string hint = "NAME")
            => new() { Name = name, Kind = BatchFlagKind.Choice, Group = g, Modes = m, Help = help, ValueHint = hint,
                       Choices = choices, Default = def };

        private static BatchFlagSpec Col(string name, BatchFlagGroup g, BatchModes m, string help, string? def = null)
            => new() { Name = name, Kind = BatchFlagKind.Color, Group = g, Modes = m, Help = help, ValueHint = "\"#RRGGBB\"",
                       Default = def };

        private static BatchFlagSpec PathFlag(string name, BatchFlagGroup g, BatchModes m, string help, string hint = "PATH")
            => new() { Name = name, Kind = BatchFlagKind.Path, Group = g, Modes = m, Help = help, ValueHint = hint };

        private static IReadOnlyList<BatchFlagSpec> Build()
        {
            const BatchFlagGroup Mode = BatchFlagGroup.Mode, Source = BatchFlagGroup.Source, Output = BatchFlagGroup.Output,
                Video = BatchFlagGroup.Video, Slideshow = BatchFlagGroup.Slideshow,
                PostFx = BatchFlagGroup.PostFx, Exr = BatchFlagGroup.Exr, Fp = BatchFlagGroup.FractalParams,
                Warp = BatchFlagGroup.DomainWarp, Relief = BatchFlagGroup.Relief, Cam = BatchFlagGroup.ReliefCamera,
                Froxel = BatchFlagGroup.Froxel, Glass = BatchFlagGroup.Glass, Denoise = BatchFlagGroup.Denoise,
                Relight = BatchFlagGroup.Relight, Vol = BatchFlagGroup.Volumetric, Iso = BatchFlagGroup.Isolate,
                Remote = BatchFlagGroup.Remote, Misc = BatchFlagGroup.Misc;
            const BatchModes ImgVid = BatchModes.Image | BatchModes.Video;
            const BatchModes Sized = BatchModes.Image | BatchModes.Encoded;
            string[] modeExclusive = { BatchFlags.Slideshow, BatchFlags.Scene, BatchFlags.RegradeExr, BatchFlags.RelightFrom };
            string[] Others(string self) => modeExclusive.Where(f => f != self).ToArray();

            var list = new List<BatchFlagSpec>
            {
                // ── Mode ──
                Pick(BatchFlags.Mode, Mode, BatchModes.Image | BatchModes.Encoded,
                    "Batch mode. --slideshow, --scene, --regrade-exr and --relight-from select their own mode.",
                    new[] { "image", "video", "slideshow", "scene" }, "image", "MODE") with { Aliases = new[] { "-m" } },
                Txt(BatchFlags.Slideshow, Mode, BatchModes.Slideshow,
                    "Render a saved SlideshowConfig preset (region/theme/timing) to video.", BatchFlagSource.SlideshowConfig)
                    with { SelectsMode = BatchMode.Slideshow, ConflictsWith = Others(BatchFlags.Slideshow) },
                Txt(BatchFlags.Scene, Mode, BatchModes.Scene,
                    "Render a saved scene (scenes.json) offline, with its shot-attached animations.", BatchFlagSource.Scene)
                    with { SelectsMode = BatchMode.Scene, ConflictsWith = Others(BatchFlags.Scene) },
                PathFlag(BatchFlags.RegradeExr, Mode, BatchModes.Regrade,
                    "Regrade a scene-linear OpenEXR without re-rendering: apply --view-transform + --exposure, write --out.",
                    "IN.exr") with { SelectsMode = BatchMode.Regrade, PathIsInput = true, ConflictsWith = Others(BatchFlags.RegradeExr) },
                PathFlag(BatchFlags.RelightFrom, Mode, BatchModes.Relight,
                    "Relight a saved --aov-exr export of a --relief-raymarch render under the --relight-* gains, write --out.",
                    "IN.exr") with { SelectsMode = BatchMode.Relight, PathIsInput = true, ConflictsWith = Others(BatchFlags.RelightFrom) },

                // ── Source ──
                Txt(BatchFlags.Region, Source, ImgVid, "Load a saved built-in or user region by name.", BatchFlagSource.Region)
                    with { Aliases = new[] { "-r" } },
                Pick(BatchFlags.Fractal, Source, ImgVid, "Fractal type.", Enum.GetNames<FractalType>(), "Mandelbrot", "TYPE")
                    with { Aliases = new[] { "-f" }, Source = BatchFlagSource.FractalType },
                Dbl(BatchFlags.X, Source, ImgVid, "Centre real coordinate (with --y and --zoom)."),
                Dbl(BatchFlags.Y, Source, ImgVid, "Centre imaginary coordinate."),
                Dbl(BatchFlags.Zoom, Source, ImgVid, "Zoom.") with { Aliases = new[] { "--z" } },
                Int(BatchFlags.Iter, Source, ImgVid, "Override the iteration count.", min: 1)
                    with { Aliases = new[] { "--i", "--iterations" } },
                Txt(BatchFlags.Theme, Source, ImgVid, "Colour theme name.", BatchFlagSource.Theme, def: BatchDefaults.ThemeName)
                    with { Aliases = new[] { "-t" } },
                Pick(BatchFlags.Quality, Source, ImgVid, "Quality preset.",
                    new[] { "Draft", "Standard", "High", "Ultra", "Extreme" }, BatchDefaults.QualityName)
                    with { Aliases = new[] { "-q" }, Source = BatchFlagSource.Quality },

                // ── Output ──
                PathFlag(BatchFlags.Out, Output, BatchModes.All,
                    "Output file (image, slideshow, scene) or folder (video). Required.") with { Aliases = new[] { "-o" } },
                Txt(BatchFlags.Name, Output, ImgVid, "Base filename (default derived from region/coords).")
                    with { Aliases = new[] { "-n" } },
                Int(BatchFlags.Width, Output, Sized, "Output width in pixels.", min: 16, enforced: true,
                    def: BatchDefaults.Width.ToString(CultureInfo.InvariantCulture)) with { Aliases = new[] { "-w" } },
                Int(BatchFlags.Height, Output, Sized, "Output height in pixels.", min: 16, enforced: true,
                    def: BatchDefaults.Height.ToString(CultureInfo.InvariantCulture)) with { Aliases = new[] { "-h" } },
                Sw(BatchFlags.NoWatermark, Output, FR,
                    "Turn the region/theme + program watermark OFF (it is on by default).")
                    with { Aliases = new[] { "--watermark" } },

                // ── Video ──
                Dbl(BatchFlags.Seconds, Video, BatchModes.Sequence,
                    "Duration in seconds (slideshow: 0..7200, default 60).",
                    0.5, 600, enforced: true, def: "20") with { Aliases = new[] { "--secs" } },
                Int(BatchFlags.Fps, Video, BatchModes.Encoded, "Frames per second.", 1, 240, enforced: true, def: "30"),
                Dbl(BatchFlags.StartZoom, Video, BatchModes.Sequence,
                    "Starting zoom (0.5 = full set; 3D dolly picks its own wide zoom when omitted).", def: "0.5"),
                Sw(BatchFlags.Reverse, Video, BatchModes.Sequence, "Zoom out from the target back to the full view."),
                Pick(BatchFlags.VideoMotion, Video, BatchModes.Sequence,
                    "How the video moves. auto = per family (2D plane zoom, 3D dolly, Logistic/AcidWarp sweep, else kenburns).",
                    new[] { "auto", "zoom", "hold", "kenburns", "sweep" }, "auto", "MODE"),
                Dbl(BatchFlags.Orbit, Video, BatchModes.Sequence, "3D only: sweep the camera azimuth this many degrees.",
                    -3600, 3600, enforced: true, def: "0") with { ValueHint = "DEG" },
                Sw(BatchFlags.NoDrift, Video, BatchModes.Sequence,
                    "Julia/Phoenix/Glynn zooms: keep the constant fixed (default drifts it gently)."),
                Int(BatchFlags.VideoSeed, Video, BatchModes.Sequence, "Seed for Ken-Burns / drift paths.", def: "0"),
                Pick(BatchFlags.Lossless, Video, BatchModes.Video,
                    "Encode preset. none = built-in WMF H.264 MP4; h264 = lossless MP4; ffv1 = lossless MKV; h264hq = CRF 18 MP4 (ffmpeg).",
                    new[] { "none", "h264", "ffv1", "h264hq" }, "none", "TYPE") with { Aliases = new[] { "-l" } },
                Sw(BatchFlags.KeepFrames, Video, BatchModes.Encoded, "Keep the PNG frame folder after encoding.")
                    with { ConflictsWith = new[] { BatchFlags.NoKeepFrames } },
                Sw(BatchFlags.NoKeepFrames, Video, BatchModes.Encoded, "Delete the PNG frame folder after encoding.")
                    with { ConflictsWith = new[] { BatchFlags.KeepFrames } },
                Int(BatchFlags.MotionBlur, Video, BatchModes.Video | BatchModes.Scene,
                    "Accumulation motion-blur sub-frames per output frame (1 = off; cost is N x per frame).",
                    1, 64, enforced: true, def: "1") with { Aliases = new[] { "--subframes" }, RangeMode = BatchMode.Scene },
                Dbl(BatchFlags.Shutter, Video, BatchModes.Video | BatchModes.Scene,
                    "Open-shutter fraction of the frame interval (0.5 ~ 180 degrees).", 0, 1, enforced: true, def: "0.5")
                    with { MinExclusive = true, RangeMode = BatchMode.Scene },

                // ── Slideshow ──
                Pick(BatchFlags.Encode, Slideshow, BatchModes.Slideshow | BatchModes.Scene,
                    "ffmpeg encode preset. h264hq = CRF 18 MP4; h264 = lossless MP4; ffv1 = lossless MKV.",
                    new[] { "h264hq", "h264", "ffv1" }, "h264hq", "TYPE"),
                Sw(BatchFlags.MoreColors, Slideshow, BatchModes.Slideshow,
                    "Colour Focus cadence: 8 themes per region with a shorter dwell (image-type presets).")
                    with { Aliases = new[] { "--more-colours" } },

                // ── Post-FX ──
                Int(BatchFlags.Brightness, PostFx, FR, "Brightness (0 = none).", -100, 100, enforced: true, def: "0"),
                Int(BatchFlags.Contrast, PostFx, FR, "Contrast (0 = none).", -100, 100, enforced: true, def: "0"),
                Int(BatchFlags.Adaptive, PostFx, FR, "Adaptive histogram-equalization strength (escape-time types).",
                    0, 100, enforced: true, def: "0") with { Aliases = new[] { "--histogram-eq" } },
                Int(BatchFlags.InteriorAlpha, PostFx, FR,
                    "Interior (in-set) opacity; below 255 the interior turns translucent over the theme background.",
                    0, 255, enforced: true, def: "255"),
                Pick(BatchFlags.ViewTransform, PostFx, FR | BatchModes.Regrade, "Output view transform / tonemap.",
                    new[] { "none", "reinhard", "aces", "agx", "filmic" }, "none") with { Aliases = new[] { "--tonemap" } },
                Dbl(BatchFlags.Exposure, PostFx, FR | BatchModes.Regrade, "Exposure in stops before the view transform.",
                    -16, 16, enforced: true, def: "0") with { ValueHint = "EV" },

                // ── EXR ──
                Sw(BatchFlags.AovExr, Exr, BatchModes.Image,
                    "Write a multi-layer AOV OpenEXR (beauty, normal, depth, AO, diffuse, specular, shadow, stepcount). Meaningful for 3D / relief raymarch."),
                Sw(BatchFlags.ExrZip, Exr, BatchModes.Image | BatchModes.Regrade | BatchModes.Relight,
                    "ZIP-compress .exr output (smaller, lossless, not byte-stable)."),

                // ── Fractal-specific ──
                Int(BatchFlags.MultibrotExp, Fp, FR, "Multibrot exponent.", def: "3")
                    with { Aliases = new[] { "--multibrot-power" }, Fractals = new[] { FractalType.Multibrot } },
                Dbl(BatchFlags.BulbPower, Fp, FR, "Mandelbulb power.", def: "8")
                    with { Fractals = new[] { FractalType.Mandelbulb } },
                Txt(BatchFlags.LSystemPreset, Fp, FR, "L-System preset name.", BatchFlagSource.LSystemPreset, def: "Hilbert")
                    with { Aliases = new[] { "--lsystem" }, Fractals = new[] { FractalType.LSystem } },
                Int(BatchFlags.LSystemDepth, Fp, FR, "L-System generation depth.", 0, 12, def: "5")
                    with { Fractals = new[] { FractalType.LSystem } },
                Dbl(BatchFlags.PlasmaRoughness, Fp, FR, "Plasma diamond-square roughness (0 smooth, 1 jagged).", 0, 1, def: "0.55")
                    with { Fractals = new[] { FractalType.Plasma } },
                Int(BatchFlags.PlasmaSeed, Fp, FR, "Plasma PRNG seed.", def: "12345")
                    with { Fractals = new[] { FractalType.Plasma } },
                Txt(BatchFlags.FlamePreset, Fp, FR, "Flame preset name.", BatchFlagSource.FlamePreset, def: "Sierpinski Variation")
                    with { Aliases = new[] { "--flame" }, Fractals = new[] { FractalType.Flame } },
                Int(BatchFlags.FlameIter, Fp, FR, "Flame chaos-game sample count.", min: 1, def: "8000000")
                    with { Fractals = new[] { FractalType.Flame } },
                Dbl(BatchFlags.FlameGamma, Fp, FR, "Flame tone-map gamma.", def: "2.2")
                    with { Fractals = new[] { FractalType.Flame } },
                Dbl(BatchFlags.FlameVibrancy, Fp, FR, "Flame highlight saturation.", 0, 1, def: "0.8")
                    with { Fractals = new[] { FractalType.Flame } },
                Int(BatchFlags.AcidPattern, Fp, FR, "Acid Warp static pattern index.",
                    0, FractalParameters.AcidWarpPatternCount - 1, enforced: true, def: "0")
                    with { Fractals = new[] { FractalType.AcidWarp } },
                Dbl(BatchFlags.AcidFrequency, Fp, FR, "Acid Warp pattern frequency.", def: "1")
                    with { Fractals = new[] { FractalType.AcidWarp } },
                Dbl(BatchFlags.AcidWarpStrength, Fp, FR, "Acid Warp spatial warp strength.", def: "0")
                    with { Fractals = new[] { FractalType.AcidWarp } },
                Int(BatchFlags.AcidSeed, Fp, FR, "Acid Warp PRNG seed.", def: "12345")
                    with { Fractals = new[] { FractalType.AcidWarp } },

                // ── Domain warp ──
                Sw(BatchFlags.DomainWarp, Warp, FR, "Enable the domain-warp post-fx distortion."),
                Dbl(BatchFlags.DomainWarpStrength, Warp, FR, "Domain-warp strength.", def: "0")
                    with { Implies = new[] { BatchFlags.DomainWarp } },
                Dbl(BatchFlags.DomainWarpFrequency, Warp, FR, "Domain-warp frequency.", def: "1")
                    with { Implies = new[] { BatchFlags.DomainWarp } },

                // ── Relief ──
                Sw(BatchFlags.Relief, Relief, FR, "Enable the 2D heightfield relief post-pass (emboss by default)."),
                Sw(BatchFlags.ReliefRaymarch, Relief, FR, "Use the oblique raymarch path instead of emboss.")
                    with { Implies = ReliefOn, ConflictsWith = new[] { BatchFlags.ReliefAbsolute } },
                Dbl(BatchFlags.ReliefHeight, Relief, FR, "Height exaggeration.", 0, null, enforced: true, def: "1")
                    with { MinExclusive = true, Implies = ReliefOn },
                Dbl(BatchFlags.ReliefDetailGain, Relief, FR, "Raise filament structure vs the slab (1 = off).", 0, 8, enforced: true, def: "1")
                    with { Implies = ReliefOn },
                Int(BatchFlags.ReliefDetailRadius, Relief, FR, "Feature size in px for --relief-detail-gain (0 = auto).", 0, 256, enforced: true, def: "0")
                    with { Implies = ReliefOn },
                Dbl(BatchFlags.ReliefHeightGamma, Relief, FR, "Top-end height contrast, h^gamma (1 = off).", 0.05, 8, enforced: true, def: "1")
                    with { Implies = ReliefOn },
                Dbl(BatchFlags.ReliefStrength, Relief, FR, "Blend of relief vs flat colour.", 0, 1, enforced: true, def: "1")
                    with { Implies = ReliefOn },
                Dbl(BatchFlags.ReliefLightAzimuth, Relief, FR, "Light azimuth in degrees.", 0, 360, enforced: true, def: "135")
                    with { Implies = ReliefOn },
                Dbl(BatchFlags.ReliefLightElevation, Relief, FR, "Light elevation in degrees.", -90, 90, enforced: true, def: "30")
                    with { Implies = ReliefOn },
                Dbl(BatchFlags.ReliefShadow, Relief, FR, "Shadow strength.", 0, 1, enforced: true, def: "0.6")
                    with { Implies = ReliefOn },
                Sw(BatchFlags.ReliefAbsolute, Relief, FR, "Emboss absolute-height mode (emboss path only).")
                    with { Implies = ReliefOn, ConflictsWith = new[] { BatchFlags.ReliefRaymarch } },

                // ── Relief raymarch camera ──
                Dbl(BatchFlags.ReliefCameraAzimuth, Cam, FR, "Camera azimuth in degrees.", 0, 360, enforced: true, def: "0")
                    with { Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch } },
                Dbl(BatchFlags.ReliefCameraElevation, Cam, FR, "Camera elevation in degrees.", -90, 90, enforced: true, def: "45")
                    with { Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch } },
                Dbl(BatchFlags.ReliefCameraFov, Cam, FR, "Camera field of view in degrees.", 1, 179, enforced: true, def: "50")
                    with { Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch }, ConflictsWith = new[] { BatchFlags.ReliefCameraOrtho } },
                Dbl(BatchFlags.ReliefCameraZoom, Cam, FR, "Camera zoom.", 0, null, enforced: true, def: "1")
                    with { MinExclusive = true, Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch } },
                Sw(BatchFlags.ReliefCameraOrtho, Cam, FR, "Orthographic camera (vs perspective).")
                    with { Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch },
                           ConflictsWith = new[] { BatchFlags.ReliefCameraFov, BatchFlags.DofAperture, BatchFlags.DofFocus } },
                Dbl(BatchFlags.ReliefFarDetail, Cam, FR, "Distant-filament detail; lower = more (slower). 1 = off.", 0.15, 1, enforced: true, def: "1")
                    with { Implies = ReliefOn, Requires = new[] { BatchFlags.ReliefRaymarch } },
                Dbl(BatchFlags.CameraExposure, Cam, FR, "In-camera exposure in stops (separate from --exposure).", -16, 16, enforced: true, def: "0")
                    with { ValueHint = "EV", Implies = RaymarchOn },
                Dbl(BatchFlags.DofAperture, Cam, FR, "Depth-of-field lens radius (0 = pinhole; perspective camera only).", 0, 1, enforced: true, def: "0")
                    with { Implies = RaymarchOn, ConflictsWith = new[] { BatchFlags.ReliefCameraOrtho } },
                Dbl(BatchFlags.DofFocus, Cam, FR, "Depth-of-field focus distance in world units (0 = auto-focus the centre).", 0, null, enforced: true, def: "0")
                    with { Implies = RaymarchOn, Requires = new[] { BatchFlags.DofAperture }, ConflictsWith = new[] { BatchFlags.ReliefCameraOrtho } },
                Dbl(BatchFlags.ReliefMotionBlur, Cam, BatchModes.Sequence,
                    "Per-pixel vector motion blur strength from the motion AOV (0 = off).", 0, 4, enforced: true, def: "0")
                    with { Implies = RaymarchOn },
                Int(BatchFlags.ReliefMotionBlurSamples, Cam, BatchModes.Sequence,
                    "Vector motion-blur taps (turns the blur on at strength 1 if unset).", 2, 64, enforced: true, def: "8")
                    with { Implies = RaymarchOn },

                // ── Froxel ──
                Sw(BatchFlags.ReliefFroxel, Froxel, FR, "Froxel (frustum-voxel) volumetrics for the fog.")
                    with { Implies = RaymarchOn },
                Pick(BatchFlags.ReliefFroxelQuality, Froxel, FR, "Froxel resolution.",
                    Enum.GetNames<FroxelQuality>(), nameof(FroxelQuality.Balanced), "Q")
                    with { Implies = new[] { BatchFlags.ReliefFroxel, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Sw(BatchFlags.ReliefFroxelTemporal, Froxel, BatchModes.Sequence, "Temporal reprojection: stable animated fog across frames.")
                    with { Implies = new[] { BatchFlags.ReliefFroxel, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Dbl(BatchFlags.ReliefFroxelFeedback, Froxel, BatchModes.Sequence, "Temporal blend weight.", 0, 0.99, enforced: true, def: "0.9")
                    with { Implies = new[] { BatchFlags.ReliefFroxelTemporal, BatchFlags.ReliefFroxel, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Sw(BatchFlags.ReliefFroxelReproject, Froxel, BatchModes.Sequence, "Sub-cell reprojection: keep animated fog anchored under camera motion.")
                    with { Implies = new[] { BatchFlags.ReliefFroxelTemporal, BatchFlags.ReliefFroxel, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },

                // ── Glass ──
                Sw(BatchFlags.Glass, Glass, FR, "Refractive glass at a sensible default (transmission 0.9 unless --transmission is given).")
                    with { Implies = RaymarchOn },
                Dbl(BatchFlags.Transmission, Glass, FR, "Transmission (0 = opaque).", 0, 1, enforced: true, def: "0")
                    with { Implies = new[] { BatchFlags.Glass, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Dbl(BatchFlags.Ior, Glass, FR, "Index of refraction.", 1, 3, enforced: true, def: "1.5")
                    with { Implies = RaymarchOn },
                Dbl(BatchFlags.AbsorptionDist, Glass, FR, "Beer-Lambert reference distance for the tint.", 0, null, enforced: true, def: "1")
                    with { MinExclusive = true, Implies = RaymarchOn },
                Col(BatchFlags.AbsorptionColor, Glass, FR, "Glass tint.", "#FFFFFF") with { Implies = RaymarchOn },
                Sw(BatchFlags.GlassInternalMarch, Glass, FR, "Full two-surface march (real thickness + exit refraction). Turns glass on.")
                    with { Implies = new[] { BatchFlags.Glass, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Int(BatchFlags.GlassInternalBounces, Glass, FR, "Internal-reflection bounce budget.", 1, 6, enforced: true, def: "1")
                    with { Implies = new[] { BatchFlags.GlassInternalMarch, BatchFlags.Glass, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },

                // ── Denoise ──
                Int(BatchFlags.Denoise, Denoise, FR, "Guided A-Trous denoise passes (0 = off).", 0, 8, enforced: true, def: "0")
                    with { Implies = RaymarchOn },
                Dbl(BatchFlags.DenoiseColorSigma, Denoise, FR, "Colour edge-stopping sigma.", 0, null, enforced: true, def: "0.1")
                    with { MinExclusive = true, Implies = RaymarchOn, Requires = new[] { BatchFlags.Denoise } },
                Dbl(BatchFlags.DenoiseNormalSigma, Denoise, FR, "Normal edge-stopping sigma.", 0, null, enforced: true, def: "0.3")
                    with { MinExclusive = true, Implies = RaymarchOn, Requires = new[] { BatchFlags.Denoise } },
                Dbl(BatchFlags.DenoiseDepthSigma, Denoise, FR, "Depth edge-stopping sigma.", 0, null, enforced: true, def: "0.2")
                    with { MinExclusive = true, Implies = RaymarchOn, Requires = new[] { BatchFlags.Denoise } },
                Sw(BatchFlags.DenoiseAdaptiveSs, Denoise, FR, "Drop anti-alias supersampling while denoise is on.")
                    with { Implies = RaymarchOn, Requires = new[] { BatchFlags.Denoise } },

                // ── Relight ──
                Sw(BatchFlags.Relight, Relight, FR, "Relight in post from the captured lighting passes.")
                    with { Implies = RaymarchOn },
                Dbl(BatchFlags.RelightDiffuse, Relight, FR | BatchModes.Relight, "Diffuse gain.", 0, 8, enforced: true, def: "1")
                    with { Implies = new[] { BatchFlags.Relight, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Dbl(BatchFlags.RelightSpecular, Relight, FR | BatchModes.Relight, "Specular gain.", 0, 8, enforced: true, def: "1")
                    with { Implies = new[] { BatchFlags.Relight, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Dbl(BatchFlags.RelightAo, Relight, FR | BatchModes.Relight, "AO strength.", 0, 4, enforced: true, def: "1")
                    with { Implies = new[] { BatchFlags.Relight, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },
                Dbl(BatchFlags.RelightAmbient, Relight, FR | BatchModes.Relight, "Flat ambient term.", 0, 4, enforced: true, def: "0")
                    with { Implies = new[] { BatchFlags.Relight, BatchFlags.ReliefRaymarch, BatchFlags.Relief } },

                // ── Volumetric ──
                Dbl(BatchFlags.FogDensity, Vol, FR, "Beer-Lambert fog density (0 = off).", 0, 10, enforced: true, def: "0"),
                Dbl(BatchFlags.FogHeightFalloff, Vol, FR, "Ground-hugging falloff (0 = uniform fog).", 0, 10, enforced: true, def: "0"),
                Int(BatchFlags.VolumeSteps, Vol, FR, "In-scatter step count (0 = exponential fog only; 16-48 typical).", 0, 256, enforced: true, def: "0"),
                Dbl(BatchFlags.VolumeAnisotropy, Vol, FR, "Henyey-Greenstein phase g (0 isotropic; > 0 forward god-rays).", -1, 1, enforced: true, def: "0"),
                Col(BatchFlags.FogColor, Vol, FR, "Fog medium tint.", "#FFFFFF"),
                Dbl(BatchFlags.VolumePaletteStrength, Vol, FR, "Cross-fade the fog toward the 3D theme gradient.", 0, 1, enforced: true, def: "0"),
                Int(BatchFlags.FogLightMask, Vol, FR, "Which lights colour the fog (bit n = light n+1). Surfaces stay lit either way.",
                    0, 7, enforced: true, def: "7"),

                // ── Isolate ──
                Sw(BatchFlags.ReliefIsolate, Iso, FR, "Isolate high-relief features (drop flat / low-detail areas).")
                    with { Implies = ReliefOn },
                Sw(BatchFlags.ReliefIsolateNoDetail, Iso, FR, "Turn OFF the default detail-based isolation.")
                    with { Implies = new[] { BatchFlags.ReliefIsolate, BatchFlags.Relief } },
                Dbl(BatchFlags.ReliefIsolateThreshold, Iso, FR, "Detail threshold.", 0, 1, enforced: true, def: "0.6")
                    with { Implies = new[] { BatchFlags.ReliefIsolate, BatchFlags.Relief } },
                Sw(BatchFlags.ReliefIsolateByColor, Iso, FR, "Also isolate by dropping the listed colours.")
                    with { Implies = new[] { BatchFlags.ReliefIsolate, BatchFlags.Relief } },
                Txt(BatchFlags.ReliefIsolateColors, Iso, FR, "Colours to drop, comma-separated hex.", hint: "CSV")
                    with { Implies = new[] { BatchFlags.ReliefIsolateByColor, BatchFlags.ReliefIsolate, BatchFlags.Relief } },
                Dbl(BatchFlags.ReliefIsolateTolerance, Iso, FR, "Colour match tolerance.", 0, 1, enforced: true, def: "0.12")
                    with { Implies = new[] { BatchFlags.ReliefIsolate, BatchFlags.Relief } },

                // ── Remote ──
                Sw(BatchFlags.Remote, Remote, BatchModes.Remote,
                    "Route this batch through a remote FracturingFog server. Image vs video comes from the preset, not --mode.")
                    with { Requires = new[] { BatchFlags.Connection, BatchFlags.Render } },
                Txt(BatchFlags.Connection, Remote, BatchModes.Remote, "Saved client-connection name.", BatchFlagSource.RemoteConnection)
                    with { Requires = new[] { BatchFlags.Remote } },
                Txt(BatchFlags.Render, Remote, BatchModes.Remote, "Saved render-preset name.", BatchFlagSource.RemotePreset)
                    with { Requires = new[] { BatchFlags.Remote } },

                // ── Misc ──
                Sw(BatchFlags.Verbose, Misc, BatchModes.All, "Print extra diagnostics.") with { Aliases = new[] { "-v" } },
                Sw(BatchFlags.Help, Misc, BatchModes.None, "Print this reference.") with { Aliases = new[] { "-?" } },
            };

            for (int n = 1; n <= 3; n++) list.AddRange(LightSpecs(n));
            // Keep presentation order grouped (lights were appended last).
            return list.OrderBy(s => (int)s.Group).ThenBy(s => list.IndexOf(s)).ToList();
        }

        private static IEnumerable<BatchFlagSpec> LightSpecs(int n)
        {
            const BatchFlagGroup L = BatchFlagGroup.Lights;
            string F(string field) => BatchFlags.LightFlag(n, field);
            yield return Pick(F(BatchFlags.LightFieldType), L, FR,
                "Light type. point/spot add distance falloff and imply --relief-raymarch.",
                new[] { "directional", "point", "spot" }, "directional", "T")
                with
                {
                    LightNumber = n,
                    ChoiceImplies = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["point"] = RaymarchOn,
                        ["spot"]  = RaymarchOn,
                    },
                };
            yield return Dbl(F(BatchFlags.LightFieldIntensity), L, FR, "Brightness (0 = off; light 1 defaults to 1, lights 2-3 to 0).", 0, 4, enforced: true,
                def: n == 1 ? "1" : "0") with { LightNumber = n };
            yield return new BatchFlagSpec
            {
                Name = F(BatchFlags.LightFieldDir), Kind = BatchFlagKind.Vector, Arity = 2, Group = L, Modes = FR, LightNumber = n,
                ValueHint = "\"theta,phi\"", Help = "Aim in radians (directional aim / spot cone axis).",
            };
            yield return new BatchFlagSpec
            {
                Name = F(BatchFlags.LightFieldPos), Kind = BatchFlagKind.Vector, Arity = 3, Group = L, Modes = FR, LightNumber = n,
                ValueHint = "\"x,y,z\"", Help = "World position (point / spot).", Implies = RaymarchOn,
            };
            yield return Dbl(F(BatchFlags.LightFieldRange), L, FR, "Soft cutoff distance (0 = pure inverse-square).",
                0, 100, enforced: true, def: "0") with { LightNumber = n, Implies = RaymarchOn };
            yield return new BatchFlagSpec
            {
                Name = F(BatchFlags.LightFieldCone), Kind = BatchFlagKind.Vector, Arity = 2, Group = L, Modes = FR, LightNumber = n,
                ValueHint = "\"inner,outer\"", Help = "Spot cone half-angles in degrees, each within the range.",
                Min = 0, Max = 90, Implies = RaymarchOn,
            };
            yield return Col(F(BatchFlags.LightFieldColor), L, FR, "Light colour.") with { LightNumber = n };
            yield return Dbl(F(BatchFlags.LightFieldArea), L, FR, "Area light: emitter angular radius in degrees (0 = punctual, larger = softer shadows).",
                0, 90, enforced: true, def: "0") with { LightNumber = n };
        }
    }
}
