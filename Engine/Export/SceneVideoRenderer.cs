// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Export/SceneVideoRenderer.cs
//
// Scene Engine Roadmap — Phase S7: the offline, frame-locked scene renderer.
//
// Realtime playback (S6) walks a wall clock and cuts between shots to stay
// inside the ~90% CPU/mem cap. Offline has no such clock: it renders each
// output frame deterministically, one at a time, so it can afford the two
// things S6 deferred here:
//
//   * Accumulation motion blur — render N sub-frames per output frame at
//     sub-tick times and average them (SceneRenderPlan schedules the sub-frame
//     times + weights).
//   * Frame-composited cross-fades — blend the frozen last frame of the
//     outgoing shot into the accumulated incoming frame by the timeline's blend
//     factor (SceneRenderPlan flags which frames composite).
//
// Only one calculator is live at a time (each already parallelises its own
// scanlines), so peak memory is one frame's worth of accumulators plus the
// pending PNG-encode queue — well inside the cap. Frames go to a PNG sequence
// (PngSequenceWriter, cross-platform SkiaSharp) and are post-encoded by ffmpeg,
// exactly the pipeline BatchRenderer's video/slideshow paths use.
//
// Self-contained: shots resolve against the region / theme / animation
// libraries directly, no live render host. That keeps this callable from a
// headless batch entry (BatchRenderer.RenderScene) and from a future Avalonia
// "Export Scene…" command alike.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.Export
{
    /// <summary>Options for an offline scene render.</summary>
    public sealed class SceneVideoOptions
    {
        /// <summary>Output pixel width (snapped to even for the encoder).</summary>
        public int Width { get; set; } = 1920;

        /// <summary>Output pixel height (snapped to even for the encoder).</summary>
        public int Height { get; set; } = 1080;

        /// <summary>Frame rate + motion-blur controls.</summary>
        public SceneRenderSettings Settings { get; set; } = new();

        /// <summary>ffmpeg encode preset. Default is visually-lossless H.264 MP4.</summary>
        public FfmpegEncoder.Preset Encode { get; set; } = FfmpegEncoder.Preset.HighQualityH264Mp4;

        /// <summary>Final container path. When it has no extension it is treated
        /// as a folder and a name is synthesised.</summary>
        public string OutputPath { get; set; } = "";

        /// <summary>Keep the intermediate PNG sequence after a successful encode.</summary>
        public bool KeepFrames { get; set; }

        /// <summary>Deterministic, seekable audio source for the scene's
        /// <see cref="SceneData.AudioTracks"/> (Audio-Reactive Phase 7 / #266). When
        /// set and <see cref="IAudioModulationSource.IsActive"/>, each sub-frame
        /// samples it at its scene-global time (<c>SampleAt</c>) and applies the
        /// scene's audio tracks on top of the shot params — so the render is
        /// reproducible from the audio timeline, never the wall clock. Null = the
        /// scene renders audio-silent (shots + keyframe globals only). The caller
        /// bakes this via <c>OfflineAudioAnalysis.AnalyzeFile</c>.</summary>
        public IAudioModulationSource? AudioSource { get; set; }

        /// <summary>Optional audio file to mux into the encoded container after a
        /// successful video encode (Phase 7). Normally the same file
        /// <see cref="AudioSource"/> was baked from, so the exported MP4 carries its
        /// music. Null / missing = a silent video.</summary>
        public string? AudioMuxPath { get; set; }
    }

    /// <summary>Outcome of a scene render.</summary>
    public readonly struct SceneVideoResult
    {
        public SceneVideoResult(bool ok, int framesWritten, string? videoPath,
                                string? frameFolder, string? message)
        {
            Ok = ok;
            FramesWritten = framesWritten;
            VideoPath = videoPath;
            FrameFolder = frameFolder;
            Message = message;
        }

        /// <summary>True when the frames rendered and (if ffmpeg was available)
        /// the encode succeeded.</summary>
        public bool Ok { get; }

        public int FramesWritten { get; }

        /// <summary>Encoded container path, or null when only a PNG sequence was
        /// produced (ffmpeg missing).</summary>
        public string? VideoPath { get; }

        /// <summary>PNG sequence folder — non-null when kept (ffmpeg missing or
        /// <see cref="SceneVideoOptions.KeepFrames"/>).</summary>
        public string? FrameFolder { get; }

        public string? Message { get; }
    }

    public static class SceneVideoRenderer
    {
        /// <summary>Render <paramref name="scene"/> to a video file per
        /// <paramref name="options"/>. Progress is reported as (fraction 0..1,
        /// status line). Throws only on argument / setup errors; a failed encode
        /// is surfaced through <see cref="SceneVideoResult.Ok"/>.</summary>
        public static SceneVideoResult Render(
            SceneData scene, SceneVideoOptions options,
            Action<double, string>? progress = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(options);

            var plan = SceneRenderPlan.Build(scene, options.Settings);
            if (plan.IsEmpty)
                return new SceneVideoResult(false, 0, null, null,
                    "Scene has no shots with a positive duration to render.");

            int w = options.Width & ~1;
            int h = options.Height & ~1;
            if (w < 16) w = 16;
            if (h < 16) h = 16;

            // Resolve every referenced shot once — geometry, params, theme,
            // animation, camera. Keyed by source-shot index.
            var resolved = new Dictionary<int, ResolvedShot>();
            foreach (var frame in plan.Frames)
            {
                Resolve(scene, frame.PrimaryOriginalIndex, resolved);
                if (frame.CompositeTransition)
                    Resolve(scene, frame.OutgoingOriginalIndex, resolved);
                foreach (var s in frame.SubFrames)
                    Resolve(scene, s.OriginalIndex, resolved);
            }

            // Output target.
            string outPath = options.OutputPath;
            string ext = "." + FfmpegEncoder.DefaultExtensionFor(options.Encode);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(outPath)))
            {
                Directory.CreateDirectory(string.IsNullOrWhiteSpace(outPath) ? "." : outPath);
                string baseName = "FF_Scene_" + Sanitize(scene.Name) + "_" +
                                  DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
                outPath = Path.Combine(string.IsNullOrWhiteSpace(outPath) ? "." : outPath, baseName);
            }
            else
            {
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            string pngFolder = Path.Combine(
                Path.GetTempPath(), "FracturingFog", "scene-render",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(pngFolder);

            // Scene-wide post/look tracks (S8) — sampled at each sub-frame's
            // GLOBAL time and applied on top of the shot's own params.
            var globalTracks = scene.GlobalTracks;

            // Scene-wide audio-reactive tracks (Phase 7) — deterministic: sampled
            // from the seekable source at each sub-frame's GLOBAL scene time. Only
            // live when a source is supplied and active; otherwise the scene renders
            // audio-silent exactly as before.
            IAudioModulationSource? audioSource =
                (options.AudioSource is { IsActive: true } && scene.AudioTracks is { Count: > 0 })
                    ? options.AudioSource : null;
            var audioTracks = audioSource != null ? scene.AudioTracks : null;

            int n = w * h;
            var accum = new float[n * 3];     // weighted RGB accumulator
            var outBuf = new uint[n];
            var frozenCache = new Dictionary<int, uint[]>();
            int framesWritten = 0;

            using (var png = new PngSequenceWriter(pngFolder, w, h))
            {
                foreach (var frame in plan.Frames)
                {
                    ct.ThrowIfCancellationRequested();
                    Array.Clear(accum, 0, accum.Length);

                    // ── Resolve the transition rendering mode for this frame ──
                    // Crossfade / LightSweep composite a frozen outgoing frame;
                    // ParamMorph instead renders the incoming shot with its
                    // params interpolated from the outgoing shot's (same type
                    // only — else it degrades to a crossfade).
                    var visual = frame.ResolvedTransition;
                    FractalParameters? morphBase = null;
                    if (frame.CompositeTransition && visual == SceneTransitionKind.ParamMorph)
                    {
                        var inc = Get(resolved, frame.PrimaryOriginalIndex);
                        var outg = Get(resolved, frame.OutgoingOriginalIndex);
                        if (inc != null && outg != null && inc.RenderType == outg.RenderType)
                            morphBase = SceneParamMorph.Lerp(outg.BaseParams, inc.BaseParams, frame.Blend);
                        else
                            visual = SceneTransitionKind.Crossfade; // nothing to morph
                    }
                    bool frozenComposite = frame.CompositeTransition
                        && (visual == SceneTransitionKind.Crossfade || visual == SceneTransitionKind.LightSweep);

                    // ── Accumulation motion blur: weighted average of sub-frames ──
                    foreach (var s in frame.SubFrames)
                    {
                        // Under a ParamMorph, the incoming shot's sub-frames render
                        // with the morphed base params for this frame's blend.
                        FractalParameters? overrideBase =
                            (morphBase != null && s.OriginalIndex == frame.PrimaryOriginalIndex)
                                ? morphBase : null;
                        uint[] buf = RenderShotFrame(resolved, s.OriginalIndex, s.LocalTime, w, h, ct,
                            overrideBase, s.GlobalTime, globalTracks, audioSource, audioTracks);
                        float wt = (float)s.Weight;
                        for (int i = 0; i < n; i++)
                        {
                            uint p = buf[i];
                            int b = (int)(p & 0xFF);
                            int g = (int)((p >> 8) & 0xFF);
                            int r = (int)((p >> 16) & 0xFF);
                            int j = i * 3;
                            accum[j]     += r * wt;
                            accum[j + 1] += g * wt;
                            accum[j + 2] += b * wt;
                        }
                    }

                    // ── Frame-composited transition (Crossfade uniform / LightSweep wipe) ──
                    if (frozenComposite)
                    {
                        uint[] frozen = GetFrozen(resolved, frame.OutgoingOriginalIndex,
                                                  frame.OutgoingLocalTime, w, h, frozenCache, ct);
                        bool sweep = visual == SceneTransitionKind.LightSweep;
                        double blendC = frame.Blend;   // 1 = incoming, 0 = outgoing
                        for (int i = 0; i < n; i++)
                        {
                            // Per-pixel incoming weight: uniform for a crossfade,
                            // a swept soft edge for a light-sweep.
                            float bl;
                            if (sweep)
                            {
                                int x = i % w;
                                double u = w > 1 ? (double)x / (w - 1) : 0.0;
                                bl = (float)SceneTransitions.LightSweepWeight(u, blendC);
                            }
                            else bl = (float)blendC;
                            float ibl = 1f - bl;

                            uint fp = frozen[i];
                            int fb = (int)(fp & 0xFF);
                            int fg = (int)((fp >> 8) & 0xFF);
                            int fr = (int)((fp >> 16) & 0xFF);
                            int j = i * 3;
                            float r = accum[j]     * bl + fr * ibl;
                            float g = accum[j + 1] * bl + fg * ibl;
                            float b = accum[j + 2] * bl + fb * ibl;
                            outBuf[i] = Pack(r, g, b);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int j = i * 3;
                            outBuf[i] = Pack(accum[j], accum[j + 1], accum[j + 2]);
                        }
                    }

                    png.WriteFrame(outBuf);
                    framesWritten++;
                    progress?.Invoke((double)framesWritten / plan.TotalFrames,
                        $"frame {framesWritten}/{plan.TotalFrames}");
                }
            } // Dispose drains the PNG queue.

            // ── Encode ──
            if (!FfmpegEncoder.IsAvailable())
            {
                return new SceneVideoResult(false, framesWritten, null, pngFolder,
                    "ffmpeg not found — kept PNG sequence at " + pngFolder + ".");
            }

            progress?.Invoke(1.0, "encoding " + Path.GetFileName(outPath));
            var (ok, log) = FfmpegEncoder
                .EncodeAsync(pngFolder, outPath, options.Encode, plan.Fps, ct)
                .GetAwaiter().GetResult();

            if (!ok)
            {
                return new SceneVideoResult(false, framesWritten, null, pngFolder,
                    "ffmpeg encode failed (PNG sequence kept at " + pngFolder + "):\n" + Tail(log));
            }

            // ── Mux audio (Phase 7 / #266) ──
            // Attach the analysed audio file to the encoded video so the exported
            // MP4 actually carries its music. A mux failure is non-fatal: keep the
            // (silent) video rather than losing the whole render.
            if (!string.IsNullOrWhiteSpace(options.AudioMuxPath) && File.Exists(options.AudioMuxPath))
            {
                progress?.Invoke(1.0, "muxing audio");
                string muxed = outPath + ".audio" + ext;
                var (mok, _) = FfmpegEncoder
                    .MuxAudioAsync(outPath, options.AudioMuxPath!, muxed, ct)
                    .GetAwaiter().GetResult();
                if (mok)
                {
                    try
                    {
                        File.Delete(outPath);
                        File.Move(muxed, outPath);
                    }
                    catch { try { File.Delete(muxed); } catch { /* best effort */ } }
                }
                else
                {
                    try { File.Delete(muxed); } catch { /* best effort */ }
                }
            }

            if (options.KeepFrames)
                return new SceneVideoResult(true, framesWritten, outPath, pngFolder, null);

            try { Directory.Delete(pngFolder, recursive: true); } catch { /* best effort */ }
            return new SceneVideoResult(true, framesWritten, outPath, null, null);
        }

        // ── Rendering ────────────────────────────────────────────────────────

        private static uint[] RenderShotFrame(
            IReadOnlyDictionary<int, ResolvedShot> cache, int originalIndex,
            double localTime, int w, int h, CancellationToken ct,
            FractalParameters? overrideBase = null,
            double globalTime = 0.0,
            IReadOnlyList<SceneGlobalTrack>? globalTracks = null,
            IAudioModulationSource? audioSource = null,
            IReadOnlyList<SceneAudioTrack>? audioTracks = null)
        {
            if (!cache.TryGetValue(originalIndex, out var shot))
                return BlackFrame(w, h);

            // Fresh params per sub-frame so the animation + camera pose at this
            // local time don't leak into the next sub-frame. overrideBase carries
            // a ParamMorph-interpolated baseline for this frame when present.
            var p = (overrideBase ?? shot.BaseParams).Clone();

            // Param animation at this local time. Procedural animators integrate
            // phase linearly in dt, so a single Tick(localTime) lands at the same
            // pose the bus's many small ticks would, deterministically.
            if (shot.Animation != null)
            {
                foreach (var animator in shot.Animation.ToAnimators(p))
                {
                    if (localTime > 0) animator.Tick(localTime);
                }
            }

            // Keyframed orbit camera at this local time (looped over its own
            // duration, mirroring the S6 CameraTrackAnimator Loop=true).
            if (shot.Camera != null && shot.Camera.Keys.Count > 0
                && CameraParamBinding.Supports(shot.RenderType))
            {
                double camDur = shot.Camera.Duration;
                double t = localTime;
                if (camDur > 0) t -= global::System.Math.Floor(t / camDur) * camDur;
                CameraParamBinding.Apply(p, shot.RenderType, shot.Camera.Evaluate(t));
            }

            // Scene-wide post/look tracks (S8) at this sub-frame's global time —
            // applied last so a scene exposure/bloom ramp overrides the shot's own
            // lighting uniformly across the whole timeline. No-op when the scene
            // has no global tracks (frozen-outgoing renders pass none).
            SceneGlobalTracks.Apply(globalTracks, p, globalTime);

            // Scene-wide audio-reactive tracks (Phase 7 / #266) at this sub-frame's
            // GLOBAL scene time, seeked deterministically from the offline source.
            // Applied after the keyframe globals — the live modulation layer riding
            // the static look. No-op when no source (frozen-outgoing renders, or a
            // scene without audio).
            if (audioSource != null && audioTracks is { Count: > 0 })
                SceneAudioTracks.Apply(audioTracks, p, audioSource.SampleAt(globalTime));

            // Per-shot tone-map override (S8). Applied last so it pins this shot's
            // HDR tone-map regardless of the region lighting; null = inherit.
            if (shot.ToneMap is { } tm)
            {
                var fx = p.Lighting;
                fx.ToneMap = tm;
                p.Lighting = fx;
            }

            var req = new PosterRequest
            {
                FractalType = shot.RenderType,
                Width = w, Height = h,
                CenterX = shot.CenterX, CenterXLo = shot.CenterXLo,
                CenterX2 = shot.CenterX2, CenterX3 = shot.CenterX3,
                CenterY = shot.CenterY, CenterYLo = shot.CenterYLo,
                CenterY2 = shot.CenterY2, CenterY3 = shot.CenterY3,
                Zoom = shot.Zoom,
                MaxIterations = shot.MaxIterations,
                Quality = shot.Quality,
                ColorMap = shot.Theme,
                FractalParameters = p,
            };

            // Relief 3D (#408, scene). When the shot's params enable the oblique
            // raymarch (region.ApplyRelief3DTo populated them, or a global/audio
            // track flipped it on), render the COMPOSED relief+froxel buffer via
            // PosterRenderer.RenderToPixels instead of the flat capture calculator
            // — the scene renderer previously dropped relief entirely here. Like
            // the flat path (and the batch loops), RenderToPixels returns the raw
            // composed colour buffer BEFORE b/c / view-transform / interior
            // composite, so it slots straight into the accumulator.
            //
            // Froxel history is per-call (the req carries none) so froxel fog is
            // spatial-only here: motion-blur sub-frame averaging, shot cuts, and
            // frame-composited transitions make a single shared cross-frame
            // temporal timeline ill-defined. Cross-frame froxel temporal
            // reprojection for scenes is a deliberate follow-up (see roadmap S6).
            if (p.Relief2DEnabled && p.Relief2DRaymarch)
                return PosterRenderer.RenderToPixels(req, ct, out _, out _);

            IFractalCalculator? alt = PosterRenderer.BuildCaptureCalculator(req);
            if (alt != null)
            {
                alt.Calculate(ct);
                return CopyBuffer(alt.ColorBuffer, w, h);
            }

            var calc = new MandelbrotCalculator(w, h)
            {
                CenterX = shot.CenterX, CenterXLo = shot.CenterXLo,
                CenterX2 = shot.CenterX2, CenterX3 = shot.CenterX3,
                CenterY = shot.CenterY, CenterYLo = shot.CenterYLo,
                CenterY2 = shot.CenterY2, CenterY3 = shot.CenterY3,
                Zoom = shot.Zoom,
                MaxIterations = shot.MaxIterations,
                ColorMap = shot.Theme,
                Quality = shot.Quality,
            };
            calc.Calculate(ct);
            return CopyBuffer(calc.ColorBuffer, w, h);
        }

        private static uint[] GetFrozen(
            IReadOnlyDictionary<int, ResolvedShot> cache, int originalIndex,
            double localTime, int w, int h, Dictionary<int, uint[]> frozenCache,
            CancellationToken ct)
        {
            if (frozenCache.TryGetValue(originalIndex, out var f)) return f;
            f = RenderShotFrame(cache, originalIndex, localTime, w, h, ct);
            frozenCache[originalIndex] = f;
            return f;
        }

        // ── Resolution ───────────────────────────────────────────────────────

        private static ResolvedShot? Get(IReadOnlyDictionary<int, ResolvedShot> cache, int index)
            => cache.TryGetValue(index, out var s) ? s : null;

        private static void Resolve(SceneData scene, int originalIndex,
                                    Dictionary<int, ResolvedShot> cache)
        {
            if (originalIndex < 0 || originalIndex >= scene.Shots.Count) return;
            if (cache.ContainsKey(originalIndex)) return;
            cache[originalIndex] = ResolveShot(scene.Shots[originalIndex]);
        }

        private static ResolvedShot ResolveShot(SceneShot shot)
        {
            FractalRegion? region = string.IsNullOrEmpty(shot.RegionName)
                ? null
                : FractalRegionLibrary.Instance.FindByName(shot.RegionName);

            FractalType type = region?.FractalType ?? shot.FractalType;
            var quality = region?.QualityPreset ?? QualityPreset.Standard;

            double zoom = region is { Zoom: > 0 } ? region.Zoom : 1.0;
            int iter = region != null && region.Iterations > 0
                ? region.Iterations
                : quality.ComputeIterations(zoom);

            var p = new FractalParameters();
            if (region != null)
                LoadRegionParams(region, p);

            // #295 follow-up — per-shot lighting override by name. Borrow another
            // region's captured Lighting & FX for this shot, overriding the shot
            // region's own lighting applied above. Precedence: scene global track
            // > this shot override > shot region > theme > default. Applied
            // wholesale (ApplyLighting* replaces p.Lighting), so the named source
            // wins. No-op when unset or the named region is missing.
            if (!string.IsNullOrEmpty(shot.LightingRegionName))
            {
                var lightRegion = FractalRegionLibrary.Instance.FindByName(shot.LightingRegionName);
                if (lightRegion != null)
                {
                    if (lightRegion.LightingIsAuthoritative)
                        lightRegion.ApplyLightingAuthoritative(p);
                    else
                        lightRegion.ApplyLightingTo(p);
                }
            }

            var anim = ResolveAnimation(shot, region);
            var theme = ResolveTheme(shot, region);

            return new ResolvedShot
            {
                RenderType = type,
                CenterX  = region?.CenterX  ?? 0, CenterXLo = region?.CenterXLo ?? 0,
                CenterX2 = region?.CenterX2 ?? 0, CenterX3  = region?.CenterX3  ?? 0,
                CenterY  = region?.CenterY  ?? 0, CenterYLo = region?.CenterYLo ?? 0,
                CenterY2 = region?.CenterY2 ?? 0, CenterY3  = region?.CenterY3  ?? 0,
                Zoom = zoom,
                MaxIterations = iter,
                Quality = quality,
                Theme = theme,
                BaseParams = p,
                Animation = anim,
                Camera = shot.Camera,
                ToneMap = shot.ToneMap,
            };
        }

        // Populate source-compiled equation slots + lighting + per-type camera
        // baseline from the region. The calculators lazily compile from the
        // source strings, so no live host is needed. Mirrors the equation /
        // lighting half of HostColorThemeService.LoadRegionFractalParams.
        private static void LoadRegionParams(FractalRegion region, FractalParameters p)
        {
            // #27 Phase 0 — inline raw-C# source from a cross-user imported
            // region is refused by the gate; local-store sources re-mark trusted.
            p.UserCodeOrigin = region.ExternalOrigin
                ? FracturingFog.Security.UserCodeOrigin.ExternalFile
                : FracturingFog.Security.UserCodeOrigin.Interactive;

            if (region.FractalType == FractalType.UserEquation
                && !string.IsNullOrWhiteSpace(region.UserEquationName))
            {
                var entry = UserEquationStore.Instance.GetByName(region.UserEquationName);
                if (entry != null) { p.UserEquationSource = entry.Source; p.UserEquationName = entry.Name; p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; }
            }
            if (region.FractalType == FractalType.Sandbox
                && !string.IsNullOrWhiteSpace(region.SandboxName))
            {
                var entry = SandboxEquationStore.Instance.GetByName(region.SandboxName);
                if (entry != null) { p.SandboxSource = entry.Source; p.SandboxName = entry.Name; }
            }
            if (region.FractalType == FractalType.UserBulb)
            {
                var entry = !string.IsNullOrWhiteSpace(region.UserBulbName)
                    ? UserBulbStore.Instance.GetByName(region.UserBulbName)
                    : null;
                if (entry != null) { p.UserBulbSource = entry.Source; p.UserBulbName = entry.Name; p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; }
                else if (!string.IsNullOrWhiteSpace(region.UserBulbSource))
                {
                    p.UserBulbSource = region.UserBulbSource;
                    p.UserBulbName = region.UserBulbName;
                }
                if (region.UserBulbCameraDistance > 0)
                {
                    p.UserBulbCameraDistance = region.UserBulbCameraDistance;
                    p.UserBulbCameraTheta = region.UserBulbCameraTheta;
                    p.UserBulbCameraPhi = region.UserBulbCameraPhi;
                    p.UserBulbLightTheta = region.UserBulbLightTheta;
                    p.UserBulbLightPhi = region.UserBulbLightPhi;
                }
            }
            // Region lighting override snapshot (Phase 10) — no-op when null,
            // unless the region opted into authoritative lighting (#295), in
            // which case a null override resets to stock defaults so the
            // rendered scene matches the region's portable look.
            if (region.LightingIsAuthoritative)
                region.ApplyLightingAuthoritative(p);
            else
                region.ApplyLightingTo(p);
            // Region Relief 3D snapshot — no-op when null.
            region.ApplyRelief3DTo(p);
        }

        private static AnimationData? ResolveAnimation(SceneShot shot, FractalRegion? region)
        {
            string? name = !string.IsNullOrEmpty(shot.AnimationName)
                ? shot.AnimationName
                : region?.AnimationName;
            return string.IsNullOrEmpty(name) ? null : AnimationLibrary.Instance.GetByName(name);
        }

        private static IColorMap ResolveTheme(SceneShot shot, FractalRegion? region)
        {
            string? name = shot.ThemeName;
            if (string.IsNullOrWhiteSpace(name)
                && region?.CuratedThemes is { Count: > 0 } curated)
                name = curated[0];
            if (string.IsNullOrWhiteSpace(name)) name = HsvPalette.Name;
            return ColorPalette.GetPaletteByName(name);
        }

        // ── Pixel helpers ────────────────────────────────────────────────────

        private static uint Pack(float r, float g, float b)
        {
            int ri = (int)(r + 0.5f); if (ri < 0) ri = 0; else if (ri > 255) ri = 255;
            int gi = (int)(g + 0.5f); if (gi < 0) gi = 0; else if (gi > 255) gi = 255;
            int bi = (int)(b + 0.5f); if (bi < 0) bi = 0; else if (bi > 255) bi = 255;
            return 0xFF000000u | ((uint)ri << 16) | ((uint)gi << 8) | (uint)bi;
        }

        private static uint[] CopyBuffer(uint[] src, int w, int h)
        {
            int n = w * h;
            if (src.Length == n) return (uint[])src.Clone();
            var dst = new uint[n];
            Array.Copy(src, dst, global::System.Math.Min(src.Length, n));
            return dst;
        }

        private static uint[] BlackFrame(int w, int h)
        {
            var f = new uint[w * h];
            for (int i = 0; i < f.Length; i++) f[i] = 0xFF000000u;
            return f;
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        private static string Tail(string log)
        {
            if (string.IsNullOrEmpty(log)) return "(no output)";
            return log.Length <= 2000 ? log : "…" + log[^2000..];
        }

        // Fully-resolved shot render state, cached per source-shot index.
        private sealed class ResolvedShot
        {
            public FractalType RenderType;
            public double CenterX, CenterXLo, CenterX2, CenterX3;
            public double CenterY, CenterYLo, CenterY2, CenterY3;
            public double Zoom;
            public int MaxIterations;
            public QualityPreset Quality = QualityPreset.Standard;
            public IColorMap Theme = null!;
            public FractalParameters BaseParams = null!;
            public AnimationData? Animation;
            public CameraTrack? Camera;
            public Rendering.Lighting.ToneMapOperator? ToneMap;
        }
    }
}
