// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/BatchStereo.cs
//
// #1012 (S5 of the autostereogram epic #1014) — stereo output for the batch
// zoom video. Stills go through PosterRenderer, which already renders every
// stereo mode; the video loop renders frames itself, so this carries the same
// behaviour there:
//
//   * True stereo on a 3D raymarcher renders both eyes inside the frame render
//     (StereoRender.RenderTrueStereo) — the frame IS the side-by-side buffer.
//   * Fake (depth-parallax) and the autostereogram are a post step on the graded
//     frame + its depth, exactly where the live host runs them (after the grade /
//     view transform): the 3D calculator's published depth, or the Relief G-buffer
//     (Relief has one camera, so True maps to the warp there, as live).
//
// FrameW / FrameH are the output frame size the writers must use (2W × H for a
// Full-SBS pair, W × H for Half-SBS and the autostereogram).
//
// Lives in Engine (not the WinExe beside BatchRenderer) so the test project can
// reach it; ApplyFlags is the --stereo* → lighting mapping BatchRenderer uses.

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;

namespace FracturingFog.Batch
{
    public enum BatchStereoKind
    {
        /// <summary>Two eye renders of a 3D raymarcher.</summary>
        True3D,
        /// <summary>Fake warp / autostereogram over a 3D calculator's depth.</summary>
        Depth3D,
        /// <summary>Warp / autostereogram over the Relief depth G-buffer.</summary>
        DepthRelief,
    }

    public sealed class BatchStereo
    {
        public BatchStereoKind Kind { get; }
        public LightingFxData Fx { get; }
        public int FrameW { get; }
        public int FrameH { get; }

        private BatchStereo(BatchStereoKind kind, LightingFxData fx, int frameW, int frameH)
        {
            Kind = kind; Fx = fx; FrameW = frameW; FrameH = frameH;
        }

        /// <summary>The stereo plan for a render of <paramref name="type"/> at
        /// <paramref name="w"/> × <paramref name="h"/>, or null when stereo is off
        /// or has nothing to work with (flat 2D: no depth, no second camera).</summary>
        public static BatchStereo? For(FractalType type, bool relief, in LightingFxData fx, int w, int h)
        {
            if (fx.StereoMode == StereoMode.Off) return null;
            bool auto = fx.StereoMode == StereoMode.Autostereogram;
            if (!auto && fx.StereoEyeSeparation <= 0.0) return null;
            var (sw, sh) = auto ? (w, h) : StereoRender.OutputDims(w, h, fx.StereoLayout);

            if (FractalViewState.IsThreeD(type))
            {
                return fx.StereoMode == StereoMode.True
                    ? new BatchStereo(BatchStereoKind.True3D, fx, sw, sh)
                    : new BatchStereo(BatchStereoKind.Depth3D, fx, sw, sh);
            }
            return relief ? new BatchStereo(BatchStereoKind.DepthRelief, fx, sw, sh) : null;
        }

        /// <summary>The <c>--stereo*</c> flags written onto a lighting block (over
        /// the region / <c>--lighting-preset</c> values); unset flags keep its
        /// values. A True / Fake pair with no eye separation would render mono,
        /// so it gets a comfortable default (0.06).</summary>
        public static LightingFxData ApplyFlags(LightingFxData fx, BatchOptions opts)
        {
            if (opts.Stereo.HasValue)              fx.StereoMode = opts.Stereo.Value;
            if (opts.StereoEyeSep.HasValue)        fx.StereoEyeSeparation = opts.StereoEyeSep.Value;
            if (opts.StereoConvergence.HasValue)   fx.StereoConvergence = opts.StereoConvergence.Value;
            if (opts.StereoMaxDisparity.HasValue)  fx.StereoMaxDisparity = opts.StereoMaxDisparity.Value;
            if (opts.StereoFov.HasValue)           fx.StereoFovDegrees = opts.StereoFov.Value;
            if (opts.StereoLayout.HasValue)        fx.StereoLayout = opts.StereoLayout.Value;
            if (opts.StereoSwapEyes)               fx.StereoSwapEyes = true;
            if (opts.AutostereoPattern.HasValue)   fx.StereoAutoPattern = opts.AutostereoPattern.Value;
            if (opts.AutostereoEyeSep.HasValue)    fx.StereoAutoEyeSep = opts.AutostereoEyeSep.Value;
            if (opts.AutostereoDepth.HasValue)     fx.StereoAutoDepthOfField = opts.AutostereoDepth.Value;
            if (opts.AutostereoSmoothing.HasValue) fx.StereoAutoBlur = opts.AutostereoSmoothing.Value;
            if (opts.AutostereoLevels.HasValue)    fx.StereoAutoLevels = opts.AutostereoLevels.Value;
            if (opts.AutostereoCrossEyed)          fx.StereoAutoCrossEyed = true;
            if (opts.AutostereoNoGuideDots)        fx.StereoAutoGuideDots = false;
            if (opts.AutostereoSeed.HasValue)      fx.StereoAutoSeed = opts.AutostereoSeed.Value;
            if (fx.StereoMode is StereoMode.True or StereoMode.Fake && fx.StereoEyeSeparation <= 0.0)
                fx.StereoEyeSeparation = 0.06;
            return fx;
        }

        /// <summary>A one-line description for the batch console header.</summary>
        public string Describe()
        {
            string what = Fx.StereoMode switch
            {
                StereoMode.Autostereogram => $"autostereogram ({Fx.StereoAutoPattern}{(Fx.StereoAutoCrossEyed ? ", cross-eyed" : "")})",
                StereoMode.True when Kind == BatchStereoKind.True3D => $"true SBS ({Fx.StereoLayout})",
                _ => $"depth-parallax SBS ({Fx.StereoLayout})",
            };
            return $"{what} -> {FrameW}x{FrameH}";
        }

        /// <summary>The post-frame step for the depth kinds, with this frame's
        /// lighting (<paramref name="fx"/> — an animation may move its settings;
        /// the frame size stays the plan's): the graded mono
        /// frame (<paramref name="w"/> × <paramref name="h"/>) + its depth → the
        /// stereo frame (<see cref="FrameW"/> × <see cref="FrameH"/>). Returns the
        /// mono frame unchanged when no depth came back (e.g. a cancelled or
        /// thin-lens frame) — the caller then pads it to the frame size.</summary>
        public uint[] Finish(uint[] graded, int w, int h, float[]? depth3D,
            HeightfieldRaymarch2D.ReliefAovBuffers? reliefAov, in LightingFxData fx)
        {
            uint[]? stereo = Kind switch
            {
                BatchStereoKind.Depth3D when depth3D != null && depth3D.Length >= w * h
                    => ScreenSpacePost.ApplyDepthStereo(graded, depth3D, w, h, in fx, out _, out _),
                BatchStereoKind.DepthRelief
                    => FracturingFog.Imaging.ReliefScreenSpacePost.ApplyStereo(graded, reliefAov, w, h, in fx, out _, out _),
                _ => null,
            };
            return stereo ?? FitToFrame(graded, w, h);
        }

        /// <summary>Keep the writer's frame size when a frame could not be made
        /// stereo: the mono frame, left-aligned on a black frame.</summary>
        public uint[] FitToFrame(uint[] mono, int w, int h)
        {
            if (w == FrameW && h == FrameH) return mono;
            var frame = new uint[FrameW * FrameH];
            System.Array.Fill(frame, 0xFF000000u);
            for (int y = 0; y < System.Math.Min(h, FrameH); y++)
                System.Array.Copy(mono, y * w, frame, y * FrameW, System.Math.Min(w, FrameW));
            return frame;
        }
    }
}
