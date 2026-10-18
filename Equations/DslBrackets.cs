// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslBrackets.cs
//
// #1364 (S3 of #1360) — bracket structure over a DslTokenizer token stream:
// nesting depth (for rainbow colouring), each bracket's partner (for the caret
// pair highlight and jump-to-match), and which brackets are unmatched (shown
// with the error style). Pure; brackets inside comments are not tokens, so
// they never count.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>Per-token bracket facts, aligned with the token list passed to
    /// <see cref="DslBrackets.Match"/>.</summary>
    public sealed class DslBracketMap
    {
        /// <summary>Partner token index for a matched bracket.</summary>
        public const int Unmatched = -1;
        /// <summary>Value for tokens that are not brackets.</summary>
        public const int NotBracket = -2;

        /// <summary>Nesting depth of each bracket token (0 = outermost; an open
        /// and its close share a depth). 0 for non-brackets.</summary>
        public int[] Depth { get; }

        /// <summary>Partner token index, <see cref="Unmatched"/>, or
        /// <see cref="NotBracket"/>.</summary>
        public int[] Partner { get; }

        internal DslBracketMap(int[] depth, int[] partner)
        {
            Depth = depth;
            Partner = partner;
        }

        public bool IsUnmatched(int tokenIndex) => Partner[tokenIndex] == Unmatched;
    }

    public static class DslBrackets
    {
        /// <summary>Match brackets in <paramref name="tokens"/> (from
        /// <paramref name="text"/>). A close bracket pairs with the nearest open
        /// bracket of the same type; a close with no open of its type is
        /// unmatched and leaves the open stack alone, so one stray `)` does not
        /// unmatch everything after it. Opens left at the end are unmatched.</summary>
        public static DslBracketMap Match(IReadOnlyList<DslToken> tokens, string text)
        {
            var depth = new int[tokens.Count];
            var partner = new int[tokens.Count];
            var stack = new List<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != DslTokenKind.Bracket) { partner[i] = DslBracketMap.NotBracket; continue; }
                char ch = text[tokens[i].Start];
                if (ch is '(' or '[' or '{')
                {
                    depth[i] = stack.Count;
                    partner[i] = DslBracketMap.Unmatched;
                    stack.Add(i);
                    continue;
                }
                char open = ch switch { ')' => '(', ']' => '[', _ => '{' };
                int s = stack.Count - 1;
                while (s >= 0 && text[tokens[stack[s]].Start] != open) s--;
                if (s < 0)
                {
                    depth[i] = stack.Count;
                    partner[i] = DslBracketMap.Unmatched;
                    continue;
                }
                // Opens above the match were never closed: they stay unmatched.
                int o = stack[s];
                stack.RemoveRange(s, stack.Count - s);
                depth[i] = depth[o];
                partner[i] = o;
                partner[o] = i;
            }
            return new DslBracketMap(depth, partner);
        }

        /// <summary>The matched pair touching <paramref name="caret"/> (bracket
        /// just after the caret first, else just before), as token indices, or
        /// null when the caret is not next to a matched bracket.</summary>
        public static (int Open, int Close)? PairAtCaret(IReadOnlyList<DslToken> tokens, DslBracketMap map, int caret)
        {
            int hit = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Start > caret) break;
                if (map.Partner[i] < 0) continue;
                if (tokens[i].Start == caret) { hit = i; break; }
                if (tokens[i].End == caret) hit = i;
            }
            if (hit < 0) return null;
            int other = map.Partner[hit];
            return hit < other ? (hit, other) : (other, hit);
        }
    }
}
