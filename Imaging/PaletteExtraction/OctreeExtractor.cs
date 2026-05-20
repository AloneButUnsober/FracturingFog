// Imaging/PaletteExtraction/OctreeExtractor.cs
//
// Octree quantization (Gervautz/Purgathofer). Each pixel walks down an
// 8-way tree keyed by the high bits of R/G/B until the max depth, where
// it lands in (or creates) a leaf accumulator. A "reducible" set tracks
// internal nodes whose children are *all* leaves — these are the only
// nodes safe to collapse next. While the leaf count exceeds the target,
// repeatedly pick a reducible node (deepest first, lowest pixel count
// to minimize information loss), merge its children's accumulators in,
// turn it into a leaf, and notify its parent in case the parent itself
// just became reducible.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class OctreeExtractor : IPaletteExtractor
    {
        public string Name => "Octree";

        // 6 levels = 64 cells per axis. Deep enough for ~262k leaves at
        // worst case; shallow enough that the tree stays small for
        // typical downsampled images (256-pixel thumbnails).
        private const int MaxDepth = 6;

        private sealed class Node
        {
            public Node?[]? Children;
            public Node? Parent;
            public int Depth;
            public long RSum, GSum, BSum;
            public int PixelCount;
            public bool InReducible;
        }

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            int k = Math.Max(2, opts.ColorCount);
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();

            var root = new Node { Depth = 0 };
            int leafCount = 0;

            // reducible[d] = internal nodes at depth d whose every existing
            // child is itself a leaf. HashSet for O(1) add/remove.
            var reducible = new HashSet<Node>[MaxDepth];
            for (int i = 0; i < MaxDepth; i++) reducible[i] = new HashSet<Node>();

            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3];
                byte g = rgb[i * 3 + 1];
                byte b = rgb[i * 3 + 2];
                InsertPixel(root, r, g, b, ref leafCount, reducible);
            }

            // Safety cap on reductions; should never bind given proper tree
            // accounting, but kept as a belt-and-suspenders guard against
            // infinite loops on pathological inputs.
            int safety = leafCount + 16;

            while (leafCount > k && safety-- > 0)
            {
                int depth = MaxDepth - 1;
                while (depth >= 0 && reducible[depth].Count == 0) depth--;
                if (depth < 0) break;

                Node? pick = null;
                int pickPixels = int.MaxValue;
                foreach (var n in reducible[depth])
                {
                    int total = 0;
                    var ch = n.Children!;
                    for (int c = 0; c < 8; c++)
                        if (ch[c] != null) total += ch[c]!.PixelCount;
                    if (total < pickPixels)
                    {
                        pickPixels = total;
                        pick = n;
                    }
                }

                if (pick == null) break;

                reducible[depth].Remove(pick);
                pick.InReducible = false;

                int mergedChildren = 0;
                var pc = pick.Children!;
                for (int c = 0; c < 8; c++)
                {
                    var child = pc[c];
                    if (child == null) continue;
                    pick.RSum += child.RSum;
                    pick.GSum += child.GSum;
                    pick.BSum += child.BSum;
                    pick.PixelCount += child.PixelCount;
                    pc[c] = null;
                    mergedChildren++;
                }
                pick.Children = null;
                leafCount = leafCount - mergedChildren + 1;

                // Pick is now a leaf; its parent may have just become
                // reducible (all remaining children are leaves).
                var parent = pick.Parent;
                if (parent != null && !parent.InReducible && AllChildrenLeaves(parent))
                {
                    reducible[parent.Depth].Add(parent);
                    parent.InReducible = true;
                }
            }

            var leaves = new List<ExtractedColor>();
            Collect(root, leaves);
            return leaves;
        }

        private static void InsertPixel(Node root, byte r, byte g, byte b,
                                        ref int leafCount, HashSet<Node>[] reducible)
        {
            Node node = root;
            while (node.Depth < MaxDepth)
            {
                int shift = 7 - node.Depth;
                int idx = (((r >> shift) & 1) << 2)
                        | (((g >> shift) & 1) << 1)
                        | ((b >> shift) & 1);

                if (node.Children == null)
                    node.Children = new Node?[8];

                var child = node.Children[idx];
                if (child == null)
                {
                    child = new Node
                    {
                        Depth = node.Depth + 1,
                        Parent = node,
                    };
                    node.Children[idx] = child;

                    bool willBeLeafNow = node.Depth + 1 == MaxDepth;
                    if (willBeLeafNow)
                    {
                        leafCount++;
                        if (!node.InReducible && AllChildrenLeaves(node))
                        {
                            reducible[node.Depth].Add(node);
                            node.InReducible = true;
                        }
                    }
                    else
                    {
                        // New child will spawn its own subtree → can't be a
                        // leaf yet, so the parent isn't reducible anymore.
                        if (node.InReducible)
                        {
                            reducible[node.Depth].Remove(node);
                            node.InReducible = false;
                        }
                    }
                }
                else
                {
                    // Walking through an existing internal node — no leaf-count
                    // change, no reducibility change.
                }
                node = node.Children[idx]!;
            }

            // node is at MaxDepth — leaf accumulator.
            node.RSum += r;
            node.GSum += g;
            node.BSum += b;
            node.PixelCount++;
        }

        private static bool AllChildrenLeaves(Node n)
        {
            if (n.Children == null) return false;
            bool any = false;
            for (int c = 0; c < 8; c++)
            {
                var ch = n.Children[c];
                if (ch == null) continue;
                if (ch.Children != null) return false;
                any = true;
            }
            return any;
        }

        private static void Collect(Node node, List<ExtractedColor> outList)
        {
            if (node.Children == null)
            {
                if (node.PixelCount == 0) return;
                int n = node.PixelCount;
                outList.Add(new ExtractedColor(
                    (byte)(node.RSum / n),
                    (byte)(node.GSum / n),
                    (byte)(node.BSum / n),
                    n));
                return;
            }
            for (int c = 0; c < 8; c++)
                if (node.Children[c] != null)
                    Collect(node.Children[c]!, outList);
        }
    }
}
