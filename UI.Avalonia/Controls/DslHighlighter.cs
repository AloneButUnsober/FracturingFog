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
    /// that overlap it.
    /// </summary>
    public sealed class DslHighlighter : DocumentColorizingTransformer
    {
        private DslDialect _dialect;
        private List<DslToken> _tokens = new();
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

        protected override void ColorizeLine(DocumentLine line)
        {
            var doc = CurrentContext.Document;
            var version = doc.Version;
            if (_version == null || version == null || !_version.BelongsToSameDocumentAs(version)
                || _version.CompareAge(version) != 0)
            {
                _tokens = DslTokenizer.Tokenize(doc.Text, _dialect);
                _version = version;
            }

            int lineStart = line.Offset, lineEnd = line.EndOffset;
            for (int i = FirstTokenEndingAfter(lineStart); i < _tokens.Count; i++)
            {
                var t = _tokens[i];
                if (t.Start >= lineEnd) break;
                var brush = DslPalette.For(t.Kind);
                if (brush == null) continue;
                int s = Math.Max(t.Start, lineStart), e = Math.Min(t.End, lineEnd);
                if (e <= s) continue;
                bool error = t.Kind == DslTokenKind.Error;
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
}
