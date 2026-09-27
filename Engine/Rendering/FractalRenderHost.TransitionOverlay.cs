// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/FractalRenderHost.TransitionOverlay.cs
//
// #988 (#940 slice B) — animated region cross-fade for the slideshow. The
// host has one live pipeline, so two regions can't both animate. Instead the
// incoming region is committed live straight away (its animation starts),
// and a frozen snapshot of the outgoing frame is blended OVER every upload
// with a weight the slideshow ramps 1 → 0. Outgoing dissolves while the
// incoming fades in already moving.
//
// Blended after the grid / watermark composite so the labels cross-fade with
// the image (the outgoing snapshot carries its own watermark). While active,
// the low-res progressive preview and stale-hold presents are suppressed:
// they bypass UploadProcessedBuffer and would pop the incoming region on
// screen un-blended.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Rendering
{
    public sealed partial class FractalRenderHost
    {
        // Guarded by _uploadGate (read inside UploadProcessedBuffer).
        private uint[]? _transitionOverlay;
        private int _transitionOverlayW, _transitionOverlayH;
        // Weight of the outgoing frame, 0..1.
        private volatile float _transitionOverlayWeight;

        /// <summary>True while a region-transition overlay is installed.</summary>
        private bool TransitionOverlayActive => _transitionOverlay != null;

        /// <inheritdoc/>
        public bool BeginTransitionOverlay(uint[] outgoing, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(outgoing);
            if (_disposed || width <= 0 || height <= 0) return false;
            int n = width * height;
            if (outgoing.Length < n) return false;

            var copy = new uint[n];
            Array.Copy(outgoing, copy, n);
            lock (_uploadGate)
            {
                _transitionOverlay = copy;
                _transitionOverlayW = width;
                _transitionOverlayH = height;
                _transitionOverlayWeight = 1f;
            }
            return true;
        }

        /// <inheritdoc/>
        public void SetTransitionOverlay(double outgoingWeight)
            => _transitionOverlayWeight = (float)Math.Clamp(outgoingWeight, 0.0, 1.0);

        /// <inheritdoc/>
        public void EndTransitionOverlay()
        {
            lock (_uploadGate)
            {
                _transitionOverlay = null;
                _transitionOverlayWeight = 0f;
            }
        }

        // Called from UploadProcessedBuffer under _uploadGate on the final
        // display buffer. No-op when inactive, fully transparent, or when the
        // frame's dims don't match the snapshot (resize / SBS stereo) — then the
        // incoming frame shows as-is rather than blending mismatched pixels.
        private void ApplyTransitionOverlay(uint[] dst, int w, int h)
        {
            var ov = _transitionOverlay;
            if (ov == null) return;
            float a = _transitionOverlayWeight;
            if (a <= 0f || w != _transitionOverlayW || h != _transitionOverlayH) return;
            int n = w * h;
            if (dst.Length < n || ov.Length < n) return;

            if (a >= 1f)
            {
                Array.Copy(ov, dst, n);
                return;
            }

            BlendOutgoing(dst, ov, n, a);
        }

        /// <summary>dst = lerp(dst, outgoing, weight) per RGB channel; result
        /// opaque. Public for tests.</summary>
        public static void BlendOutgoing(uint[] dst, uint[] outgoing, int n, float weight)
        {
            float a = Math.Clamp(weight, 0f, 1f);
            float ia = 1f - a;
            Parallel.For(0, Math.Max(1, Environment.ProcessorCount), part =>
            {
                int parts = Math.Max(1, Environment.ProcessorCount);
                int start = (int)((long)n * part / parts);
                int end = (int)((long)n * (part + 1) / parts);
                for (int i = start; i < end; i++)
                {
                    uint nw = dst[i], o = outgoing[i];
                    uint r = (uint)(((nw >> 16) & 0xFF) * ia + ((o >> 16) & 0xFF) * a + 0.5f);
                    uint g = (uint)(((nw >> 8) & 0xFF) * ia + ((o >> 8) & 0xFF) * a + 0.5f);
                    uint b = (uint)((nw & 0xFF) * ia + (o & 0xFF) * a + 0.5f);
                    dst[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
            });
        }
    }
}
