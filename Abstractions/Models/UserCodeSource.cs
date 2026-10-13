// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/UserCodeSource.cs
//
// #1249 — the equation source of the user-code types (User Equation, Sandbox,
// User Bulb) on the batch command line. A render of one of them is defined by
// its source text, which no other flag carries:
//   --equation NAME        a saved equation of the family (the store entry;
//                          a User Equation's per-equation render settings come
//                          with it, as selecting it in the editor does);
//   --equation-source TEXT the source itself, for unsaved or edited equations.
//                          Newlines are written as \n (and a backslash as \\)
//                          so the command stays one line.
// The Command builder emits the name only when the saved entry reproduces the
// live source AND settings exactly; otherwise the inline source. Settings that
// differ from the defaults ride along as --param (the region snapshot).

using System;
using System.Text;

namespace FracturingFog.Models
{
    public static class UserCodeSource
    {
        /// <summary>True for the types whose render is defined by a source text.</summary>
        public static bool IsUserCode(FractalType t)
            => t is FractalType.UserEquation or FractalType.Sandbox or FractalType.UserBulb;

        /// <summary>The live source of a user-code type (null otherwise).</summary>
        public static string? SourceOf(FractalType t, FractalParameters p) => t switch
        {
            FractalType.UserEquation => p.UserEquationSource,
            FractalType.Sandbox => p.SandboxSource,
            FractalType.UserBulb => p.UserBulbSource,
            _ => null,
        };

        /// <summary>The saved name the live equation was loaded from / saved as.</summary>
        public static string? NameOf(FractalType t, FractalParameters p) => t switch
        {
            FractalType.UserEquation => p.UserEquationName,
            FractalType.Sandbox => p.SandboxName,
            FractalType.UserBulb => p.UserBulbName,
            _ => null,
        };

        /// <summary>The saved name that reproduces the live equation exactly
        /// (same source; for a User Equation also the same render settings), or
        /// null — then the command must carry the source inline.</summary>
        public static string? SavedNameMatching(FractalType t, FractalParameters p)
        {
            string? name = NameOf(t, p);
            if (string.IsNullOrWhiteSpace(name)) return null;
            string live = Normalize(SourceOf(t, p));
            switch (t)
            {
                case FractalType.UserEquation:
                    var ue = UserEquationStore.Instance.GetByName(name);
                    return ue != null && Normalize(ue.Source) == live && ue.RenderSettingsMatch(p) ? ue.Name : null;
                case FractalType.Sandbox:
                    var sb = SandboxEquationStore.Instance.GetByName(name);
                    return sb != null && Normalize(sb.Source) == live ? sb.Name : null;
                case FractalType.UserBulb:
                    var ub = UserBulbStore.Instance.GetByName(name);
                    return ub != null && Normalize(ub.Source) == live ? ub.Name : null;
                default:
                    return null;
            }
        }

        /// <summary>Load the saved equation <paramref name="name"/> of family
        /// <paramref name="t"/> onto <paramref name="p"/>. False (with the reason)
        /// when the family has no equations or the name is not saved.</summary>
        public static bool TryApplySaved(FractalType t, string name, FractalParameters p, out string? error)
        {
            error = null;
            switch (t)
            {
                case FractalType.UserEquation:
                    if (UserEquationStore.Instance.GetByName(name) is not { } ue) break;
                    ue.ApplyTo(p);
                    ue.ApplyRenderSettingsTo(p);
                    p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
                    return true;
                case FractalType.Sandbox:
                    if (SandboxEquationStore.Instance.GetByName(name) is not { } sb) break;
                    p.SandboxSource = sb.Source;
                    p.SandboxName = sb.Name;
                    return true;
                case FractalType.UserBulb:
                    if (UserBulbStore.Instance.GetByName(name) is not { } ub) break;
                    p.UserBulbSource = ub.Source;
                    p.UserBulbName = ub.Name;
                    p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
                    return true;
                default:
                    error = $"--equation applies to UserEquation, Sandbox and UserBulb, not {t}.";
                    return false;
            }
            error = $"No saved {t} equation named '{name}'.";
            return false;
        }

        /// <summary>Set the source of family <paramref name="t"/> from the
        /// command line (already <see cref="Decode"/>d). It is not a saved
        /// entry, so the name is cleared.</summary>
        public static bool TryApplyInline(FractalType t, string source, FractalParameters p, out string? error)
        {
            error = null;
            switch (t)
            {
                case FractalType.UserEquation: p.UserEquationSource = source; p.UserEquationName = null; break;
                case FractalType.Sandbox: p.SandboxSource = source; p.SandboxName = null; break;
                case FractalType.UserBulb: p.UserBulbSource = source; p.UserBulbName = null; break;
                default:
                    error = $"--equation-source applies to UserEquation, Sandbox and UserBulb, not {t}.";
                    return false;
            }
            // Typed by the user on their own command line.
            p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
            return true;
        }

        /// <summary>One-line form of a source: newline → \n, backslash → \\
        /// (CR dropped, tabs kept).</summary>
        public static string Encode(string source)
        {
            var sb = new StringBuilder(source.Length + 8);
            foreach (char ch in source)
            {
                if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch != '\r') sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Inverse of <see cref="Encode"/>. A backslash before any
        /// other character is kept as is.</summary>
        public static string Decode(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == 'n' || text[i + 1] == '\\'))
                {
                    sb.Append(text[i + 1] == 'n' ? '\n' : '\\');
                    i++;
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n").Trim();
    }
}
