# Stereo output and autostereograms

Epic #1014 (slices #1008–#1013, follow-ups #1016, #1017, #1020, #1023). SBS background: #106–#111.

Every stereo mode carries depth by geometry or by pattern repetition, never by colour. That suits the red/green colourblind owner; anaglyph was rejected in #106.

## Modes (Lighting & FX → Reflection / Edge / Stereo / DoF → Stereo mode)

| Mode | What it makes | Size | Works on |
|---|---|---|---|
| Off | mono | W×H | all |
| Fake | one render plus a depth-parallax warp; the second eye is synthesised | 2W×H (Full) / W×H (Half) | 3D fractals, Relief 3D |
| True | two real eye renders (camera ±IPD/2) | 2W×H / W×H | 3D fractals (Relief maps True to the warp) |
| Autostereogram | one "Magic Eye" image whose repeating pattern encodes the depth | W×H | 3D fractals, Relief 3D |

Flat 2D fractals have no depth and no second camera, so stereo has no effect on them. Flat-2D autostereograms are #1020.

### Side-by-side settings

- **Eye sep:** world units.
- **Convergence:** 0 means parallel eyes, so the whole scene floats in front of the screen. Positive values push the subject back to the screen plane. #1008 fixed the sign.
- **Max disparity:** a comfort cap for the warp.
- **FOV:** the field of view the warp assumes.
- **Layout:** Full or Half.
- **Swap eyes:** cross-view layout (#1017).

The live window letterboxes Full-SBS so each eye keeps its proportions (#1016). Saved files are unpadded.

### Autostereogram settings

- **Pattern:** RandomDots (black and white, easiest to fuse), ThemeDots (colours from the render), or FractalTexture (a strip of the render, cut from where the fractal is).
- **Eye separation:** a fraction of the width; the background repeats every half of it.
- **Depth amount:** depth of field μ.
- **Depth smoothing / Depth levels:** rough fractal surfaces fuse poorly, so defaults are 3 px and 6 levels. Some viewers fuse faster with both at 0.
- **Cross-eyed:** inverts the depth for cross-eyed viewing.
- **Guide dots:** two dots near the top; fuse them into three.
- **Dot seed.**
- **Video smoothing:** see Animation below.

## Pipeline

- **3D depth:** the raymarchers publish their per-pixel ray distance on `IDepthAovSource` when `ScreenSpacePost.WantsDepthOutput` asks for it (#1009). That capture forces the CPU trace, because the GPU kernels have no depth pass. Relief uses its depth G-buffer.
- **Shared post step:** `ScreenSpacePost.ApplyDepthStereo` produces either the warp or the autostereogram. It runs last, on the finished display buffer, in the live host, Relief, the poster and batch.
- **True stereo:** `StereoRender.RenderTrueStereo` renders the two eyes. The per-eye offset lives on `IStereoEyeCamera`, not in the shared params (#1008).
- **Autostereogram engine:** `Autostereogram` uses Thimbleby / Inglis / Witten (1994) linking with hidden-surface removal. Each linked chain takes its colour from its member nearest the image centre (#1013).

## Batch / CLI (#1012)

- **Mode:** `--stereo off|fake|true|autostereogram`.
- **Side-by-side:** `--stereo-eye-sep`, `-convergence`, `-max-disparity`, `-fov`, `-layout full|half`, `-swap-eyes`.
- **Autostereogram:** `--autostereo-pattern dots|theme|texture`, `-eye-sep`, `-depth`, `-smoothing`, `-levels`, `-cross-eyed`, `-no-guide-dots`, `-seed`, `-temporal` (video only).

Stills and zoom videos support every mode. The video writer is sized to the stereo frame, and Ken-Burns with stereo holds the view. Slideshow and scene batch modes stay mono and print a note saying so. The Command builder's "Seed from live view" emits all of these flags.

## Animation (#1013)

A stateless autostereogram flickers in motion for three reasons:

1. The texture / palette is re-cut from every frame.
2. Per-frame depth noise shimmers.
3. The paper's right-to-left colouring repaints the whole row to the left of any depth change.

The fixes:

- **`AutostereoSequence`:** while a video or slideshow runs (live `IsRunning`, or batch video), it holds the pattern source from the first frame of each leg. It blends the depth with an exponential moving average (`StereoAutoTemporal`, default 0.5), applied before the depth levels. Outside a sequence each frame stands alone, so a settled view always shows its own texture.
- **Centre-anchored colouring:** a depth change repaints only the far side of its chains from the centre, not everything to its left.

## Limits

- **Few depth planes:** an autostereogram shows only a few distinguishable depth planes. Fine filaments do not read; bulb bodies and relief terrain do.
- **Animated autostereograms still shimmer** where the surface moves. Linked pixels must be re-linked as depth changes, so no encoding is perfectly still. Random dots re-roll only where the depth changes, and a held texture keeps the rest steady. Raising video smoothing trades shimmer for a little lag.
- **Fake stereo on 3D, and autostereograms, render on the CPU.** Thin-lens DoF provides no depth, so the frame stays mono; the dialog shows a yellow note.
- **Free-viewing:** wall-eyed works only when the pair is at most about 6 cm apart on screen. Cross-eyed works at any size, via Swap eyes for SBS or Cross-eyed for autostereograms. VR players (Skybox / DeoVR) need the matching SBS / Half-SBS projection.
