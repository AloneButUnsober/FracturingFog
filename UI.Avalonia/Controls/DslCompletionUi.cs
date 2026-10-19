// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.Controls
{
    /// <summary>
    /// #1366 — one entry in the DSL completion list. Functions insert
    /// "name(" so signature help opens straight away.
    /// </summary>
    public sealed class DslCompletionData : ICompletionData
    {
        private readonly DslCompletionItem _item;

        public DslCompletionData(DslCompletionItem item) => _item = item;

        public DslCompletionItem Item => _item;

        public global::Avalonia.Media.IImage? Image => null;

        public string Text => _item.Text;

        public object Content => new TextBlock
        {
            Inlines = new InlineCollection
            {
                new Run(Glyph(_item.Kind) + "  ") { Foreground = BrushFor(_item.Kind) },
                new Run(_item.Text),
            },
        };

        public object Description => _item.Description;

        // Functions and names you are most likely to want first.
        public double Priority => _item.Kind switch
        {
            DslCompletionKind.Name => 3,
            DslCompletionKind.Variable => 2,
            DslCompletionKind.Function => 1,
            _ => 0,
        };

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            string insert = _item.Kind == DslCompletionKind.Function ? _item.Text + "(" : _item.Text;
            textArea.Document.Replace(completionSegment, insert);
        }

        private static string Glyph(DslCompletionKind kind) => kind switch
        {
            DslCompletionKind.Function => "fn",
            DslCompletionKind.Keyword => "k",
            DslCompletionKind.Type => "T",
            DslCompletionKind.Variable => "v",
            DslCompletionKind.Member => ".",
            _ => "•",
        };

        private static IBrush BrushFor(DslCompletionKind kind) => kind switch
        {
            DslCompletionKind.Function => DslPalette.Function,
            DslCompletionKind.Keyword or DslCompletionKind.Type => DslPalette.Keyword,
            DslCompletionKind.Variable or DslCompletionKind.Member => DslPalette.Variable,
            _ => DslPalette.Operator,
        };
    }

    /// <summary>
    /// #1366 — signature help for one built-in: header "name(a, b)" with the
    /// argument at the caret in bold, content = the description. The DSL has
    /// no overloads, so there is always exactly one entry.
    /// </summary>
    public sealed class DslSignatureProvider : IOverloadProvider
    {
        private DslSignature _signature;
        private int _arg;

        public DslSignatureProvider(DslSignature signature, int argIndex)
        {
            _signature = signature;
            _arg = argIndex;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DslSignature Signature => _signature;

        public void Update(DslSignature signature, int argIndex)
        {
            if (ReferenceEquals(signature, _signature) && argIndex == _arg) return;
            _signature = signature;
            _arg = argIndex;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
        }

        public int SelectedIndex { get => 0; set { } }

        public int Count => 1;

        public string? CurrentIndexText => null;

        public object CurrentHeader
        {
            get
            {
                var tb = new TextBlock { Inlines = new InlineCollection { new Run(_signature.Name + "(") } };
                for (int i = 0; i < _signature.Params.Count; i++)
                {
                    if (i > 0) tb.Inlines!.Add(new Run(", "));
                    var run = new Run(_signature.Params[i]);
                    // Only the current argument gets a colour: a null Foreground
                    // would blank the run instead of inheriting.
                    if (i == _arg)
                    {
                        run.FontWeight = FontWeight.Bold;
                        run.Foreground = DslPalette.Function;
                    }
                    tb.Inlines!.Add(run);
                }
                tb.Inlines!.Add(new Run(")"));
                return tb;
            }
        }

        public object CurrentContent => _signature.Doc;
    }
}
