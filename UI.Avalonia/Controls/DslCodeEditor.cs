// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Services;

namespace FracturingFog.UI.Avalonia.Controls
{
    /// <summary>Which DSL an editor box holds — drives highlighting / completion
    /// in later #1360 slices.</summary>
    public enum DslLanguage
    {
        /// <summary>User Equation (CalcGen DSL, single source #1088).</summary>
        Equation,
        /// <summary>Sandbox 2D expression DSL.</summary>
        Sandbox,
        /// <summary>User Bulb 3D step / DE-body DSL.</summary>
        Bulb,
    }

    /// <summary>
    /// Shared DSL equation editor (#1362, slice S1 of #1360). Hosts two editors
    /// over one <see cref="Text"/>:
    /// <list type="bullet">
    /// <item>plain view — a <see cref="TextBox"/>, exactly the pre-#1360 editor;</item>
    /// <item>code view — an AvaloniaEdit <see cref="TextEditor"/> with line
    /// numbers and syntax colouring (<see cref="DslHighlighter"/>, #1363);
    /// brackets, squiggles, completion and format arrive in S3-S6.</item>
    /// </list>
    /// The stored text is always plain — formatting is display-only, so turning
    /// code view off IS "remove formatting", and saves / --batch never see it.
    ///
    /// <see cref="TextEditor"/> has no bindable Text (only <c>Document</c>), so
    /// this control owns a two-way <see cref="Text"/> and syncs the document by
    /// minimal replace (keeps caret and undo history intact on VM pushes).
    ///
    /// The mode is shared per <see cref="PrefsKey"/> via
    /// <see cref="DslEditorPrefsService"/>; boxes without a key keep a local mode.
    /// </summary>
    public sealed class DslCodeEditor : UserControl
    {
        public static readonly StyledProperty<string?> TextProperty =
            AvaloniaProperty.Register<DslCodeEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<DslLanguage> LanguageProperty =
            AvaloniaProperty.Register<DslCodeEditor, DslLanguage>(nameof(Language));

        public static readonly StyledProperty<string?> PrefsKeyProperty =
            AvaloniaProperty.Register<DslCodeEditor, string?>(nameof(PrefsKey));

        public static readonly StyledProperty<bool> IsCodeViewProperty =
            AvaloniaProperty.Register<DslCodeEditor, bool>(nameof(IsCodeView));

        public static readonly StyledProperty<bool> ShowModeToggleProperty =
            AvaloniaProperty.Register<DslCodeEditor, bool>(nameof(ShowModeToggle), defaultValue: true);

        public static readonly StyledProperty<bool> AcceptsReturnProperty =
            AvaloniaProperty.Register<DslCodeEditor, bool>(nameof(AcceptsReturn), defaultValue: true);

        public static readonly StyledProperty<string?> PlaceholderTextProperty =
            AvaloniaProperty.Register<DslCodeEditor, string?>(nameof(PlaceholderText));

        /// <summary>The equation source (two-way by default).</summary>
        public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

        public DslLanguage Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }

        /// <summary>Shared mode key ("UserEquation", "Sandbox", "UserBulb").</summary>
        public string? PrefsKey { get => GetValue(PrefsKeyProperty); set => SetValue(PrefsKeyProperty, value); }

        /// <summary>True = AvaloniaEdit code view; false = plain text box.</summary>
        public bool IsCodeView { get => GetValue(IsCodeViewProperty); set => SetValue(IsCodeViewProperty, value); }

        /// <summary>Show the in-box "{ }" toggle (off for compact chain-step boxes,
        /// which follow their window's key).</summary>
        public bool ShowModeToggle { get => GetValue(ShowModeToggleProperty); set => SetValue(ShowModeToggleProperty, value); }

        /// <summary>False = single-line box (Enter does not insert a newline).</summary>
        public bool AcceptsReturn { get => GetValue(AcceptsReturnProperty); set => SetValue(AcceptsReturnProperty, value); }

        public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }

        private readonly TextBox _plain;
        private readonly TextEditor _code;
        private readonly TextBlock _codePlaceholder;
        private readonly ToggleButton _toggle;
        // #1367 — Format / Minimise-brackets buttons beside the toggle (code view only).
        private readonly Button _formatButton;
        private readonly Button _parensButton;
        private readonly StackPanel _tools;
        private readonly DslHighlighter _highlighter = new();
        private bool _syncing;

        // #1365 — current error span. Code view: anchors in the document so the
        // underline follows edits until the next validation replaces it. Plain
        // view: a selection, deferred while the box has focus (#1089).
        private TextAnchor? _errStart, _errEnd;
        private string? _errMessage;
        private bool _errIsError = true;
        private int _pendingSelStart = -1, _pendingSelEnd = -1;

        // #1366 — completion list and signature help popups (one of each at most).
        private CompletionWindow? _completion;
        private OverloadInsightWindow? _insight;
        private DslSignatureProvider? _insightProvider;

        public DslCodeEditor()
        {
            _plain = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                BorderThickness = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_plain, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_plain, ScrollBarVisibility.Auto);
            _plain.PropertyChanged += OnPlainPropertyChanged;
            _plain.LostFocus += (_, _) => FlushPendingSelection();

            _code = new TextEditor
            {
                IsVisible = false,
                ShowLineNumbers = true,
                WordWrap = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                LineNumbersForeground = new SolidColorBrush(Color.Parse("#808080")),
                Padding = new Thickness(2, 2, 0, 0),
            };
            _code.Options.EnableHyperlinks = false;
            _code.Options.EnableEmailHyperlinks = false;
            _code.Options.HighlightCurrentLine = false;
            _code.TextArea.Caret.CaretBrush = Brushes.White;
            _code.Document.TextChanged += OnCodeTextChanged;
            _code.TextArea.AddHandler(KeyDownEvent, OnCodeKeyDown, RoutingStrategies.Tunnel);
            // #1366 smoke — AvaloniaEdit inserts whatever text input arrives, unlike
            // TextBox: Ctrl+- (U+001F), Ctrl+] (U+001D) and Ctrl+Space (" ") put
            // stray characters into the equation. Ctrl chords never type here;
            // the window's Ctrl+= / Ctrl+- / Ctrl+0 UI-scale keys still fire.
            _code.TextArea.AddHandler(TextInputEvent, OnCodeTextInput, RoutingStrategies.Tunnel);
            _code.TextArea.TextView.LineTransformers.Add(_highlighter);
            // #1364 — box the bracket pair at the caret; redraw it as the caret moves.
            _code.TextArea.TextView.BackgroundRenderers.Add(
                new DslBracketPairRenderer(_highlighter, () => _code.CaretOffset));
            _code.TextArea.Caret.PositionChanged += (_, _) =>
                _code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
            // #1365 — wavy underline under the error span + its message on hover.
            _code.TextArea.TextView.BackgroundRenderers.Add(new DslSquiggleRenderer(this));
            _code.TextArea.TextView.PointerMoved += OnCodePointerMoved;
            _code.TextArea.TextView.PointerExited += (_, _) => ToolTip.SetIsOpen(_code.TextArea.TextView, false);
            // #1366 — completion as you type, signature help inside calls.
            _code.TextArea.TextEntered += OnCodeTextEntered;
            _code.TextArea.Caret.PositionChanged += (_, _) => UpdateSignatureHelp(openIfClosed: false);

            _codePlaceholder = new TextBlock
            {
                IsVisible = false,
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                Margin = new Thickness(40, 3, 0, 0),
            };

            _toggle = new ToggleButton
            {
                Content = "{ }",
                FontSize = 11,
                Padding = new Thickness(5, 0),
                MinHeight = 0,
                Opacity = 0.8,
                Focusable = false,
            };
            ToolTip.SetTip(_toggle,
                "Code view: line numbers, syntax colouring, bracket matching (Ctrl+] jumps to the " +
                "matching bracket), error underlines, completion (Ctrl+Space) and Format (Shift+Alt+F) (#1360). " +
                "Off = plain text box. " +
                "The equation text is the same either way — code view never changes what is saved.");
            _toggle.IsCheckedChanged += (_, _) =>
            {
                bool on = _toggle.IsChecked == true;
                if (PrefsKey is { } key) DslEditorPrefsService.SetCodeView(key, on);
                else IsCodeView = on;
            };

            _formatButton = MakeToolButton("Format",
                "Format: tidy spacing and indentation (Shift+Alt+F). Only whitespace changes; " +
                "refused if it would change the equation. Undo with Ctrl+Z.");
            _formatButton.Click += (_, _) => RunFormat(minimiseParens: false);
            _parensButton = MakeToolButton("( )",
                "Minimise brackets: remove brackets that don't change the equation " +
                "(checked against the parsed equation; needs an equation that parses). Undo with Ctrl+Z.");
            _parensButton.Click += (_, _) => RunFormat(minimiseParens: true);

            _tools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 20, 0),
            };
            _tools.Children.Add(_formatButton);
            _tools.Children.Add(_parensButton);
            _tools.Children.Add(_toggle);

            var grid = new Grid();
            grid.Children.Add(_plain);
            grid.Children.Add(_code);
            grid.Children.Add(_codePlaceholder);
            grid.Children.Add(_tools);
            Content = grid;
        }

        /// <summary>True while either inner editor has keyboard focus.</summary>
        public bool IsEditorFocused => IsKeyboardFocusWithin;

        /// <summary>#1366 — extra names to offer in completion, asked for each time
        /// the list opens (User Bulb params and chain-step outputs). Inherited, so
        /// a view sets it once on itself and templated editors (chain steps) see it.</summary>
        public static readonly AttachedProperty<Func<IEnumerable<string>>?> HostNamesProviderProperty =
            AvaloniaProperty.RegisterAttached<DslCodeEditor, Control, Func<IEnumerable<string>>?>(
                "HostNamesProvider", inherits: true);

        public static void SetHostNamesProvider(Control element, Func<IEnumerable<string>>? value)
            => element.SetValue(HostNamesProviderProperty, value);

        public static Func<IEnumerable<string>>? GetHostNamesProvider(Control element)
            => element.GetValue(HostNamesProviderProperty);

        /// <summary>
        /// #1365 — show a validation result over [start, start+length).
        /// Code view: a wavy underline (yellow for errors, blue for hints such as
        /// "C#-style, Ctrl+Period converts") with <paramref name="message"/> on hover;
        /// it moves with edits and never touches the caret or selection.
        /// Plain view: the pre-#1360 behaviour — the span is selected, deferred
        /// to focus loss so typing is never replaced by the selection.
        /// A length of 0 clears.
        /// </summary>
        public void ShowError(int start, int length, string? message, bool isError = true)
        {
            if (length <= 0) { ClearError(); return; }
            var doc = _code.Document;
            int s = Math.Clamp(start, 0, doc.TextLength);
            int e = Math.Clamp(start + length, s, doc.TextLength);
            _errStart = doc.CreateAnchor(s);
            _errEnd = doc.CreateAnchor(e);
            _errMessage = message;
            _errIsError = isError;
            _code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);

            if (!IsCodeView)
            {
                _pendingSelStart = s;
                _pendingSelEnd = e;
                if (!_plain.IsKeyboardFocusWithin) FlushPendingSelection();
            }
            else
            {
                _pendingSelStart = _pendingSelEnd = -1;
            }
        }

        /// <summary>#1365 — remove the error underline / pending selection.</summary>
        public void ClearError()
        {
            _errStart = _errEnd = null;
            _errMessage = null;
            _pendingSelStart = _pendingSelEnd = -1;
            _code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
            ToolTip.SetIsOpen(_code.TextArea.TextView, false);
        }

        /// <summary>#1365 — the live error span (follows edits), or null when there
        /// is none or its text was deleted.</summary>
        public (int Start, int Length, string? Message, bool IsError)? CurrentError
        {
            get
            {
                if (_errStart is not { IsDeleted: false } a || _errEnd is not { IsDeleted: false } b) return null;
                int len = b.Offset - a.Offset;
                return len > 0 ? (a.Offset, len, _errMessage, _errIsError) : null;
            }
        }

        private void FlushPendingSelection()
        {
            if (_pendingSelStart < 0) return;
            int len = (_plain.Text ?? string.Empty).Length;
            _plain.SelectionStart = Math.Min(_pendingSelStart, len);
            _plain.SelectionEnd = Math.Min(_pendingSelEnd, len);
            _pendingSelStart = _pendingSelEnd = -1;
        }

        private void OnCodePointerMoved(object? sender, PointerEventArgs e)
        {
            var view = _code.TextArea.TextView;
            var pos = view.GetPosition(e.GetPosition(view) + view.ScrollOffset);
            int offset = pos is { } p ? _code.Document.GetOffset(p.Location) : -1;
            string? tip = null;
            // #1365 — the error message wins over the hover doc.
            if (CurrentError is { } err && !string.IsNullOrEmpty(err.Message)
                && offset >= err.Start && offset <= err.Start + err.Length)
            {
                tip = err.Message;
            }
            else if (offset >= 0)
            {
                // #1366 — description of the built-in function / input under the pointer.
                var (tokens, _) = _highlighter.Analyze(_code.Document);
                foreach (var t in tokens)
                {
                    if (t.Start > offset) break;
                    if (offset >= t.End) continue;
                    if (t.Kind is DslTokenKind.Function or DslTokenKind.Variable)
                        tip = DslCompletion.DocFor(_code.Document.GetText(t.Start, t.Length), DialectOf(Language));
                    break;
                }
            }
            if (tip != null)
            {
                ToolTip.SetTip(view, tip);
                ToolTip.SetIsOpen(view, true);
            }
            else
            {
                ToolTip.SetIsOpen(view, false);
            }
        }

        // #1366 — open the list on an identifier character (or '.' for a Bulb
        // member), signature help on '(' / ','.
        private void OnCodeTextEntered(object? sender, TextInputEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            char ch = e.Text[^1];
            if (ch is '(' or ',')
            {
                _completion?.Close();
                UpdateSignatureHelp(openIfClosed: true);
                return;
            }
            if (ch == ')') { UpdateSignatureHelp(openIfClosed: false); return; }
            if (_completion == null && (char.IsLetter(ch) || ch == '_' || (ch == '.' && Language == DslLanguage.Bulb)))
                ShowCompletion(force: false);
        }

        /// <summary>#1366 — open the completion list at the caret. Unforced (while
        /// typing) it only opens when something matches.</summary>
        private void ShowCompletion(bool force)
        {
            int caret = _code.CaretOffset;
            string text = _code.Document.Text;
            var dialect = DialectOf(Language);
            if (DslCompletion.ContextAt(text, caret, dialect) is not { } ctx) return;
            var items = DslCompletion.Items(text, caret, dialect, GetHostNamesProvider(this)?.Invoke());
            if (items.Count == 0 || (!force && ctx.Prefix.Length == 0 && !ctx.IsMember)) return;

            _completion?.Close();
            var window = new CompletionWindow(_code.TextArea) { StartOffset = ctx.Start, CloseWhenCaretAtBeginning = true };
            foreach (var item in items) window.CompletionList.CompletionData.Add(new DslCompletionData(item));
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completion, window)) _completion = null;
                // A function was picked (inserted "name("): show its signature.
                UpdateSignatureHelp(openIfClosed: true);
            };
            _completion = window;
            window.Show();
            if (ctx.Prefix.Length > 0) window.CompletionList.SelectItem(ctx.Prefix);
        }

        /// <summary>#1366 — keep signature help in step with the caret: update the
        /// bold argument, close outside a built-in call, open on request.</summary>
        private void UpdateSignatureHelp(bool openIfClosed)
        {
            if (!IsCodeView) return;
            var at = DslCompletion.SignatureAt(_code.Document.Text, _code.CaretOffset, DialectOf(Language));
            if (at is not { } sig)
            {
                _insight?.Close();
                return;
            }
            if (_insight != null && _insightProvider != null)
            {
                _insightProvider.Update(sig.Signature, sig.ArgIndex);
                return;
            }
            if (!openIfClosed) return;
            _insightProvider = new DslSignatureProvider(sig.Signature, sig.ArgIndex);
            var window = new OverloadInsightWindow(_code.TextArea) { Provider = _insightProvider };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_insight, window)) { _insight = null; _insightProvider = null; }
            };
            _insight = window;
            window.Show();
        }

        /// <summary>
        /// Make <paramref name="doc"/> read <paramref name="text"/> by replacing
        /// only the span between the common prefix and suffix. A VM push that
        /// changes one token (Apply fix, migration) then keeps the caret, the
        /// scroll position and a single undo step instead of a whole-document swap.
        /// </summary>
        public static void ReplaceMinimal(TextDocument doc, string text)
        {
            string old = doc.Text;
            if (string.Equals(old, text, StringComparison.Ordinal)) return;
            int prefix = 0;
            int max = Math.Min(old.Length, text.Length);
            while (prefix < max && old[prefix] == text[prefix]) prefix++;
            int suffix = 0;
            while (suffix < max - prefix
                   && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
            doc.Replace(prefix, old.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DslEditorPrefsService.CodeViewChanged += OnPrefsChanged;
            LoadModeFromPrefs();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            // Static event: unsubscribe so closed windows are not kept alive.
            DslEditorPrefsService.CodeViewChanged -= OnPrefsChanged;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty)
            {
                if (_syncing) return;
                _syncing = true;
                try
                {
                    string text = Text ?? string.Empty;
                    if (_plain.Text != text) _plain.Text = text;
                    ReplaceMinimal(_code.Document, text);
                }
                finally { _syncing = false; }
                UpdatePlaceholder();
            }
            else if (change.Property == LanguageProperty)
            {
                _highlighter.Dialect = DialectOf(Language);
                _code.TextArea.TextView.Redraw();
            }
            else if (change.Property == IsCodeViewProperty)
            {
                ApplyMode();
            }
            else if (change.Property == PrefsKeyProperty)
            {
                if (VisualRoot != null) LoadModeFromPrefs();
            }
            else if (change.Property == ShowModeToggleProperty)
            {
                _tools.IsVisible = ShowModeToggle;
            }
            else if (change.Property == AcceptsReturnProperty)
            {
                _plain.AcceptsReturn = AcceptsReturn;
                // A single-line box has no lines to number.
                _code.ShowLineNumbers = AcceptsReturn;
            }
            else if (change.Property == PlaceholderTextProperty)
            {
                _plain.PlaceholderText = PlaceholderText;
                _codePlaceholder.Text = PlaceholderText;
                UpdatePlaceholder();
            }
            else if (change.Property == BackgroundProperty)
            {
                _plain.Background = Background;
                _code.Background = Background;
            }
        }

        /// <summary>User Equation and Sandbox both run on the Sandbox 2D
        /// interpreter, so they share its dialect.</summary>
        public static DslDialect DialectOf(DslLanguage language)
            => language == DslLanguage.Bulb ? DslDialect.Bulb3D : DslDialect.Complex2D;

        private void OnPrefsChanged(string key)
        {
            if (key == PrefsKey) LoadModeFromPrefs();
        }

        private void LoadModeFromPrefs()
        {
            if (PrefsKey is { } key) IsCodeView = DslEditorPrefsService.GetCodeView(key);
        }

        private void ApplyMode()
        {
            bool code = IsCodeView;
            bool hadFocus = IsKeyboardFocusWithin;
            if (!code) { _completion?.Close(); _insight?.Close(); }
            _toggle.IsChecked = code;
            _formatButton.IsVisible = code;
            _parensButton.IsVisible = code;
            if (code)
            {
                int caret = Math.Clamp(_plain.CaretIndex, 0, _code.Document.TextLength);
                _plain.IsVisible = false;
                _code.IsVisible = true;
                _code.CaretOffset = caret;
                if (hadFocus) _code.TextArea.Focus();
            }
            else
            {
                int caret = Math.Clamp(_code.CaretOffset, 0, (_plain.Text ?? string.Empty).Length);
                _code.IsVisible = false;
                _plain.IsVisible = true;
                _plain.CaretIndex = caret;
                if (hadFocus) _plain.Focus();
            }
            UpdatePlaceholder();
        }

        private void UpdatePlaceholder()
            => _codePlaceholder.IsVisible = IsCodeView
                                            && !string.IsNullOrEmpty(PlaceholderText)
                                            && string.IsNullOrEmpty(Text);

        private void OnPlainPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != TextBox.TextProperty || _syncing) return;
            _syncing = true;
            try
            {
                string text = _plain.Text ?? string.Empty;
                SetCurrentValue(TextProperty, text);
                ReplaceMinimal(_code.Document, text);
            }
            finally { _syncing = false; }
            UpdatePlaceholder();
        }

        private void OnCodeTextChanged(object? sender, EventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                string text = _code.Document.Text;
                SetCurrentValue(TextProperty, text);
                if (_plain.Text != text) _plain.Text = text;
            }
            finally { _syncing = false; }
            UpdatePlaceholder();
        }

        private static Button MakeToolButton(string content, string tip)
        {
            var b = new Button
            {
                Content = content,
                FontSize = 11,
                Padding = new Thickness(5, 0),
                MinHeight = 0,
                Opacity = 0.8,
                Focusable = false,
                IsVisible = false,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        /// <summary>#1367 — apply Format or Minimise brackets to the code view as
        /// one undo step, and say what happened (applied / already tidy / why
        /// refused) in a short-lived tooltip.</summary>
        private void RunFormat(bool minimiseParens)
        {
            if (!IsCodeView) return;
            _completion?.Close();
            _insight?.Close();
            var dialect = DialectOf(Language);
            string before = _code.Document.Text;
            var result = minimiseParens
                ? DslFormatter.MinimiseParens(before, dialect)
                : DslFormatter.Format(before, dialect);
            if (result.Ok && result.Text != before)
            {
                int caret = _code.CaretOffset;
                ReplaceMinimal(_code.Document, result.Text);
                _code.CaretOffset = Math.Min(caret, _code.Document.TextLength);
            }
            Flash(_tools.IsVisible ? (minimiseParens ? _parensButton : _formatButton) : this, result.Message);
        }

        private DispatcherTimer? _flashTimer;
        private Control? _flashAnchor;
        private object? _flashNormalTip;

        // Show a message as the control's tooltip (below it) for a few seconds,
        // then put its normal tooltip back. A new message first ends the old one.
        private void Flash(Control anchor, string message)
        {
            EndFlash();
            _flashAnchor = anchor;
            _flashNormalTip = ToolTip.GetTip(anchor);
            ToolTip.SetPlacement(anchor, PlacementMode.Bottom);
            ToolTip.SetTip(anchor, message);
            ToolTip.SetIsOpen(anchor, true);
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _flashTimer.Tick += (_, _) => EndFlash();
            _flashTimer.Start();
        }

        private void EndFlash()
        {
            _flashTimer?.Stop();
            _flashTimer = null;
            if (_flashAnchor is not { } a) return;
            ToolTip.SetIsOpen(a, false);
            ToolTip.SetTip(a, _flashNormalTip);
            _flashAnchor = null;
            _flashNormalTip = null;
        }

        // Modifiers of the last key press: text input that follows a Ctrl chord
        // (Ctrl without Alt — Ctrl+Alt is AltGr and must still type) is dropped.
        private bool _ctrlChord;

        /// <summary>#1366 — true when text input should not reach the document:
        /// any control character other than tab / newline, or anything typed as
        /// part of a Ctrl chord.</summary>
        public static bool ShouldDropTextInput(string? text, bool ctrlChord)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (ctrlChord) return true;
            foreach (char ch in text)
                if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r')) return true;
            return false;
        }

        private void OnCodeTextInput(object? sender, TextInputEventArgs e)
        {
            if (ShouldDropTextInput(e.Text, _ctrlChord)) e.Handled = true;
        }

        private void OnCodeKeyDown(object? sender, KeyEventArgs e)
        {
            _ctrlChord = e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            // #1367 — Shift+Alt+F formats (the VS Code key; Ctrl+ is the UI scale).
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt))
            {
                RunFormat(minimiseParens: false);
                e.Handled = true;
                return;
            }
            // #1366 — Ctrl+Space opens completion even with nothing typed yet.
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
            {
                ShowCompletion(force: true);
                e.Handled = true;
                return;
            }
            // #1364 — Ctrl+] jumps between a bracket next to the caret and its partner.
            if (e.Key == Key.OemCloseBrackets && e.KeyModifiers == KeyModifiers.Control)
            {
                var (tokens, brackets) = _highlighter.Analyze(_code.Document);
                if (DslBrackets.PairAtCaret(tokens, brackets, _code.CaretOffset) is { } pair)
                {
                    // Land in front of the other bracket, so a second press jumps back.
                    int caret = _code.CaretOffset;
                    bool atOpen = caret == tokens[pair.Open].Start || caret == tokens[pair.Open].End;
                    int target = tokens[atOpen ? pair.Close : pair.Open].Start;
                    // Empty pair "(|)": the close starts at the caret, so go to the open.
                    if (target == caret) target = tokens[pair.Open].Start;
                    _code.CaretOffset = target;
                    _code.TextArea.Caret.BringCaretToView();
                }
                e.Handled = true;
                return;
            }
            // Single-line boxes (chain steps): Enter must not split the step.
            if (!AcceptsReturn && e.Key == Key.Enter) e.Handled = true;
        }
    }
}
