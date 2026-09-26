# Capture Guide

Screenshots, posters, videos, and PNG sequences from Fracturing Fog.

> Companion pages: [User Index](_Index.md) · [Avalonia User Guide](Avalonia-UserGuide.md) · [Regions Guide](Regions-Guide.md)

![PLACEHOLDER — Floating Menu showing the Image / Poster / Video / Slideshow row](../Images/_placeholders/placeholder.svg)

---

## A friendly tour

There are four ways to save a picture out of Fracturing Fog. Pick the one that matches what you
want to *do* with it, and the rest follows:

| You want to…                                  | Use this button     | Format you get          |
|------------------------------------------------|---------------------|-------------------------|
| Post a screenshot to social media              | **Image**           | One PNG, ~1-3 MB        |
| Print a wall-sized poster, or send to a printer | **Poster**         | One huge TIFF/PNG       |
| Record whatever is on screen right now         | **● Rec** / **F9**  | MP4 / H.265 / WebM / MKV / GIF / PNG — picked when you stop |
| Share a smooth zoom-in video                   | **Video** (tick *Record this run*) | Same choice as ● Rec |
| Loop endlessly between favourite views         | **Video Slideshow** (tick *Record this run*) | Same choice as ● Rec |

> [!TIP]
> Before you capture, decide what should be in the picture. Should the **grid** be on? Should the
> **watermark** be on? Should the **post-FX** sliders (brightness / contrast / adaptive) be applied?
> All three flow into the saved file exactly as you see them on screen.

### Worked example — "I just want a 4K screenshot of Seahorse Valley"

1. From the **Region** combo (toolbar), pick **Seahorse Valley**. The view jumps to the saved spot.
2. Optional: switch the **Theme** combo to *Fire* for a warmer look.
3. Optional: hide the **Grid** (toolbar toggle) so the image is clean.
4. Press **`M`** to open the Floating Menu, then click **Image**.
5. Pick a filename ending in `.png`, hit Save. You get a 1920×1080 PNG by default; if your window
   is bigger, you get the window's resolution.

> [!NOTE]
> For a true 3840×2160 (4K) capture independent of your monitor, use **Poster** instead and dial
> in `Width=3840`, `Height=2160`, `Tile=1024`. The Poster path runs the calculator afresh per tile
> at full quality — what you save is exactly what the math produced, not what the screen sampled.

### Worked example — "I want a 30-second zoom-in video into a deep region"

1. Pan, zoom, or apply a region until you are on the *starting* frame you want to see.
2. Switch on **Lock Iterations** in the Region Navigation panel if you have already tuned the iter
   count for the deep target. Otherwise the video may starve detail on the inner frames.
3. Open Floating Menu → **Video**.
4. Pick **Region = Mini Mandelbrot** (or whatever deep spot you want to dive into).
5. Duration `30` seconds, start zoom `0.5`, and tick **● Record this run**.
6. Click **Start**. When the zoom ends the **Save Recording** prompt asks for the format (MP4 for a
   quick share, *MKV — FFV1* for archival), quality, frame rate and size.

> [!WARNING]
> H.265, WebM and lossless MKV need `ffmpeg.exe` (install it from **FFmpeg Setup**). The app looks
> for it in the install folder, then `Tools\`, then your `PATH`. Without it the prompt offers MP4,
> animated GIF and PNG sequence only.

> [!NOTE]
> **Audio in exports (#435).** When Audio settings are **Enabled** with **Source = File**, the
> chosen audio file is muxed into the exported video after encoding — Video Zoom, the image
> Slideshow's *Convert*, and the lossless *encode* paths all attach it (AAC, trimmed to the shorter
> of video/audio). Because the audio-reactive analysis and the mux both start from the file at
> `t = 0` at a constant frame rate, the result is frame-accurate. Streamed sources (System Loopback /
> Microphone) are not muxed yet — capturing a live stream to a file for later render is tracked
> separately (#773). The status line shows "(with audio)" when a track was attached; a mux failure
> is non-fatal and keeps the silent video.

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

The Image button in the Control Center's Capture section saves the current view as a single still.

| Behavior | Description |
|---|---|
| Format | PNG (default), JPEG, BMP, TIFF, **GIF**, **ICO**, **EXR** — chosen by file extension |
| Resolution | Current panel resolution, OR full virtual desktop when Span is active |
| Post-FX | Live brightness / contrast / adaptive applied |
| Watermark | Embedded if the Watermark toggle is on. Contrast-aware text color. |
| Filename | Auto-generated: `FracturingFog_Theme_Region_x...y...z...i..._WxH.png` |

The file dialog defaults to your Pictures folder; switch to any path before confirming.

> [!NOTE]
> **Name the file `.exr`** to save an OpenEXR instead of a PNG. EXR is a
> floating-point, scene-linear format that Blender, Nuke, DaVinci Resolve and
> `oiiotool` read — the right choice when you plan to grade or composite the
> render, not just view it. The RGB is un-gamma'd to linear on the way out and
> alpha is preserved. (Watermarks are skipped on EXR — they are a display-space
> overlay.) Today the source is the same 8-bit render promoted to float;
> true high-dynamic-range AOV layers land with the render-pass work (roadmap S1).

> [!NOTE]
> **Name the file `.ico`** to save a Windows icon. The frame is center-cropped
> to a square and written as a multi-resolution set (16 / 32 / 48 / 256,
> capped to the cropped side) so the OS can pick the size it needs — handy for
> turning a favourite view into an app or shortcut icon. Watermarks are skipped
> (an icon-sized overlay is meaningless).

> [!NOTE]
> **Name the file `.gif`** to save a GIF. The frame is quantized to a 256-colour
> palette (median cut) with 1-bit transparency, so smooth fractal gradients will
> band — PNG stays the better choice for stills. GIF is handy for small,
> few-colour images and for pasting where only GIF is accepted. This is a single
> still; for an **animated** GIF record with **● Rec** / **F9** and pick *Animated GIF*
> in the Save Recording prompt ([§3](#3-video-zoom-single-shot)).

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

### Start point

By default a forward zoom begins at the classic full view. The **Start from**
picker changes where it begins:

| Choice | Zoom begins at |
|---|---|
| Classic full view (default) | The whole-set overview, as before |
| Current view | Wherever you are right now (centre + zoom) |
| *(a saved region)* | That region's coordinates + zoom |

A start **region** must be the **same fractal type** as the target (its X/Y are
meaningless otherwise) — a mismatch falls back to the classic view with a status
note. The picker is ignored for a **reverse** zoom (which already starts at the
target).

> [!NOTE]
> When the start and target are both **deep** and **far apart** on the plane, a
> straight pan would fly sideways at high zoom. The zoom automatically inserts a
> **dolly** — zoom out to a level where both fit, pan across, then zoom back in —
> so the motion stays watchable. Near or shallow starts pan directly, unchanged.

### Recording outputs

All interactive recording goes through one recorder and one **Save Recording**
prompt (#936):

- **Instant record** — press **F9**, click the toolbar **● Rec** button, or pick
  **Record** from the render-window right-click menu. Everything the render
  window shows is captured as displayed: themes and theme fades, animations,
  slideshow legs, lighting / FX, watermark, HUD. Press again to stop.
- **Record this run** — tick it in the Video Zoom dialog and the zoom (or the
  video slideshow) is recorded from start to finish; the recording stops by
  itself when the run ends.

When recording stops, the **Save Recording** prompt offers:

| Choice | Options |
|---|---|
| Format | MP4 — H.264 · MP4 — H.265 · WebM — VP9 · MKV — FFV1 (lossless) · Animated GIF · PNG sequence |
| Quality | Lossless · High · Medium · Low (ignored by MKV / PNG) |
| Frame rate | 15 – 60 fps (resampled from the capture) |
| Size | 100 / 75 / 50 / 25 % |

**Discard** asks before throwing a take away, and a failed export re-opens the
prompt so you can try another format.

### Quick Record settings

**Control Center ▸ Capture ▸ Quick Record** holds the recorder's defaults:
capture rate, default format / quality / frame rate / size, and **Ask how to
save when recording stops**. Turn that off to skip the prompt entirely:
recordings then save straight to the chosen folder (default
`Videos\FracturingFog`) with the defaults. The prompt remembers your last
choices here.

> [!NOTE]
> The recorder samples the window at the capture rate, but only saves a new
> frame when the picture actually changed — a still view costs almost nothing,
> and playback follows real time. The low-resolution previews shown *while*
> dragging are not recorded, only the settled frames. **Animated GIF** and MP4
> work without ffmpeg (built-in encoders); H.265 / WebM / MKV need it.

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

### 4a. Record an Image Slideshow to Video

The image slideshow (Slideshow Settings → Type = Image) can now be captured.
Tick **Record Slideshow** in the Slideshow Settings dialog and pick an encode
preset. The engine streams every cross-fade interpolation frame into a PNG
sequence under your temp folder while the slideshow runs.

When you Stop the slideshow you get a three-button prompt:

| Button | Action |
|---|---|
| **Convert** | Runs ffmpeg with the chosen preset and saves a video file. Temp PNG folder is deleted on success. |
| **Save Frames** | Pick a destination folder; the PNG sequence is moved there for offline post-processing. |
| **Cancel** | Discards the temp PNG folder. |

Encode presets:

| Preset | Codec / container | Notes |
|---|---|---|
| HighQualityH264Mp4 (default) | libx264 -crf 18, yuv420p | Visually lossless, small, broad-compat |
| LosslessH264Mp4 | libx264 -qp 0, yuv444p | Mathematically lossless |
| Ffv1Mkv | FFV1 v3 in Matroska | Archival / editing pipeline |

The Convert path needs `ffmpeg.exe` — install it via the floating menu's
**FFmpeg Setup** dialog first. Save Frames works without ffmpeg.

---

## 5. Recording Formats

Interactive recordings pick their format in the Save Recording prompt (§3).
The table below covers the **batch** (`--batch --mode video`) and image-slideshow
*Convert* encode presets.

| Format | Container | Encoder | Needs ffmpeg? | Best for |
|---|---|---|---|---|
| None | — | — | No | Live playback only |
| MP4 (built-in) | .mp4 | Media Foundation H.264 | No | Quick exports, browser playback |
| Lossless H.264 | .mp4 | libx264 -qp 0, yuv444p, +faststart | Yes | Mathematical-lossless archive |
| Lossless FFV1 | .mkv | FFV1 v3 in Matroska | Yes | Archival / editing pipeline |
| H.264 HQ | .mp4 | libx264 -crf 18, yuv420p | Yes | Visually-lossless sharing |
| PNG sequence | folder | (sidecar) | No | Offline encoder pipelines |

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

### Any fractal type (#947)

Batch video works for **every** fractal type. By default the motion follows the
family, the same way the interactive video slideshow moves it:

| Family | Default motion (`--video-motion auto`) |
|---|---|
| 2D escape-time (Mandelbrot, Julia, Newton, Apollonian, Generated, …) | Plane zoom from `--start-zoom` to the target. Julia / Phoenix / Glynn also drift their constant gently (`--no-drift` to keep it fixed). |
| Raymarched 3D (Mandelbulb, Mandelbox, KIFS, Quaternion, Kleinian, …) | Camera dolly from 6× wider to the authored framing. Add `--orbit DEG` to swing the camera around. |
| Logistic, AcidWarp | Param sweep (Logistic pans its r-window, AcidWarp morphs its flow). |
| Other non-spatial (Flame, Plasma, DLA, Buddhabrot, IFS, …) | Rendered once, then a slow Ken-Burns pan + zoom. |

Force a motion with `--video-motion zoom|hold|kenburns|sweep`, and make the
random parts reproducible with `--video-seed N`. A `--region` also brings its
saved per-type settings (Julia constant, 3D camera, seeds, lighting) into every
frame.

```
FracturingFog.exe --batch --mode video --fractal Mandelbulb --x 0 --y 0 --zoom 1 ^
                  --seconds 12 --orbit 120 --out C:\out\bulb.mp4
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

### Image slideshow → MP4 (headless)

Drives the same image-slideshow preset the interactive Slideshow Settings
dialog edits, then encodes the captured PNG sequence with ffmpeg.

```
FracturingFog.exe --batch --slideshow ""Default"" ^
                  --seconds 90 --fps 30 ^
                  --width 1920 --height 1080 ^
                  --encode h264hq ^
                  --out C:\out\slideshow.mp4
```

Notes:
- `--slideshow NAME` names a saved preset in `%APPDATA%\FracturingFog\slideshow-configs.json`. Omit to use the active preset.
- `--encode` accepts `h264hq` (default), `h264` (lossless), or `ffv1`.
- v1 headless slideshow renders Mandelbrot regions only; non-Mandelbrot regions in the preset's filter set are skipped with a warning.
- `--keep-frames` keeps the PNG sequence at its temp path after ffmpeg succeeds. Default behaviour deletes it.

### Watermark (on by default)

Every batch render — image, video, and image/video slideshow — paints the
watermark (region + theme name over the program label) into each frame by
default. This matches what the interactive **Save** button does, so a poster you
render headless looks the same as one you export from the app.

To render **without** the watermark, pass `--watermark` (or its clearer alias
`--no-watermark`). The flag is a switch that turns the watermark *off*:

```
FracturingFog.exe --batch --region ""Seahorse Valley"" --theme Fire ^
                  --width 3840 --height 2160 ^
                  --no-watermark --out C:\out\seahorse-clean.png
```

> [!NOTE]
> The flag reads backwards on purpose. Because the watermark is now the default,
> the only thing left for a flag to do is remove it — so both `--watermark` and
> `--no-watermark` mean "suppress the watermark". There is no flag to *add* one;
> it is already there.

Scene mode (`--mode scene`) is the exception: it carries its own watermark
setting inside the saved scene, so this flag does not affect it.

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

Headless renders follow the same rule but the other way round: the watermark is
**on by default** for every `--batch` mode, and you pass `--no-watermark` to drop
it. See [Watermark (on by default)](#watermark-on-by-default) under the Batch CLI
section.

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
