// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>
    /// Live owner of the DSL-editor "code view" toggle (#1362). One flag per
    /// editor key (a window: "UserEquation", "Sandbox", "UserBulb"); every
    /// <see cref="Controls.DslCodeEditor"/> with the same key follows it, so the
    /// User Bulb source, DE body and chain-step boxes switch together.
    /// Persistence delegates to <see cref="DslEditorPrefsStore"/>.
    ///
    /// UI-thread affinity: mutate only from the UI thread.
    /// </summary>
    public static class DslEditorPrefsService
    {
        private static readonly DslEditorPrefs _prefs = DslEditorPrefsStore.Load();

        /// <summary>Raised after a key's code-view flag changes (arg = key).</summary>
        public static event Action<string>? CodeViewChanged;

        public static bool GetCodeView(string key)
            => _prefs.CodeView.TryGetValue(key, out bool on) && on;

        public static void SetCodeView(string key, bool on)
        {
            if (GetCodeView(key) == on) return;
            _prefs.CodeView[key] = on;
            DslEditorPrefsStore.Save(_prefs);
            CodeViewChanged?.Invoke(key);
        }

        /// <summary>#1419 — minimise brackets on Paste LaTeX / Paste MathML.</summary>
        public static bool MinimiseImportBrackets
        {
            get => _prefs.MinimiseImportBrackets;
            set
            {
                if (_prefs.MinimiseImportBrackets == value) return;
                _prefs.MinimiseImportBrackets = value;
                DslEditorPrefsStore.Save(_prefs);
            }
        }
    }
}
