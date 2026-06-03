# Capture Guide

Screenshots, posters, videos, and PNG sequences from Fracturing Fog.

---

## Table of Contents

1. [Screenshot (Image button)](#1-screenshot-image-button)
2. [Poster (multi-tile)](#2-poster-multi-tile)
3. [Video Zoom (single shot)](#3-video-zoom-single-shot)
4. [Video Slideshow (continuous loop)](#4-video-slideshow-continuous-loop)
5. [Recording Formats](#5-recording-formats)
6. [ffmpeg Discovery + Flags](#6-ffmpeg-discovery--flags)
7. [Resolution Reference](#7-resolution-reference)
8. [Batch CLI](#8-batch-cli)
9. [Watermark](#9-watermark)
10. [Tips](#10-tips)

---

## 1. Screenshot (Image button)

The Image button in the Floating Menu saves the current view as a single still.

| Behavior | Description |
|---|---|
| Format | PNG (default), TIFF, BMP — chosen by file extension |
| Resolution | Current panel resolution, OR full virtual desktop when Span is active |
| Post-FX | Live brightness / contrast / adaptive applied |
| Watermark | Embedded if the Watermark toggle is on. Contrast-aware text color. |
| Filename | Auto-generated: `FracturingFog_Theme_Region_x...y...z...i..._WxH.png` |

The file dialog defaults to your Pictures folder; switch to any path before confirming.

---

## 2. Poster (multi-tile)

The Poster button renders a tiled composite at print resolution. Each tile is calculated separately at full quality; tiles stitch into one large image.

### Dialog options

| Field | Range | Default |
|---|---|---|
| Width × Height | up to 32768 × 32768 | 7680 × 4320 (8K) |
| Tile size | 256 – 4096 | 1024 |
| Format | .png / .tif / .tiff / .bmp | .png |
| Output | file path or folder | (current dir) |
| Apply post-FX | on / off | on |
| Include watermark | on / off | on |

### Workflow

1. Pan + zoom to your subject.
2. Open Poster.
3. Set output dimensions (e.g., 11520 × 8640 = 12K, 15360 × 8640 = 16K wide).
4. Pick a tile size — 1024 is the default; smaller tiles parallelise better but increase seam risk; larger tiles use more memory.
5. Browse to output path.
6. Click Render. Progress bar shows tile-of-total + ETA.

Cancel any time — the partial frame buffer is dropped.

### Soft cap

A 64-megapixel ceiling applies by default to protect against accidental OOM (a 32k × 32k render holds ~10 GB resident). Override only if you have the RAM headroom.

### Remote poster

Use the Client dialog instead of the local Poster button:

1. Pick a saved server connection.
2. Mode = `image`.
3. Width × Height = your poster dimensions.
4. Pick fractal / region / theme / quality.
5. Output: a local file path on YOUR machine.

For huge posters, set Return mode = `saved-path`. The server keeps the file on its disk and replies with the path; read it later over file share. Inline (default) streams bytes in 1 MB chunks over TLS — fine for a few-MB poster, slow for multi-GB.

---

## 3. Video Zoom (single shot)

The Video button animates a smooth zoom from the current view to the active region's coordinates.

### Motion

| Phase | Duration | Behavior |
|---|---|---|
| Pan | First 5 % | Pan to target center at current zoom |
| Zoom | Last 95 % | Log-zoom interpolation, center fixed |

Both phases smoothstep-eased.

### Frame rate

**Calculation-bound, not wall-clock-bound.** The loop advances by elapsed wall-clock time so the total duration is honored even if individual frames take longer than 1/fps to render. Drop quality + iter cap if you need consistent fps.

### Live TAA tuning

While a video zoom is rendering, three extra sliders appear in the Floating Menu:

| Slider | Range | Purpose |
|---|---|---|
| TAA Alpha | 0 – 1 | Temporal blend strength between successive frames |
| Fade Start | 1e0 – 1e60 zoom | Where the deep-zoom artifact fade begins |
| Fade End | 1e0 – 1e60 zoom | Where the fade reaches full strength |

Use TAA Alpha around 0.3 – 0.6 for cinematic smoothing without ghost trails.

### Per-region iter override

Regions may carry a stored iteration target. During the video leg, MaxIterations is raised to at least that value so the deep target doesn't render as all-in-set black just because the quality preset's iter formula produced a smaller number.

---

## 4. Video Slideshow (continuous loop)

A continuous mode: zoom in → pause → zoom out → next region → repeat.

| Leg | Default | Configurable |
|---|---:|---|
| Zoom in duration | 30 s | Slideshow Settings |
| Pause at target | 7 s | Slideshow Settings |
| Zoom out duration | 30 s | Slideshow Settings |
| Inter-region gap | 0 s | Slideshow Settings |

Stops independently from the single-shot Video feature. Esc or the Slideshow button toggles off.

The Video button label flips to **Stop** while running.

---

## 5. Recording Formats

| Format | Container | Encoder | Needs ffmpeg? | Best for |
|---|---|---|---|---|
| None | — | — | No | Live playback only |
| MP4 (built-in) | .mp4 | Media Foundation H.264 | No | Quick exports, browser playback |
| Lossless H.264 | .mp4 | libx264 -qp 0, yuv444p, +faststart | Yes | Mathematical-lossless archive |
| Lossless FFV1 | .mkv | FFV1 v3 in Matroska | Yes | Archival / editing pipeline |
| H.264 HQ | .mp4 | libx264 -crf 18, yuv420p | Yes | Visually-lossless sharing |
| PNG sequence | folder | (sidecar) | No | Offline encoder pipelines |

MP4 (built-in) and PNG sequence can record **simultaneously** with any video format — useful for keeping a high-quality intermediate while shipping a small MP4 for preview.

### File-size order of magnitude

For a 60s 1080p 30fps clip:

| Format | Approximate size |
|---|---:|
| MP4 (built-in) | 30 – 80 MB |
| H.264 HQ | 80 – 200 MB |
| Lossless H.264 | 1 – 4 GB |
| Lossless FFV1 | 800 MB – 2.5 GB |
| PNG sequence | 5 – 15 GB |

A 4K 60s 60fps clip is ~16× a 1080p 30fps clip.

---

## 6. ffmpeg Discovery + Flags

ffmpeg.exe discovery order:

1. The app folder.
2. `<install>\Tools\`, `<install>\Resources\`.
3. PATH.

Missing ffmpeg with a lossless preset selected: exit code 3 (batch) or a clear error toast (UI).

### Workflow when ffmpeg is engaged

1. Render every frame to disk as `frame_NNNNNN.png` (image2 demuxer compatible — starts at 000001).
2. Invoke ffmpeg on the sequence with the preset's argument set. ffmpeg progress feeds a second progress meter.

### Preset arguments

Lossless H.264 (`h264`):

```
-i frame_%06d.png -c:v libx264 -qp 0 -preset veryslow -pix_fmt yuv444p
-movflags +faststart out.mp4
```

Lossless FFV1 (`ffv1`):

```
-i frame_%06d.png -c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slices 24
-slicecrc 1 -pix_fmt yuv422p out.mkv
```

H.264 HQ (`h264hq`):

```
-i frame_%06d.png -c:v libx264 -crf 18 -preset slow -pix_fmt yuv420p
-movflags +faststart out.mp4
```

### Keep / discard PNG sidecar

By default `--keep-frames` is on with `none` and off with lossless presets (since frames are intermediates). Override:

```
FracturingFog.exe --batch --mode video … --lossless ffv1 --keep-frames
FracturingFog.exe --batch --mode video … --lossless none  --no-keep-frames
```

In the UI, Slideshow Settings → Video tab has a `Keep frames` checkbox.

---

## 7. Resolution Reference

| Target | Pixels | Aspect | Notes |
|---|---:|---|---|
| 720p | 1280 × 720 | 16:9 | YouTube minimum |
| 1080p (FHD) | 1920 × 1080 | 16:9 | Standard HD |
| 2K | 2048 × 1080 | ~17:9 | DCI 2K |
| 1440p (QHD) | 2560 × 1440 | 16:9 | 1440p monitors |
| 4K UHD | 3840 × 2160 | 16:9 | Consumer 4K |
| 4K DCI | 4096 × 2160 | ~17:9 | Cinema 4K |
| 5K | 5120 × 2880 | 16:9 | iMac 5K |
| 8K UHD | 7680 × 4320 | 16:9 | 8K TV |
| 8K Cinema | 8192 × 4320 | ~17:9 | |
| 12K | 11520 × 6480 | 16:9 | High-end poster |
| 16K | 15360 × 8640 | 16:9 | Approaching the 32k cap |
| 32K | 32768 × 18432 | 16:9 | Server cap |

Video mode rounds width/height **down to the nearest even number** (codec constraint).

Poster mode honors odd dimensions but rejects anything above 32768 × 32768 or 64 MP total (hard server cap).

---

## 8. Batch CLI

Headless render with full UI parity.

### Image

```
FracturingFog.exe --batch --region ""Seahorse Valley"" --theme Fire ^
                  --width 3840 --height 2160 --out C:\out\seahorse.png
```

### Video

```
FracturingFog.exe --batch --mode video --region ""Mini Mandelbrot"" ^
                  --theme Plasma --seconds 30 --fps 30 ^
                  --out C:\out\zoom.mp4
```

### Lossless 60-second archival zoom

```
FracturingFog.exe --batch --mode video --region ""Seahorse Valley"" ^
                  --theme Fire --seconds 60 --fps 60 ^
                  --lossless ffv1 --keep-frames --out C:\out\
```

### Reverse zoom-out from a saved user region

```
FracturingFog.exe --batch --mode video --region ""MyDeepPick"" ^
                  --theme Inferno --seconds 20 --reverse ^
                  --lossless h264hq --out C:\out\dive_out.mp4
```

### Remote render

```
FracturingFog.exe --batch --remote ^
    --connection MyDeskstation ^
    --render Poster8K ^
    --out C:\out\poster.png
```

Prompts on stdin for the master password (no echo). Exit 0 on success.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | unhandled runtime error |
| 2 | bad command-line argument |
| 3 | --lossless selected but ffmpeg.exe not found |
| 4 | ffmpeg encode pass failed |

---

## 9. Watermark

Toggle from the toolbar **Watermark** button.

Watermark composition:

| Element | Default | Configurable |
|---|---|---|
| Region name | active region | yes (Slideshow Settings → Watermark) |
| Theme name | active theme | yes |
| Program label | ""Fracturing Fog v0.6.2"" | no |
| Position | bottom-right | yes (corner picker) |
| Opacity | 80 % | yes (slider) |
| Color | auto (contrast-aware) | manual override available |

The watermark is **CPU-composited into the BGRA buffer** before swap-chain upload, which is why it survives into screenshots, posters, and videos — the GPU never sees an unwatermarked frame when the toggle is on.

For non-watermarked exports, disable the toggle before triggering the capture.

---

## 10. Tips

**Match poster output dimensions to your printer's target DPI.** A 24"" × 16"" print at 300 DPI = 7200 × 4800. At 600 DPI = 14400 × 9600 — approaching the 16K poster ceiling.

**For long video zooms, pre-test with low-quality settings.** Drop Quality to Draft + Iter to 256, set --seconds short, render to PNG-sequence. Confirms the motion + framing before committing to a multi-hour Ultra render.

**ffmpeg's encode pass is single-threaded for libx264 -preset veryslow.** Lossless H.264 takes much longer than the render itself for many regions. Use FFV1 instead — it's also lossless, files are smaller, encode is fast.

**Browser-friendly MP4 requires yuv420p.** The H.264 HQ preset uses yuv420p; Lossless H.264 uses yuv444p (NOT browser-friendly — re-encode to ship to YouTube).

**Poster tile size affects memory but not seam quality.** Tiles are calculated with consistent coordinate math so a 256-tile poster looks identical to a 4096-tile poster (just renders slower per tile and faster overall on multi-core).

**Lock iterations before capture.** A capture mid-iteration-promotion can produce subtly different shading frame-to-frame. Tick Lock Iterations + pin a high value before triggering Poster / Video.

**Frame rate vs duration trade.** Doubling fps = doubling frame count = doubling render + encode time, but only 4 % better motion smoothness at the human-perception level. 30 fps is usually fine; reserve 60 fps for high-motion zoom-outs.

**Span mode for wallpaper-resolution screenshots.** Click Span → click Image. The output covers the entire virtual desktop, which is usually 2× – 4× a single monitor.

**Adaptive sweep on video.** Start the Adaptive Sweep just before triggering Video — the sweep animates from 0 → 100 across the recording, giving a slow-build dramatic reveal.

---

*Capture Guide · Fracturing Fog · © 2026*
