// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.Controls
{
    /// <summary>
    /// #1363 — token colours for <see cref="DslCodeEditor"/>'s code view. Tuned
    /// for the #1C1C1C editor background and safe for red/green colour
    /// blindness: hues are purple / blue / orange / tan / grey (no red-vs-green
    /// pairs), and yellow #FFCC00 is reserved for errors, which are also
    /// underlined so they never rely on hue alone.
    /// </summary>
    public static class DslPalette
    {
        public static readonly IBrush Keyword = Brush("#C792EA");
        public static readonly IBrush Type = Brush("#C792EA");
        public static readonly IBrush Function = Brush("#82AAFF");
        public static readonly IBrush Variable = Brush("#F78C6C");
        public static readonly IBrush Member = Brush("#F78C6C");
        public static readonly IBrush Number = Brush("#D8C59A");
        public static readonly IBrush Operator = Brush("#B0B0B0");
        public static readonly IBrush Bracket = Brush("#D4D4D4");
        public static readonly IBrush Comment = Brush("#7F7F7F");
        public static readonly IBrush Error = Brush("#FFCC00");

        /// <summary>#1364 — bracket colours by nesting depth (cycled). Distinct
        /// from each other by hue AND lightness, none red-vs-green, none the
        /// error yellow; unmatched brackets use <see cref="Error"/> instead.</summary>
        public static readonly IBrush[] BracketDepth =
        {
            Brush("#E8E8E8"), Brush("#DA70D6"), Brush("#4FB0FF"), Brush("#F0A050"),
        };

        /// <summary>#1364 — box around the bracket pair at the caret.</summary>
        public static readonly IBrush PairFill = Brush("#3A4A5A");
        public static readonly IPen PairPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8FA8C0")), 1);

        /// <summary>Brush for a token kind, or null to keep the editor foreground
        /// (plain identifiers).</summary>
        public static IBrush? For(DslTokenKind kind) => kind switch
        {
            DslTokenKind.Keyword => Keyword,
            DslTokenKind.Type => Type,
            DslTokenKind.Function => Function,
            DslTokenKind.Variable => Variable,
            DslTokenKind.Member => Member,
            DslTokenKind.Number => Number,
            DslTokenKind.Operator => Operator,
            DslTokenKind.Bracket => Bracket,
            DslTokenKind.Comment => Comment,
            DslTokenKind.Error => Error,
            _ => null,
        };

        private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    }

    /// <summary>
    /// #1363 — colours a <see cref="TextDocument"/> from <see cref="DslTokenizer"/>.
    /// Block comments span lines, so the whole document is tokenized once per
    /// document version (equations are small) and each line paints the tokens
    /// that overlap it. #1364 — brackets are coloured by depth, unmatched ones
    /// take the error style, and the same analysis feeds the caret pair box
    /// (<see cref="DslBracketPairRenderer"/>) and jump-to-match.
    /// </summary>
    public sealed class DslHighlighter : DocumentColorizingTransformer
    {
        private DslDialect _dialect;
        private List<DslToken> _tokens = new();
        private DslBracketMap _brackets = DslBrackets.Match(Array.Empty<DslToken>(), string.Empty);
        private ITextSourceVersion? _version;

        public DslDialect Dialect
        {
            get => _dialect;
            set
            {
                if (_dialect == value) return;
                _dialect = value;
                _version = null; // re-tokenize on next paint
            }
        }

        /// <summary>Tokens and bracket map for <paramref name="doc"/>'s current
        /// version (re-analysed only when the document changed).</summary>
        public (IReadOnlyList<DslToken> Tokens, DslBracketMap Brackets) Analyze(TextDocument doc)
        {
            var version = doc.Version;
            if (_version == null || version == null || !_version.BelongsToSameDocumentAs(version)
                || _version.CompareAge(version) != 0)
            {
                string text = doc.Text;
                _tokens = DslTokenizer.Tokenize(text, _dialect);
                _brackets = DslBrackets.Match(_tokens, text);
                _version = version;
            }
            return (_tokens, _brackets);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            Analyze(CurrentContext.Document);

            int lineStart = line.Offset, lineEnd = line.EndOffset;
            for (int i = FirstTokenEndingAfter(lineStart); i < _tokens.Count; i++)
            {
                var t = _tokens[i];
                if (t.Start >= lineEnd) break;
                bool unmatched = t.Kind == DslTokenKind.Bracket && _brackets.IsUnmatched(i);
                var brush = t.Kind != DslTokenKind.Bracket ? DslPalette.For(t.Kind)
                    : unmatched ? DslPalette.Error
                    : DslPalette.BracketDepth[_brackets.Depth[i] % DslPalette.BracketDepth.Length];
                if (brush == null) continue;
                int s = Math.Max(t.Start, lineStart), e = Math.Min(t.End, lineEnd);
                if (e <= s) continue;
                bool error = t.Kind == DslTokenKind.Error || unmatched;
                bool italic = t.Kind == DslTokenKind.Comment;
                bool bold = t.Kind == DslTokenKind.Keyword;
                ChangeLinePart(s, e, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(brush);
                    if (error)
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    if (italic || bold)
                    {
                        var tf = el.TextRunProperties.Typeface;
                        el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily,
                            italic ? FontStyle.Italic : tf.Style,
                            bold ? FontWeight.Bold : tf.Weight));
                    }
                });
            }
        }

        // Binary search: first token whose end is past the line start.
        private int FirstTokenEndingAfter(int offset)
        {
            int lo = 0, hi = _tokens.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_tokens[mid].End <= offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>
    /// #1364 — draws a box behind the matched bracket pair next to the caret.
    /// The editor invalidates the layer when the caret moves.
    /// </summary>
    public sealed class DslBracketPairRenderer : IBackgroundRenderer
    {
        private readonly DslHighlighter _highlighter;
        private readonly Func<int> _caret;

        public DslBracketPairRenderer(DslHighlighter highlighter, Func<int> caret)
        {
            _highlighter = highlighter;
            _caret = caret;
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView.Document is not { } doc || !textView.VisualLinesValid) return;
            var (tokens, brackets) = _highlighter.Analyze(doc);
            if (DslBrackets.PairAtCaret(tokens, brackets, _caret()) is not { } pair) return;
            foreach (int idx in new[] { pair.Open, pair.Close })
            {
                var seg = new TextSegment { StartOffset = tokens[idx].Start, Length = tokens[idx].Length };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                    drawingContext.DrawRectangle(DslPalette.PairFill, DslPalette.PairPen, r);
            }
        }
    }
}
