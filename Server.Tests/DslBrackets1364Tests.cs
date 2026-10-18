// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1364 (S3 of #1360) — bracket matching for the DSL code view: depth for
/// rainbow colouring, partners for the caret pair box / Ctrl+], unmatched
/// brackets for the error style.
/// </summary>
public sealed class DslBrackets1364Tests
{
    private static (List<DslToken> Tokens, DslBracketMap Map) Run(string src)
    {
        var tokens = DslTokenizer.Tokenize(src, DslDialect.Bulb3D);
        return (tokens, DslBrackets.Match(tokens, src));
    }

    // Bracket tokens only, as (char, depth, partner char offset or -1).
    private static List<(char Ch, int Depth, int PartnerAt)> Brackets(string src)
    {
        var (t, m) = Run(src);
        var list = new List<(char, int, int)>();
        for (int i = 0; i < t.Count; i++)
            if (t[i].Kind == DslTokenKind.Bracket)
                list.Add((src[t[i].Start], m.Depth[i], m.Partner[i] >= 0 ? t[m.Partner[i]].Start : -1));
        return list;
    }

    [Fact]
    public void Nested_mixed_brackets_get_depth_and_partners()
    {
        //          0123456789012
        var b = Brackets("((a)[b]{c})");
        Assert.Equal(new (char, int, int)[]
        {
            ('(', 0, 10), ('(', 1, 3), (')', 1, 1), ('[', 1, 6), (']', 1, 4),
            ('{', 1, 9), ('}', 1, 7), (')', 0, 0),
        }, b);
    }

    [Fact]
    public void Stray_close_is_unmatched_without_breaking_later_pairs()
    {
        var b = Brackets("a) (b)");
        Assert.Equal(-1, b[0].PartnerAt);          // stray ')'
        Assert.Equal(5, b[1].PartnerAt);           // '(' pairs with the last ')'
        Assert.Equal(3, b[2].PartnerAt);
    }

    [Fact]
    public void Wrong_type_close_and_unclosed_opens_are_unmatched()
    {
        var mixed = Brackets("( ]");
        Assert.All(mixed, x => Assert.Equal(-1, x.PartnerAt));

        // "( [ )": ')' finds '(' past the unclosed '[', which stays unmatched.
        var skip = Brackets("( [ )");
        Assert.Equal(4, skip[0].PartnerAt);
        Assert.Equal(-1, skip[1].PartnerAt);
        Assert.Equal(0, skip[2].PartnerAt);

        var open = Brackets("sin(z");
        Assert.Equal(-1, open.Single().PartnerAt);
    }

    [Fact]
    public void Brackets_inside_comments_do_not_count()
    {
        var b = Brackets("(z /* ) */ // (\n)");
        Assert.Equal(2, b.Count);
        Assert.Equal(0, b[1].PartnerAt);
    }

    [Fact]
    public void Matches_reference_on_generated_well_formed_input()
    {
        // Independent reference: build balanced strings while recording, for
        // each bracket, its true depth and partner — then the matcher must
        // reproduce exactly that, with nothing unmatched.
        var rng = new Random(1364);
        const string opens = "([{", closes = ")]}";
        for (int k = 0; k < 500; k++)
        {
            var sb = new StringBuilder();
            var depthAt = new Dictionary<int, int>();
            var partnerAt = new Dictionary<int, int>();
            var stack = new Stack<(int Pos, int Type)>();
            int steps = rng.Next(1, 60);
            for (int s = 0; s < steps; s++)
            {
                int r = rng.Next(4);
                if (r == 0 || (r == 1 && stack.Count == 0))
                {
                    int type = rng.Next(3);
                    depthAt[sb.Length] = stack.Count;
                    stack.Push((sb.Length, type));
                    sb.Append(opens[type]);
                }
                else if (r == 1)
                {
                    var (pos, type) = stack.Pop();
                    depthAt[sb.Length] = stack.Count;
                    partnerAt[sb.Length] = pos;
                    partnerAt[pos] = sb.Length;
                    sb.Append(closes[type]);
                }
                else sb.Append(r == 2 ? "z" : " + ");
            }
            while (stack.Count > 0)
            {
                var (pos, type) = stack.Pop();
                depthAt[sb.Length] = stack.Count;
                partnerAt[sb.Length] = pos;
                partnerAt[pos] = sb.Length;
                sb.Append(closes[type]);
            }

            string src = sb.ToString();
            var (t, m) = Run(src);
            for (int i = 0; i < t.Count; i++)
            {
                if (t[i].Kind != DslTokenKind.Bracket) continue;
                Assert.Equal(depthAt[t[i].Start], m.Depth[i]);
                Assert.True(m.Partner[i] >= 0, $"unmatched in balanced {src}");
                Assert.Equal(partnerAt[t[i].Start], t[m.Partner[i]].Start);
            }
        }
    }

    [Fact]
    public void Structural_invariants_hold_on_arbitrary_input()
    {
        var rng = new Random(13640);
        const string alphabet = "()[]{}z+ /*\n";
        for (int k = 0; k < 2000; k++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(0, 40);
            for (int j = 0; j < len; j++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
            string src = sb.ToString();
            var (t, m) = Run(src);
            var pairs = new List<(int A, int B)>();
            for (int i = 0; i < t.Count; i++)
            {
                int p = m.Partner[i];
                if (t[i].Kind != DslTokenKind.Bracket) { Assert.Equal(DslBracketMap.NotBracket, p); continue; }
                if (p < 0) continue;
                Assert.Equal(i, m.Partner[p]);                               // symmetric
                Assert.Equal(m.Depth[i], m.Depth[p]);                        // same depth
                char a = src[t[Math.Min(i, p)].Start], b = src[t[Math.Max(i, p)].Start];
                Assert.Equal("([{".IndexOf(a), ")]}".IndexOf(b));            // open before close, same type
                if (i < p) pairs.Add((i, p));
            }
            // Matched pairs nest properly: no two pairs cross.
            foreach (var x in pairs)
                foreach (var y in pairs)
                    Assert.False(x.A < y.A && y.A < x.B && x.B < y.B, $"crossing pairs in {src}");
        }
    }

    [Fact]
    public void PairAtCaret_prefers_the_bracket_after_the_caret()
    {
        const string src = "a(b)(c)";   // brackets at 1,3,4,6
        var (t, m) = Run(src);
        int At(int tokenIndex) => t[tokenIndex].Start;

        var before = DslBrackets.PairAtCaret(t, m, 1);           // caret before '(' at 1
        Assert.Equal((1, 3), (At(before!.Value.Open), At(before.Value.Close)));

        var between = DslBrackets.PairAtCaret(t, m, 4);          // after ')' at 3, before '(' at 4
        Assert.Equal((4, 6), (At(between!.Value.Open), At(between.Value.Close)));

        var after = DslBrackets.PairAtCaret(t, m, 7);            // after the last ')'
        Assert.Equal((4, 6), (At(after!.Value.Open), At(after.Value.Close)));

        Assert.Null(DslBrackets.PairAtCaret(t, m, 0));           // not next to a bracket
        var (t2, m2) = Run("(z");                                 // unmatched: no pair
        Assert.Null(DslBrackets.PairAtCaret(t2, m2, 0));
    }
}
