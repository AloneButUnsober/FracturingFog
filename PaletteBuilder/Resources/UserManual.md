# Palette Builder — User Documentation

Generate color palettes from images. Standalone Avalonia desktop app.
Cross-platform (Windows/Linux/macOS) — image decode + PDF export run via SkiaSharp/QuestPDF.

---

## 1. Getting Started

### Install / Run

```
dotnet run --project PaletteBuilder
```

Or build once and run the produced exe at
PaletteBuilder\bin\Debug\net10.0-windows\PaletteBuilder.exe.

### First Use

1. Drag any image onto the window, or File → Open Image(s).
2. Click Extract. Default method (K-Means) runs.
3. A swatch strip + gradient strip appears in the results pane.
4. Click Export… to save in any of 12 formats.

---

## 2. Main Window Layout

```
┌────────────────────────────────────────────────────────┐
│  File   Edit   Presets   Tools   Help                  │  Menu bar
├────────────────────────────────────────────────────────┤
│  ┌──────────┐  ┌─────────────────────────┐             │
│  │ Preview  │  │  Method / ColorCount /  │             │
│  │ +        │  │  Space / Downsample /   │  Options    │
│  │ Histogr. │  │  Sort / Dedup ΔE / …    │             │
│  └──────────┘  │  Expanders              │             │
│                │  [Extract] [Compare All]│             │
├────────────────┴─────────────────────────┤             │
│  Results panel: swatch + gradient rows                 │
│  Per row: [Edit] stop editor when toggled              │
├────────────────────────────────────────────────────────┤
│  Temp ─●─  Tint ─●─    Gradient: [Lab▾]                │
│       [Inspect…] Format:[PDF▾] [Export…] [Close]       │
├────────────────────────────────────────────────────────┤
│  Status: K-Means: 142 ms → 8 swatches                  │  Status bar
└────────────────────────────────────────────────────────┘
```

---

## 3. Loading Images

| Method        | How                                                        |
|---------------|------------------------------------------------------------|
| Drag-drop     | Drop file(s) anywhere on window                            |
| Drag folder   | Drop a folder → enumerates .png/.jpg/.jpeg/.bmp/.gif/.tif  |
| File menu     | File → Open Image(s)… (multi-select supported)             |
| Folder picker | File → Open Folder…                                        |
| Recent        | File → Recent — last 12 loaded paths                       |
| Hex paste     | Edit → Paste Hex List… — skip extraction entirely          |

Multi-image batch: dropping multiple files or a folder switches to batch
mode. Saliency / filters / ROI apply per-image; pixels from every source
concatenate into one synthetic buffer the extractor sees. Spatial-K-Means
falls back to colour-only in batch mode.

EXIF orientation is honoured automatically (tag 0x0112). No need to
pre-rotate.

---

## 4. Extraction Methods

11 algorithms registered. Pick via Method ComboBox.

| Method              | Use For              | Strengths                            | Weaknesses                          |
|---------------------|----------------------|--------------------------------------|-------------------------------------|
| K-Means             | General photography  | Sharp clusters, good defaults        | Slow on huge images                 |
| Median Cut          | Pixel art, gradients | Fast, predictable                    | Cleaves high-density regions        |
| Octree              | Limited palettes     | Fast, low memory                     | Limited to ≤256 colours             |
| Histogram           | Posterised art       | Catches dominant pixels exactly      | Misses smooth gradients             |
| Wu (variance cut)   | High-detail photos   | Sharper than median cut              | Slower than median cut              |
| Mini-Batch K-Means  | Huge images          | 5–10× faster than vanilla k-means    | Slightly noisier centroids          |
| Material Palette    | UI design            | Vibrant / Muted / Dark / Light slots | Output capped at 6–7                |
| Mean Shift          | Unknown color count  | Auto-finds modes; tune Bandwidth     | Slow, needs bandwidth tuning        |
| DBSCAN              | Noise-heavy images   | Drops outliers as noise              | Needs ε + MinPts tuning             |
| GMM (EM)            | Overlapping regions  | Smoother centroids via soft assign   | Heavier math; slower                |
| Spatial K-Means     | Mixed-subject images | Same colour, different regions split | Ignored in batch mode               |

---

## 5. Options Reference

### Method-agnostic

| Option                                       | Range                     | Default | What it does                                            |
|----------------------------------------------|---------------------------|---------|---------------------------------------------------------|
| Method                                       | enum                      | K-Means | Which extractor runs                                    |
| Color count                                  | 4–32                      | 8       | Target swatches                                         |
| Color space                                  | RGB/Lab/HSL/OkLab         | Lab     | Feature space for clustering distance                   |
| Downsample max                               | 64–1024                   | 256     | Longest-dim cap before clustering                       |
| Sort                                         | NN Chain/Hue/Lum/ClusterSz| NN Chain| Order stops within result                               |
| Dedup ΔE                                     | 0–30                      | 2.0     | Merge near-duplicate stops below this distance          |
| Dedup metric                                 | ΔE76 / ΔE2000             | ΔE76    | Which formula. ΔE2000 = more accurate, slower           |
| Weight stop positions by cluster size        | bool                      | off     | Heavier clusters take wider slice of gradient           |
| Exclude near-black                           | bool                      | off     | Drop pixels w/ R,G,B all ≤ 24                           |
| Exclude near-white                           | bool                      | off     | Drop pixels w/ R,G,B all ≥ 240                          |
| Gamma-correct                                | bool                      | off     | Linearise sRGB before RGB-space clustering              |

### Color space comparison

- RGB: Cheapest, blunt. Equal R/G/B distance — doesn't match perception.
- Lab: CIELAB. Perceptually weighted. Solid default for photographs.
- HSL: Projects hue to a 2D circle. Picks hue-themed palettes.
- OkLab: Björn Ottosson's modern perceptual space. Better than Lab for
  high-chroma. Smoothest blends.

### Sort comparison

- Nearest-Neighbor Chain: Repeatedly append closest unvisited colour.
  Smoothest gradients.
- Hue: Rainbow order. Pushes desaturated colours to the start.
- Luminance: Dark → light. Good for tonal ramps.
- Cluster Size: Most pixels first. Reveals dominance.

### Algorithm-specific (Expander "Algorithm-specific tuning")

| Option         | Used by         | Range            | Default |
|----------------|-----------------|------------------|---------|
| Bandwidth      | Mean Shift      | 1–100 Lab units  | 25      |
| ε (epsilon)    | DBSCAN          | 0.5–100 Lab units| 8       |
| MinPts         | DBSCAN          | 1–5000           | 20      |
| Spatial weight | Spatial K-Means | 0–1              | 0.5     |

- Bandwidth: Smaller = more modes, finer palette. Larger = fewer, broader.
- ε: DBSCAN neighbourhood radius. Smaller = tighter clusters.
- MinPts: Pixel-weight sum within ε to seed cluster. Higher = aggressive noise reject.
- Spatial weight: 0 = colour only. 1 = colour + position equal.

---

## 6. Preprocessing Filters

Expander "Preprocessing filters" — applied before clustering.

| Filter                       | What                                                       |
|------------------------------|------------------------------------------------------------|
| Exclude transparent pixels   | Skip pixels w/ alpha < 16 (PNGs, etc.)                     |
| Min/Max saturation           | Drop pixels outside HSL S band. min=0.2 kills greys        |
| Min/Max lightness            | Drop pixels outside HSL L band                             |
| Use saliency                 | Spectral-residual saliency drops background pixels         |
| Saliency threshold           | 0–1, default 0.3. Higher = stricter                        |
| ROI X/Y/W/H                  | Crop rect in normalised [0,1]. All-zero = full image       |

### Saliency notes

Computes per-image saliency map via 2-D FFT spectral-residual algorithm.
~50ms per image, cached per option-tuple.

- Works best when subject is high-frequency vs background.
- Smooth subject on smooth background scores poorly — use ROI or sat/lum band.
- Cache invalidates when image or any preprocessing knob changes.

### ROI

- Type values directly into X/Y/W/H NumericUpDowns, or Tools → Clear ROI.
- Values clamp into source. X=0.25 Y=0.0 W=0.5 H=1.0 = middle vertical strip.
- ROI applied per-source in batch mode (same rect to every image).

---

## 7. Running Extraction

### Single Extract

Press Extract (or Ctrl+E). Runs current method with current options.
Replaces results.

### Compare All

Press Compare All (or Ctrl+Shift+E). Runs every registered extractor (11)
against the same source + options. Each result becomes its own row with a
RadioButton — pick one to feed Export/Inspector.

### Auto-extract

Tools → Auto-extract on option change (or inline "Auto" checkbox). When
on, any option tweak re-runs Extract after 250ms quiet period. Debounced
so a slider drag = 1 run, not 80.

Temperature/Tint/Gradient-interp changes are excluded from auto-extract
(display-only).

---

## 8. Results Panel

Each row shows:
- Method name + swatch count
- Edit toggle button → reveals stop editor
- Swatch strip (per-cluster colours)
- Gradient strip (built from stops, interpolated in chosen space)

Compare All mode: each row has a RadioButton (group selection). Selected
row drives Export/Inspector.

Single Extract: only the one row, auto-selected.

---

## 9. Stop Editor

Click row's Edit toggle. Inline panel appears below the gradient strip.

Per stop:
| Control            | What                                          |
|--------------------|-----------------------------------------------|
| Colour preview     | Live colour box                               |
| Hex label          | #RRGGBB (auto-updates when R/G/B edited)      |
| pos NumericUpDown  | Position 0–1, step 0.01                       |
| R / G / B NUDs     | Channel values 0–255                          |
| 🔒 lock toggle     | Visual marker (advisory only)                 |
| ↑                  | Move stop up                                  |
| ↓                  | Move stop down                                |
| ✕                  | Remove stop                                   |

Normalize positions button — redistributes evenly across [0,1].

Export honours edits: when Edit toggle is on, exports use the edited stops
(EffectiveStops). Toggle off to revert to original extraction output.

Lock limitation: lock is currently a visual flag. Re-extracting clobbers
all stops. Workflow: extract → lock → edit values → export.

---

## 10. Adjustments

### Temperature / Tint sliders (bottom bar)

| Slider | Range    | What                                            |
|--------|----------|-------------------------------------------------|
| Temp   | -1 to +1 | Blue↔Yellow shift. Positive warms.              |
| Tint   | -1 to +1 | Green↔Magenta shift.                            |

Click ↺ next to either to reset to zero.

Applied to selected palette before export. Magnitude max = ±64 byte shift
on most-affected channel. Display refreshes immediately; doesn't
re-cluster.

### Gradient interpolation space

Gradient ComboBox: sRGB / Lab / OkLab.

- sRGB: Avalonia native LinearGradientBrush. Fastest. Can show "muddy"
  mid-tones between distant hues.
- Lab / OkLab: Per-pixel column fill in perceptual space. Smoother
  visually. ~10ms slower per repaint.

Affects both preview gradient strips and PDF gradient strips.

---

## 11. Inspector

Press Inspect… (enabled when palette selected). Modal dialog w/ 3 tabs.

### Names tab

Per row: colour box + #HEX + RGB(r, g, b) + nearest CSS/X11 colour name
(140+ names matched via Lab nearest-neighbor).

Right-click any row → context menu:
- Copy HEX → #aabbcc
- Copy RGB → rgb(170, 187, 204)
- Copy HSL → hsl(210, 25%, 73%)
- Copy name → LightSteelBlue

### WCAG Contrast tab

N×N matrix of pairwise contrast ratios.

| Cell colour  | Pass tier        | Ratio       |
|--------------|------------------|-------------|
| Green        | AAA              | ≥ 7.0:1     |
| Dark green   | AA Normal        | ≥ 4.5:1     |
| Olive        | AA Large text    | ≥ 3.0:1     |
| Red          | Fail             | < 3.0:1     |

Numbers in each cell: x.xx:1 + badge.

### Color Blindness tab

4 horizontal swatch strips:
- Original (sRGB)
- Protanopia (no L cones)
- Deuteranopia (no M cones)
- Tritanopia (no S cones)

Machado et al. 2009 transformation matrices (severity = 1.0 = full
dichromacy). Operates in linear-sRGB.

---

## 12. Export Formats

Format dropdown (12 entries). Click Export… → save dialog → file written.

| Format                  | Ext             | Notes                                    |
|-------------------------|-----------------|------------------------------------------|
| PDF document            | .pdf            | Triggers settings dialog first. See §13  |
| PNG sheet               | .png            | Single-column strip 480px wide           |
| JSON                    | .json           | { name, source, method, swatches, stops }|
| CSS variables           | .css            | :root { --palette-01: #hex; ... }        |
| SCSS map                | .scss           | $palette: ("01": #hex, ...);             |
| Tailwind colors snippet | .js             | Paste into theme.extend.colors           |
| GIMP palette            | .gpl            | R G B  Name lines                        |
| Sketch palette          | .sketchpalette  | JSON float-channel format                |
| Inkscape SVG swatches   | .svg            | <rect fill="#hex"> per swatch            |
| Adobe swatch            | .ase            | Big-endian binary ASEF v1.0              |
| Procreate               | .swatches       | Zip w/ Swatches.json (HSV)               |
| Krita palette           | .kpl            | Zip w/ colorset.xml + mimetype           |

Suggested filename: <source-basename>-palette.<ext> when an image is
loaded, else palette.<ext>.

Temperature/Tint applied to all formats — exports the swatches you see,
not raw extraction.

---

## 13. PDF Export Settings

Triggered when format = PDF. Modal dialog with:

| Setting                    | Options                            | Default  |
|----------------------------|------------------------------------|----------|
| Page size                  | Letter/Legal/Tabloid/A4/A3         | Letter   |
| Orientation                | Portrait/Landscape                 | Portrait |
| Columns                    | 1–6                                | 2        |
| Cover page                 | bool                               | off      |
| Source thumbnail           | bool                               | off      |
| Gradient strip below grid  | bool                               | off      |
| Per-swatch metadata        | bool                               | off      |
| Color-blindness rows       | bool                               | off      |
| Comparison page            | bool                               | off      |

### What each adds

- Cover page — Title + source preview + Method + settings dump.
- Source thumbnail — Aspect-fit preview centred above swatch grid page 1.
- Gradient strip — Per-pixel gradient render at bottom of every swatch page.
- Per-swatch metadata — Below each tile: HSL/Lab/CMYK/contrast ratios.
- CVD rows — Proto/Deut/Trito strips under each swatch.
- Comparison page — Runs ExtractAll. One row per method. Slower.

### PDF metadata (always set)

- Title = palette name (defaults to source filename)
- Creator = "Palette Builder"
- Author = current Windows username
- Subject = "Method: <method name>"
- Keywords = palette name + method

---

## 14. Presets

Save current option state for re-use.

| Action              | Where                                      |
|---------------------|--------------------------------------------|
| Save current as…    | Presets menu → prompt for name             |
| Load                | Presets → Load → pick name                 |
| Delete              | Presets → Delete → pick name               |

Persisted to %APPDATA%\PaletteBuilder\presets\<Name>.palettebuilder.json.

Saved fields: all extraction options + filters + algorithm-specific +
Saliency. ROI is excluded (per-image; nonsense to save). Temperature/Tint
also excluded (display-only).

Filename sanitised — slashes/reserved chars become _.

---

## 15. Recent Files

%APPDATA%\PaletteBuilder\recent.json stores last 12 image paths
(case-insensitive dedup). Accessed via File → Recent. Updated
automatically on each successful load.

---

## 16. Undo / Redo

Edit → Undo (Ctrl+Z) / Edit → Redo (Ctrl+Y). Snapshots every option change
(throttled 400ms — drag = 1 snapshot).

- Stack capped at 50 snapshots.
- Redo cleared on any new change.
- Captures preset DTO state. Stop edits + Temp/Tint not in stack.

---

## 17. Hex Paste Seed

Edit → Paste Hex List… prompts for hex string, seeded from clipboard if
available.

Accepted formats:
```
#aabbcc, #112233, #ddeeff
aabbcc 112233 ddeeff
#abc          (short → expanded to #aabbcc)
```

Separators: comma, semicolon, space, tab, newline.

Bypasses extraction entirely — populates synthetic result row labelled
"Pasted hex". Use Export pipeline normally.

---

## 18. Keyboard Shortcuts

| Gesture          | Action       |
|------------------|--------------|
| Ctrl+O           | Browse       |
| Ctrl+E           | Extract      |
| Ctrl+Shift+E     | Compare All  |
| Ctrl+Z           | Undo         |
| Ctrl+Y           | Redo         |

---

## 19. Status Bar

Bottom strip. Default "Ready". Updates after each extract:
```
K-Means: 142 ms → 8 swatches
Compare-All: 1240 ms → 11 swatches
```

Also surfaces ad-hoc messages.

---

## 20. Theme

Tools → Toggle Dark/Light Theme. Flips Avalonia's RequestedThemeVariant.
Not persisted across sessions (relaunch = dark default).

---

## 21. Histogram

Below the image preview. RGB + Luminance 256-bin histogram of current
source.

- R = translucent red
- G = translucent green
- B = translucent blue
- Y (Rec.709 luminance) = white outlined bars

Auto-recomputes on image change.

---

## 22. File Locations

| Path                                                              | Purpose       |
|-------------------------------------------------------------------|---------------|
| %APPDATA%\PaletteBuilder\presets\*.palettebuilder.json            | Saved presets |
| %APPDATA%\PaletteBuilder\recent.json                              | MRU list      |

---

## 23. Tips & Troubleshooting

### Palette has too much background
Enable Use saliency (threshold 0.3). Or draw an ROI around the subject.

### Palette has muddy mid-tones in gradient
Switch Gradient ComboBox to OkLab.

### Want only vibrant colours, no greys
Set Min saturation to 0.2+. Or pick Material Palette method.

### Same colour appearing twice
Raise Dedup ΔE to 4 or 6. Set Dedup metric to ΔE2000.

### Too few / too many Mean Shift results
Adjust Bandwidth. Default 25; try 15 for fine, 40 for broad.

### DBSCAN returns nothing
ε too small or MinPts too high. Try ε=12, MinPts=10.

### Extract feels slow
Drop Downsample max to 128. Switch to Mini-Batch K-Means. Disable
Auto-extract.

### PDF "font not found" error
Should not occur on Windows (PDFsharp-gdi uses GDI). Ensure Arial +
Consolas TTFs are present.

### Stop lock not preserved across re-extract
Known limitation. Extract first, lock + edit after, don't re-extract.

### Saliency takes too long
~50ms on 256². Already cached per option-tuple. Disable for many images.

### Multi-image batch + Spatial K-Means
Spatial K-Means falls back to colour-only in batch mode.

### Theme switch back to dark after restart
Not persisted. Toggle each session.

### EXIF rotation wrong
Tag stripped after first rotate. Re-encoded files may have lost tag.

### Hex paste rejects values
Must be 3 or 6 hex digits. #abc and #aabbcc OK. 0xaabbcc won't parse.

---

End of documentation.
