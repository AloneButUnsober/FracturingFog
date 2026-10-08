// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Help/HelpTextBundle.cs
//
// Single source of truth for the Avalonia FloatingHelp window's static
// help text. HostHelpContentProvider reads directly from these properties
// and the FloatingHelp view renders each tab body verbatim.
//
// SCOPE: covers every tab IHelpContentProvider exposes — AboutText,
// FeaturesText, BatchText, ClientServerText, CalcGenText, ColorGenText,
// AudioText, EditorText, BioText — plus 18 math sub-tabs (Overview,
// Mandelbrot, Julia, Burning Ship, Tricorn, Multibrot, Phoenix, Newton,
// Nova, Buddhabrot, IFS, L-System, Attractor, Mandelbulb, User Equation,
// User Bulb 3D, Sandbox, Mandelbrot Z² Generated) and the supplementary
// Toolbar / Regions / Server Admin / Slideshow / Poster / Architecture
// reference tabs.

namespace FracturingFog.Help
{
    public static class HelpTextBundle
    {
        public const string AboutText =
@"Fracturing Fog — real-time high-precision Mandelbrot & friends explorer.

Built for deep zoom: double → double-double (DD) → quad-double (QD)
auto-promotion with perturbation, series approximation, and bilinear
approximation past zoom 1e25. 20+ fractal families share the same
view-state, color pipeline, and capture suite.

• UI:         Avalonia 12 MVVM shell. Cross-platform foundation;
              Windows ships first, macOS / Linux follow Skia / Metal /
              Vulkan renderer back-ends.
• Renderer:   DirectX 11 / 12 via Vortice on Windows. The render
              surface is hosted in a NativeControlHost so the DXGI
              swap-chain code path stays unchanged across UI shells.
• Calculator: SIMD-vectorized scalar / DD / QD CPU paths + perturbation
              + series approximation + BLA. ILGPU GPU path for the
              generated Mandelbrot family and User Bulb 3D.
• Themes:     200+ built-in palettes (gradient, cycling, Phong3D, PBR,
              distance estimation, orbit traps, domain coloring, …)
              plus an algorithmic ColorGen DSL and a live-preview
              theme editor. JSON import / export for sharing.
• Equations:  one safe equation language (User Equation + Sandbox),
              rendered live by an interpreter; User Equation's
              CalcGen toggle compiles it to scalar / AVX2 / GPU /
              perturbation calculators for speed and deep zoom.
• 3D:         Mandelbulb plus User Bulb 3D — Vec3 / Quat raymarched
              escape-time engine with analytic + numerical DE,
              animated time parameter, OBJ mesh export.
• Audio:      Fractured Waves Reactive Audio slideshow (loopback /
              file / microphone / fractal-synth) with on-beat region +
              theme transitions.
• Capture:    Single-frame PNG, multi-tile Poster, MP4 / lossless
              FFV1 / lossless H.264 video, PNG sequence.
• Client/Svr: Mutual-TLS render server + sealed-vault client. Render
              on a workstation, drive from a laptop.
";

        public const string FeaturesText =
@"=== Navigation — Mouse ===

  Wheel              Zoom in / out anchored at the cursor.
  Left-click drag    Pan. While dragging, the renderer runs a Fast
                     pass; a 300 ms debounce after release fires a
                     full-quality re-render.
  Double-click       Center on the clicked point + zoom in one step.
  Right-click drag   Highlight-to-zoom. Draws a marquee on the canvas;
                     releasing centers the view on the box and snaps
                     zoom so the marquee fills the panel. Mid-drag the
                     status bar shows the live target rectangle.
  Right-click (3D)   In Mandelbulb / User Bulb 3D, drag rotates the
                     camera — X = theta, Y = phi (inverted for natural
                     ""tilt up"" mapping).
  Middle-drag (3D)   Highlight-to-zoom for 3D fractals. Same marquee as
                     the 2D right-drag, on the middle button so right-
                     drag stays camera rotation. Outline a box, release
                     to recentre + zoom in; Esc cancels before release.

=== Navigation — Keyboard ===

  Commands (any fractal)
    M       Open the Control Center (the main menu).
    T       Toggle the Color Theme Editor.
    R       Reset the view to the default for the current fractal.
    V       Save the current view as a named region.
    Esc     Exit borderless multi-monitor Span; stop a running
            slideshow / video zoom; close a modal sub-dialog.

  UI scale (accessibility)
    Ctrl++  Increase the global UI scale one step (Ctrl+= also).
    Ctrl+-  Decrease the global UI scale one step.
    Ctrl+0  Reset the UI scale to 100%.
            Works from any window incl. the render window; the
            fractal surface itself is not scaled, only the UI chrome.

  2-D pan & zoom
    W / S   Zoom in / out (centred).
    A / D   Pan left / right.
    Q / E   Pan up / down.
    Shift   Hold with a pan key for a precise quarter-step.
            Shift+W / Shift+S give quarter-step (fine) zoom.

  3-D (Mandelbulb / User Bulb 3D)
    W / S   Move the camera closer / farther.
            Shift+W / Shift+S = quarter-step (fine) distance.
    A D Q E Pan in screen space.
    Arrows  Orbit the camera (↑↓ = phi, ←→ = theta).
    PgUp / PgDn   Rotate the key-light azimuth.
    Home / End    Rotate the key-light elevation.

  Focus behaviour
    Pan / zoom / camera keys are ignored while a text box (CX, CY,
    Zoom, Iter, equation editor, …) has keyboard focus. Clicking
    the render surface restores focus to the canvas — including
    after a toolbar click — so the canvas accepts keystrokes again
    without further intervention.

  Slideshow capture
    All keys (except Esc) are passed to the slideshow VCR transport
    while the slideshow is running.

=== Toolbar (top of MainWindow) ===

  Type        Active fractal family. The combo lists 17 built-ins
              plus a ""— Registered —"" divider followed by every
              promoted User Equation / Sandbox entry.
  Quality     Draft / Standard / High / Ultra / Extreme. Drives
              iteration count, wheel step, and DD / QD promotion
              thresholds. See Quality Presets below.
  Region      Named view bookmarks — built-in tour plus your saved
              regions. Right-click sorts by Default / FractalType.
  Theme       Active color map. Right-click sorts by Default / All /
              per-kind (Cycling / Phong3D / PBR3D / …).
  Grid        Toggle the Cartesian complex-plane overlay.
  Watermark   Toggle the region + theme + program watermark drawn
              into the BGRA buffer (CPU-composited so it survives
              into screenshots).
  Params      Open the per-type parameters dialog (Julia c, Newton
              degree, Phoenix p, IFS preset, etc.).
  Reset       Restore the view to the current fractal's default.
  Edit Theme  Open the modeless Color Theme Editor with the active
              theme loaded.
  Menu        Toggle the Floating Menu.
  Help        Open this Help window.

=== Floating Menu (M) ===

  Sections (top → bottom):

  View row 1     Reset · Span · Image · Poster
  View row 2     Slideshow · Video (toggle) · Close Program
  Toggles        Status · Grid + Resolution combo
  Region Nav     Region combo + Save · Delete · Exp… · Imp… +
                 CX / CY / Quality / Zoom / Iter textboxes + Lock
                 Iterations checkbox + Go · Flip Y · Copy buttons
  Color Themes   Theme combo + Exp… · Imp… · Delete · Reload +
                 Edit Theme…
  Post-FX        Brightness · Contrast · Adaptive sliders with
                 per-slider Lock checkbox + Sweep button +
                 sweep-duration NumericUpDown (seconds)
  Slideshow      Slideshow Settings…
  Remote         Server… · Client…

  Span / Video / Adaptive-Sweep button labels flip while their
  modes are active (""Back"" / ""Stop"" / ""Stop Sweep"").

=== Coordinate Panel ===

  CX, CY          Real / imaginary coordinates of the view center.
                  Accepts the pipe-separated DD / QD limb format
                  for high-precision paste-back (e.g.
                  ""-0.7548...|1.2e-17|0|0"").
  Zoom            Scalar zoom factor. Scientific notation accepted
                  up to ~5e58 (Extreme tier).
  Iterations      Max escape iterations. Minimum 64; no upper cap.
  Lock            Pin iterations across pan / zoom so deep regions
                  do not auto-recompute the iteration scaler.
  Go              Apply the typed values.
  Flip Y          Mirror the view vertically by negating every CY
                  limb (Hi + 3 low limbs) — deep-zoom precision is
                  preserved.
  Copy            Copy CX / CY / Zoom / Iter to the system clipboard.

=== Post-FX ===

  Brightness    −100 … +100  Additive offset. 0 = neutral.
  Contrast      −100 … +100  Multiplicative gain. 0 = neutral.
  Adaptive       0 … 100     Histogram equalisation strength.
                              Surfaces flat detail in deep zooms.
  Per-slider Lock pins the value when a theme switch would
  otherwise snap to that theme's authored default.
  Sweep button animates Adaptive 0 → 100 over the configured
  duration with a sine ease-in/out, then stops automatically.
  Re-press to cancel mid-sweep.

=== Color Themes ===

  Categories  Escape-time, distance estimation, orbit traps,
              binary / argument decomposition, domain coloring,
              field lines, histograms, stripe averages, potentials,
              lemniscates, Phong3D + PBR3D lighting,
              chromostereopsis, post-process, JSON-imported,
              ColorGen DSL.
  Exp / Imp   Export / import individual themes as JSON.
  Delete      Remove a user-imported theme (built-ins protected).
  Reload      Re-scan disk for edited theme JSON files.
  Editor      ""Edit Theme…"" opens the modeless Color Theme Editor.
              Saving an existing user-theme name prompts to confirm
              overwrite.

=== Overlays / Mini Windows ===

  Grid        Cartesian complex-plane overlay (toolbar / menu).
  Mini Map    Inset window of the whole set with a marker for the
              current view. Click to jump.
  Mini Depth  Per-pixel iteration-depth heat-map miniature.
  Watermark   Region + theme + program label, CPU-composited into
              the BGRA buffer so it survives screenshots.
  Status bar  CX / CY / Zoom / Iter / active precision (SP/DD/QD)
              / render time / last operation + a green / grey ●
              Server indicator for the local render server.

=== Capture ===

  Image       Single-frame screenshot. Format chosen by file
              extension (.png / .tif / .tiff / .bmp). Honours the
              live brightness / contrast / adaptive sliders.
  Poster      Multi-tile composite render at print resolution.
              Tiles stitched into one large image; suitable for
              wallpaper or printing. Also reachable from the
              Client dialog for remote-host posters.
  Video       Smooth animated zoom from the current view to the
              active region. Lossless H.264 / FFV1 / H.264HQ via
              ffmpeg, or browser-friendly Media Foundation MP4.
              Optional PNG-sequence sidecar.
  Video Slideshow
              Continuous zoom-out → next-region → zoom-in cycle.

=== Slideshow ===

  Region cycle    Default 30 s per region (configurable via
                  Slideshow Settings…). Set to 0 to lock the
                  current region.
  Theme cycle     Default 10 s per theme.
  Cross-fade      ~3 s linear blend between outgoing and incoming
                  buffers; falls to ~0.75 × beat when audio-reactive
                  is enabled.
  Fractured Waves Beat counter from system loopback / file / mic /
                  fractal-synth — see the Audio tab.
  VCR transport   ◀◀  ◀  ▮▮  ▶  ▶▶  bar at the bottom of the
                  MainWindow lets you pause, skip backward /
                  forward, or scrub through queued regions.

=== Multi-Monitor & Window Modes ===

  Span         Stretch the window across the entire virtual
               desktop. Toolbar + status auto-hide; the Span
               button reads ""Back"" while active.
  Full Screen  Borderless single-monitor full-screen.
  Mini Mode    Shrink + on-top + borderless companion view.
  On Top       Keep MainWindow above other windows.

=== Quality Presets ===

  Tier        Zoom ceiling   Iter range       Wheel step  Precision
  Draft       1e5            64 – 256          1.40       SP only
  Standard    1e13           256 – 2 048       1.20       SP → DD @ 1e12
  High        1e22           512 – 16 384      1.12       SP → DD @ 1e12
  Ultra       5e27           1024 – 65 536     1.08       SP → DD @ 1e12
  Extreme     5e58           2048 – 131 072    1.06       SP → DD → QD

  Iter auto-scales: IterBase + ⌊log10(Zoom) × IterPerDecade⌋,
  clamped to [IterBase, IterMax]. Lock Iterations pins the value.

=== Precision ===

  Double  (SP)    ~15 digits  — zoom ≤ ~1e13
  Double-Double   ~31 digits  — zoom ≤ ~1e25
  Quad-Double     ~62 digits  — zoom ≤ ~1e50+
  Auto-promotion crosses thresholds based on the active view.
  Perturbation + Series Approximation + BLA accelerate deep zooms;
  the status bar shows the live path label (SP, AVX2, DD, QD, PT,
  BLA, QD-PT).

=== Persistence ===

  All user data lives under %APPDATA%\FracturingFog\:

    regions.json              User-defined coordinate bookmarks
    colorthemes.json          User-imported / authored themes
    colorgen.json             ColorGen DSL source library
    userequations.json        User Equation source library
    sandboxequations.json     Sandbox DSL source library
    userbulbs.json            User Bulb 3D source + chain library
    audio-settings.json       Fractured Waves Reactive Audio config
    client-connections.json   Sealed (AES-GCM) server connections
    client-render-presets.json    Client render presets
    server-config.json        Local server settings
    server-certs\*.pfx        Self-signed mTLS bundle
    server-logs\*.log         Server session logs
    server-work\              Server scratch dir (auto-purged)

  All JSON files are human-readable indented output. Theme + region
  files round-trip with full DD / QD limb fidelity.
";

        public const string BatchText =
@"=== Batch / Command-Line Processing ===

Fracturing Fog can render images and zoom videos headlessly from a
command line, with no GUI window. The same calculator, palette, and
quality pipeline used interactively backs every batch render, so a
batch job produces output identical to what you would see in the UI
at the same coordinates and resolution.

Launch from cmd or PowerShell. The executable attaches to the parent
console so progress meters and final paths are visible inline.

=== Invoking batch mode ===

    FracturingFog --batch [options]
    FracturingFog -b     [options]
    FracturingFog --batch --help        (full flag reference)

Batch mode is mutually exclusive with the interactive shell — when
--batch is present no UI window opens; the process renders, writes
the requested file(s), and exits.

Exit codes:
    0   success
    1   unhandled runtime error during render
    2   bad command-line argument
    3   --lossless selected but ffmpeg.exe not found
    4   ffmpeg encode pass failed

=== Region source (pick one) ===

    --region NAME, -r NAME
        Name of a built-in region (""Seahorse Valley"", ""Mini
        Mandelbrot"", ""Classic Full View"", etc.) or a user-saved
        region. Case-insensitive. Loads center, zoom, iterations,
        fractal type, and authored quality preset.

    --x VAL --y VAL --zoom VAL [--iter N]
        Manual coordinates. Supply all three of x / y / zoom for
        a free render. --iter is optional (defaults to 1000 when
        omitted and no region default is in play).

Manual flags override individual fields of a named region, so
combining them is supported — e.g. --region ""Seahorse Valley""
--iter 4000 keeps the saved center but lifts iteration count.

=== Common flags ===

    --fractal TYPE, -f TYPE
        Fractal family. One of:
            Mandelbrot, Julia, BurningShip, Tricorn, Multibrot,
            Phoenix, Newton, Nova, BuddhaBrot, IFS, LSystem,
            StrangeAttractor, UserEquation, Mandelbulb, Sandbox,
            UserBulb, TearDrop
        Defaults to the region's saved type, else Mandelbrot.

    --theme NAME, -t NAME
        Color theme name as shown in the Theme picker. Built-in
        names like ""HSV"", ""Fire"", ""Plasma"", ""Inferno"" all work,
        as do user-imported JSON themes. Default: HSV.

    --quality NAME, -q NAME
        Draft | Standard | High | Ultra | Extreme. Default Standard.
        Higher tiers raise iteration ceilings and engage QD math.

    --width N, -w N        Output width  in pixels (default 1920)
    --height N, -h N       Output height in pixels (default 1080)

    --out PATH, -o PATH     (required)
        Image mode  — file path. Extension picks format:
            .png   PNG
            .tif / .tiff   TIFF (LZW)
            .bmp   BMP
        If --out is a folder, a filename is synthesized from the
        region/coords + theme + timestamp.

        Video mode — file path OR folder. With --lossless none a
        .mp4 file path is used directly; with --lossless h264/ffv1/
        h264hq the extension is forced to match the preset
        (mp4 / mkv / mp4). A folder is acceptable in both cases.

    --name NAME, -n NAME
        Override the auto-generated base filename (extension is
        still chosen by the chosen format / encoder).

    --verbose, -v
        Print stack traces on failure and extra diagnostics.

=== Mode ===

    --mode image|video, -m image|video
        Default: image.

=== Image mode ===

Renders a single still through the same offscreen path the
interactive Image button uses (PosterRenderer). A console spinner
runs while the calculator is working; the final line reports the
saved file size and elapsed time.

Example:
    FracturingFog --batch --region ""Seahorse Valley"" --theme Fire ^
                  --width 3840 --height 2160 --out C:\out\seahorse.png

=== Video mode ===

Animates a smooth log-zoom from --start-zoom into the target's
zoom, rendering one full frame per video frame. Frame N's
coordinate is the target center (or interpolated when --reverse
is used); zoom follows a smoothstep-eased exponential between
start and target.

Video-only flags:
    --seconds VAL          Duration in seconds (default 20.0,  0.5–600)
    --fps N                Frames per second  (default 30,     1–240)
    --start-zoom VAL       Starting zoom      (default 0.5 = full view)
    --reverse              Zoom OUT from target back to full view

Frame-by-frame progress meter:
    Video [################----------------]  52.0%  elapsed 00:03  eta 00:02  frame 26/50  zoom 14.2

PNG frame folder is written alongside the video. By default it is
kept when --lossless none and deleted when an ffmpeg lossless
preset is used (since the frames are then intermediates). Override
with --keep-frames or --no-keep-frames.

=== Lossless video encoding ===

    --lossless TYPE, -l TYPE      Default: none

      none         Built-in Windows Media Foundation H.264 MP4
                   writer (no external dependencies). Best for
                   quick exports; encoder runs while frames are
                   produced.

      h264         libx264 -qp 0 (mathematically lossless), MP4
                   container, yuv444p, +faststart. Best fidelity;
                   large files. Requires ffmpeg.exe.

      ffv1         FFV1 v3 in Matroska. True lossless intermediate;
                   significantly smaller than uncompressed but
                   still exact. Best for archival / editing
                   pipelines. Requires ffmpeg.exe.

      h264hq       libx264 -crf 18, MP4, yuv420p. Visually
                   lossless, much smaller files. Best for sharing.
                   Requires ffmpeg.exe.

When a lossless preset is selected, the workflow is two-phase:
  1. Render every frame to disk as a PNG sequence
     (frame_NNNNNN.png starting at 000001 — image2 demuxer
     compatible).
  2. Invoke ffmpeg on the sequence with the preset's argument set.
     ffmpeg progress is parsed and shown as a second meter:
        Encode [##############################--]  87.0%  …

ffmpeg.exe is discovered in:
  1. The app folder.
  2. The app's Tools\ and Resources\ subfolders.
  3. PATH.

If --lossless is set to anything other than none and ffmpeg.exe
cannot be located, batch mode exits 3 with a hint.

    --keep-frames       Retain the PNG folder after encode
    --no-keep-frames    Delete the PNG folder after encode

=== Examples ===

  4K screenshot of the seahorse valley with the Fire palette:
      FracturingFog --batch --region ""Seahorse Valley"" --theme Fire ^
                    --width 3840 --height 2160 --out C:\out\seahorse.png

  Manual coords screenshot at high quality:
      FracturingFog --batch --x -0.7269 --y 0.1889 --zoom 2500 ^
                    --iter 4000 --theme Plasma --quality High ^
                    --width 1920 --height 1080 --out C:\out\twist.png

  30-second WMF MP4 zoom into Mini Mandelbrot:
      FracturingFog --batch --mode video --region ""Mini Mandelbrot"" ^
                    --theme Plasma --seconds 30 --fps 30 ^
                    --out C:\out\zoom.mp4

  60-second lossless FFV1 archival zoom (folder out):
      FracturingFog --batch --mode video --region ""Seahorse Valley"" ^
                    --theme Fire --seconds 60 --fps 60 ^
                    --lossless ffv1 --keep-frames --out C:\out\

  Reverse zoom-out from a saved user region as visually-lossless mp4:
      FracturingFog --batch --mode video --region ""MyDeepPick"" ^
                    --theme Inferno --seconds 20 --reverse ^
                    --lossless h264hq --out C:\out\dive_out.mp4

=== Notes ===

  • Built-in regions and user-saved regions / themes / equations
    load from %APPDATA%\FracturingFog\ at batch start, so anything
    authored in the interactive shell is accessible by name.

  • Width/height are rounded down to the nearest even number in
    video mode (codec constraint).

  • Batch mode honours the same QD-precision auto-promotion the
    interactive shell uses, so deep-zoom regions at zoom 1e25+
    render correctly without any extra flags.

  • Multiple batch invocations can run in parallel; each renders
    in its own process and writes to its own --out path.
";

        public const string AudioText =
@"=== Fractured Waves Reactive Audio ===

(Formerly ""Audio Reactive"".)

The slideshow can be driven by music or any audio source so that
color-theme and region transitions land on the beat. When enabled,
the standard fixed-duration timer (12 s / 3 s) is replaced by a
beat counter: every N detected beats trigger a theme change, every
M beats trigger a region change. Cross-fade duration also scales
with the detected BPM (default 3/4 of one beat).

Open the audio settings from:
  Floating Menu  →  Audio  →  ""Audio Settings…""

The dialog is MODELESS — you can leave it open while you start the
slideshow, browse the main view, or change regions. Settings are
applied only when you click OK; clicking Cancel discards changes.

=== Master Enable ===

The Fractured Waves checkbox in Slideshow Settings (next to the
""Audio Settings…"" button) is the master switch. When OFF the
slideshow uses fixed-duration timing. When ON the engine is
started automatically whenever the slideshow runs (or remains
running if you start it directly from the dialog).

State persists between launches via
  %APPDATA%\FracturingFog\audio-settings.json

=== Source ===

Four input sources are available, selected by the ""Source""
combo at the top of the dialog:

  System Loopback  Captures whatever is currently playing on the
                   default audio output (Spotify, browser, video
                   players, games). Nothing else to configure;
                   start playing audio anywhere on the PC and the
                   detector will pick it up.

  Audio File       Plays a local file (MP3 / WAV / FLAC / OGG /
                   AIFF / WMA) through the engine. Click ""Browse…""
                   to pick a file. File playback drives the
                   detector and is also rendered to speakers so
                   you hear the same audio that's being analyzed.
                   Playback ends silently when the file finishes
                   — restart with a new file or switch source.

  Microphone       Default capture device. Good for live shows or
                   external speakers. Raise Sensitivity if the
                   mic level is low.

  Fractal Synth    Internally generated audio derived from the
                   fractal itself (closed-loop showcase mode).
                   No external source needed. Two extra options
                   apply only to this source — see below.

=== Sensitivity ===

Range 0–100 %. Default 50 %.

Controls the onset-detection threshold of the spectral-flux beat
detector. Lower values report only the strongest hits (good for
heavy drums); higher values fire on subtler transients (good for
ambient or speech).

=== Beats per Theme / Beats per Region ===

Default 8 (≈ 2 bars at 4/4) for themes, 32 (≈ 8 bars) for regions.
A region change resets the theme counter so both events never fire
on the same beat.

=== Synth BPM / Routing (Fractal Synth source only) ===

Range 30–240, default 120. Controls the synth arpeggio's tempo.
Two checkboxes route the synth: through the analyzer (closed
loop) and / or out to speakers.

=== Beat-Detector EQ ===

Five band-weight sliders (Bass / LowMid / Mid / HighMid / High),
each 0–200 %. Steer which instruments drive the beat detector.

=== Fade × beat ===

0.10 – 2.00, default 0.75. Cross-fade duration as a fraction of
one detected beat. Minimum 120 ms absolute even at high BPM.

=== Persistence ===

All values commit to disk on OK as JSON at
  %APPDATA%\FracturingFog\audio-settings.json
";

        public const string EditorText =
@"=== Color Theme Editor ===

A modeless floating window that lets you create new color themes from
scratch or edit existing ones, with live preview into the main render
window. Bound to ColorThemeEditorViewModel; runs entirely off the
ShellViewModel's IColorThemeService bridge — no direct touches to the
renderer or the on-disk theme store.

Open via:
  • Toolbar → ""Edit Theme"" button
  • Floating Menu → Color Themes → ""Edit Theme…""
  • Hotkey ""T""

The editor seeds from the currently-selected theme. Cancel-without-Save
restores the previously-active theme on close.

=== Window Layout ===

  Left column   Target (region + base theme), Identity (name, kind,
                category, description, max-zoom), Kind selector,
                Color Stops list, Cycle speed, In-Set color override,
                Post-FX defaults, action buttons.
  Right column  3D Lighting (Phong + PBR shared params: steepness /
                ambient / direction / colours / specular / shininess),
                Phong3D extras (key + fill + rim), Pbr3D extras
                (lighting mode, glow exp / scale, material bands).

Sections collapse / expand depending on the selected Kind so unused
controls are hidden.

=== Theme Kinds ===

  Gradient   Linear multi-stop interpolated palette stretched once
             across the iteration range. Best for escape-time +
             distance-estimation work.
  Cycling    Same gradient repeated N times along the iteration axis.
             CycleSpeed controls N (default 0.02 ≈ one cycle every
             50 smoothed iter units).
  Phong3D    Cycling gradient + Blinn-Phong directional lighting
             from a synthesised surface normal (Z = iter-derivative).
             Key + Fill + optional Rim lights.
  Pbr3D      Cycling gradient + Cook-Torrance physically-based
             lighting. Material bands let you switch metallic /
             roughness per iter range. Optional glow.

=== Color Stops ===

  Position   Normalised [0, 1]. 0 = start (low iter), 1 = end.
  Swatch     Click to open a colour picker. RGB / hex entry also OK.
  Add / Del  Insert above, delete selected.
  Drag       Reorder by dragging the position handle.
  Minimum 2 stops required. Linear interpolation between consecutive
  stops; outer stops clamp.

=== Randomize (Random Theme Generator) ===

The ""Random"" button (dice icon) generates a whole theme in one click,
not just the palette. It always rebuilds the colour stops via a
golden-ratio hue walk, then adds extra randomized settings chosen by
the current Kind and by the toggle row beneath the button:

  Colour stops   Always.
  Interpolation  Space / curve / transfer / strength / gamma. Toggle
                 ""Interpolation"" (default on).
  Cycle          Offset / density / speed / wrap. All Kinds but Gradient.
  3D light rig   Phong3D + Pbr3D (placement, colours, shininess,
                 steepness, ambient; rim included at random).
  Phong extras   Phong3D only (key/fill/rim spec + diff scales).
  PBR            Pbr3D only (lighting mode, glow, material bands).
  In-set colour  Toggle ""In-set"" (default on).
  Post-FX        Toggle ""Post-FX"" (default off).

Toggles:
  Random Experimental  Off = artful: values stay in logical ranges and
                       light / in-set colours are pulled from the
                       generated palette. On = experimental: caps removed,
                       full-range randomness.
  In-set               Include a random interior colour (artful = a dark
                       palette-family colour, opaque).
  Post-FX              Include random Brightness / Contrast / Adaptive-HE.
                       Ticking it also ticks the three Use-* Post-FX
                       checkboxes to match. Artful caps B/C at +/-20 and
                       Adaptive-HE under 50.
  Interpolation        Include random interpolation settings.

Seed / reproducibility:
  Each click records the seed in the Description and the Seed field.
  ""Use Seed"" off (default): the Seed field is disabled and a fresh seed
  is drawn each click. On: the field value drives the next click. Same
  seed + toggles + Kind + Experimental state reproduces the exact theme.

=== 3D Lighting (Phong3D + Pbr3D) ===

  Steepness   Z-scale on the synthetic surface normal. Higher =
              more relief. Negative = inverted relief.
  Ambient     Base illumination before lighting. 0 = pitch black
              shadows; 0.15 is typical.

  Key Light   Strong, often warm: Direction (X / Y / Z), Diffuse
              RGB, Specular RGB, Shininess (exponent 1 – 256).
  Fill Light  Dim, often cool: same fields. Sims sky / bounce.
  Rim Light   Optional back-light for silhouette highlight.

=== Pbr3D Extras ===

  Lighting mode    PBRRealistic | PBRBright. PBRBright pre-multiplies
                   incoming radiance for a punchier sci-fi look.
  Glow exp / scl   Additive emission curve near escape (t → 1).
  Material bands   List of (start t, end t, metallic, roughness)
                   tuples. Lets you make the cardioid look matte
                   while filaments stay chromed, for example.

=== In-Set Color ===

Override the in-set fill (default opaque black). Useful when the
gradient's tail is dark and ""black holes"" would visually merge. The
override carries an alpha channel: below 255 the interior composites
over the 2D interior background (per-theme, #96), multiplied by the
global InteriorAlpha knob.

=== Post-FX Defaults ===

Per-theme defaults for Brightness / Contrast / Adaptive. Applied
automatically when you switch to this theme — unless the relevant
slider's Lock checkbox is ticked, in which case the current value
is preserved.

=== Live Preview & Actions ===

  Live preview   When ticked, edits push to the main render via a
                 150 ms debounce. Drag freely; calculator re-runs once.
  Apply          Force a push regardless of live-preview state.
  New Blank      Discard edits, start from a fresh Gradient
                 (black → white, two stops).
  Revert         Reload from the last source theme name.
  Save to Library
                 Validates Name / ≥ 2 stops, then adds or replaces a
                 user theme in %APPDATA%\FracturingFog\colorthemes.json.
                 If the typed name already exists, a confirmation
                 prompt appears before overwriting.
  Export JSON…   Writes a single-theme JSON array to disk.
  Save C#…       Writes a compilable C# class via the shared
                 ColorThemeCsExporter so the theme can ship built-in.
  From Image…    Sample a user-supplied bitmap (PNG / JPG) and
                 generate a 5-stop palette via KMeans clustering.
                 Loads straight into the Color Stops list ready to
                 tweak. (See Image Palette helper.)

=== Image Palette Helper ===

A small sibling dialog accessible from ""From Image…"" and from the
toolbar's ColorGen Editor. Loads an image, samples N centroids via
k-means in CIELAB space, returns them sorted by hue. Useful starting
point for matching real-world references (album cover, photograph,
product shot).

=== File Format ===

  Library file:  %APPDATA%\FracturingFog\colorthemes.json
  Source seed:   <install>\Resources\ColorThemes\colorthemes.json

  Each entry is a single ColorThemeData object emitted via
  WhenWritingNull semantics — anything left null in the editor is
  omitted from the JSON entirely.

=== See Also ===

  Docs\ColorGen-UserGuide.md         Algorithmic DSL palette authoring
  Docs\ColorThemeEditor-Guide.md     Full walkthrough + 20 worked examples
";

        public const string BioText =
@"=== Benoit B. Mandelbrot (1924 – 2010) ===

Polish-born French-American mathematician known as the
""father of fractal geometry"".

Born:        20 November 1924, Warsaw, Poland
Died:        14 October 2010, Cambridge, Massachusetts, USA (age 85)
Citizenship: French and American

=== Early Life ===

Born to a Lithuanian Jewish family in Warsaw, Mandelbrot fled with
his family to France in 1936 to escape the rising Nazi threat.
He was tutored largely by his uncle Szolem Mandelbrojt, a
mathematician at the Collège de France. During WWII he hid in the
French countryside, attending school sporadically; despite this he
later credited his exceptional visual-geometric intuition to his
self-taught, picture-driven approach to mathematics.

=== Education & Career ===

  • École Polytechnique, Paris (Gaston Julia, Paul Lévy — 1944–47)
  • Caltech — M.S. in aeronautics (1949)
  • University of Paris — Ph.D. in mathematical sciences (1952)
  • Institute for Advanced Study, Princeton (1953, under von Neumann)
  • IBM Thomas J. Watson Research Center, Yorktown Heights NY
    (1958 – 1987) — IBM Fellow from 1974
  • Yale University — Sterling Professor of Mathematical
    Sciences (1999, becoming Yale's oldest tenure appointee)

=== Contributions ===

  • Coined the word ""fractal"" (1975, from Latin fractus = ""broken"")
  • Foundational text: ""The Fractal Geometry of Nature"" (1982)
  • Studied long-range dependence in cotton prices, river floods,
    word frequencies, telephone-line noise — finding scale
    invariance everywhere classical statistics had assumed
    Gaussian / Brownian behaviour
  • Multifractal formalism for turbulence and finance
  • Discovered & explored the set that now bears his name (1980)
  • Coastline paradox (1967) — ""How long is the coast of Britain?""

=== Honors (selected) ===

  • 1985  Barnard Medal for Meritorious Service to Science
  • 1986  Franklin Medal
  • 1993  Wolf Prize in Physics
  • 2003  Japan Prize for Science and Technology
  • 2006  Légion d'honneur (Officer)

=== In His Own Words ===

  ""Bottomless wonders spring from simple rules, which are
   repeated without end.""

  ""Clouds are not spheres, mountains are not cones, coastlines
   are not circles, and bark is not smooth, nor does lightning
   travel in a straight line.""
                                          — The Fractal Geometry of Nature
";

        // ── Math sub-tabs ─────────────────────────────────────────────────

        public const string MathMandelbrotText =
@"=== The Mandelbrot Set ===

The Mandelbrot set M is the set of complex numbers c for which the
quadratic iteration

        z₀ = 0
        zₙ₊₁ = zₙ² + c

remains bounded (|zₙ| ≤ 2 for all n). Points outside M escape to
infinity at a finite rate; that escape rate, colored by a palette,
produces the familiar fractal imagery.

=== Historical Timeline ===

  1905   Pierre Fatou & Gaston Julia study iteration of rational
         maps on the complex plane. Julia describes connected /
         disconnected behaviour but cannot visualize it.
  1978   Robert W. Brooks and Peter Matelski publish the first
         crude computer-generated picture of the set in their
         paper on Kleinian groups.
  1980   Benoit B. Mandelbrot, working at IBM's Yorktown Heights
         lab, produces high-resolution renders that reveal the
         set's astonishing self-similar structure. He coins the
         name in 1982.
  1985   Adrien Douady & John H. Hubbard prove M is connected and
         introduce the parameter ray theory.
  1991   Mitsuhiro Shishikura proves the boundary of M has
         Hausdorff dimension 2.
  2000s  Perturbation methods (K. I. Martin) make zooms past 1e50
         tractable on consumer hardware.

=== Mathematical Properties ===

  • Connected: every point in M is path-connected (Douady-Hubbard).
  • Boundary has fractal (Hausdorff) dimension 2.
  • Area ≈ 1.50659177 (numerical; closed form unknown).
  • Locally — but not globally — self-similar. Tiny copies of M
    (""mini-brots"") appear at every scale, embedded in spirals,
    filaments, and dendrites.
  • The Mandelbrot set is the bifurcation locus of the family
    fₐ(z) = z² + c — it indexes all quadratic Julia sets.
  • Cardioid: the main body is the image of the unit disk under
    w → w/2 − w²/4. Its cusp lies at c = 1/4.
  • Period-2 bulb: the circle of radius 1/4 centered at c = −1.
  • Conjecture (MLC): M is locally connected — open since 1985,
    one of the deepest open problems in complex dynamics.

=== Escape-Time Algorithm ===

  function mandelbrot(c, maxIter):
      z = 0
      for n in 0 … maxIter:
          if |z| > 2: return n            # escaped
          z = z * z + c
      return maxIter                      # inside (treated as)

The bailout |z| > 2 follows from the fact that once |z| exceeds 2
the orbit must diverge. Smoothing tricks (continuous escape time,
distance estimation, orbit traps) extract sub-pixel detail.

=== Why Deep Zoom is Hard ===

At zoom 10ⁿ the pixel spacing is ~4·10⁻ⁿ. IEEE-754 double has
~15 decimal digits, so beyond zoom 10¹³ the pixel grid stops
resolving distinct complex numbers — banding and ""solid-color""
artifacts appear. Solutions:

  • Extended precision (DD, QD, MPFR). Slow per-pixel but exact.
  • Perturbation theory: iterate ONE reference orbit in high
    precision, then iterate per-pixel deltas in double. Series
    approximation + bilinear approximation (BLA) skip thousands
    of inner iterations at a time.

Fracturing Fog uses double / DD / QD auto-promotion combined with
perturbation + SA + BLA for zooms past 1e45.
";

        public const string MathJuliaText =
@"=== The Julia Set ===

Fix c ∈ ℂ. The filled Julia set K(c) is the set of z₀ ∈ ℂ whose
orbits under

        zₙ₊₁ = zₙ² + c

remain bounded. Unlike the Mandelbrot set (parameter plane), the
Julia set lives in the dynamical plane: every point on screen is
treated as a candidate z₀ with the SAME c.

=== Connection to the Mandelbrot Set ===

Fatou: K(c) is connected ⇔ 0 ∈ K(c) ⇔ c ∈ M. Pick c inside the
Mandelbrot set: you get a connected, often dendritic, Julia set.
Pick c outside: you get a totally disconnected Cantor dust
(""Fatou dust""). Mandelbrot's set is therefore a topological
catalogue of all quadratic Julia sets.

=== Notable c Values ===

  c =  0           Unit disk (the trivial Julia set)
  c = −1           San Marco fractal (period-2 fixed cycle)
  c = −0.7 + 0.27i Dragon-like dendrite (Fracturing Fog default)
  c = −0.835−0.232i Spiraling dendrite
  c =  0.285+0.01i Cauliflower / mini-Mandelbrot lookalike
  c = −0.4+0.6i    Douady's rabbit
  c = i            Dendrite of Misiurewicz type

=== Escape-Time Algorithm ===

  function julia(z, c, maxIter):
      for n in 0 … maxIter:
          if |z| > 2: return n
          z = z * z + c
      return maxIter

Only the initial condition changes: z₀ = pixel coordinate, c =
the dialog-supplied constant.

=== Symmetry ===

J(c) is symmetric under z → −z (rotation by π). Many Julia sets
exhibit additional discrete rotational symmetries when c lies on
the boundary of a bulb.
";

        public const string MathNewtonText =
@"=== Newton Fractal ===

Newton's root-finding iteration applied as a dynamical system on ℂ.
For polynomial f(z) the iteration

        zₙ₊₁ = zₙ − R · f(zₙ) / f'(zₙ)

converges quadratically (for R = 1) to a root once near enough.
The Newton fractal colors each pixel by WHICH root the iteration
converged to, optionally shaded by speed of convergence.

Default polynomial:  f(z) = z^d − 1     (roots = d-th roots of 1)

        f'(z) = d · z^(d−1)
        z   ← z − R · (z^d − 1) / (d · z^(d−1))

=== Geometry ===

  • d basins, one per root of unity, each meeting EVERY OTHER
    basin at every boundary point. This is Wada's lakes property:
    no boundary pixel borders fewer than d basins.
  • Boundary has fractal dimension > 1.
  • Hausdorff dimension increases with d.
  • Relaxation parameter R ≠ 1 (""generalized Newton"") slows or
    accelerates convergence; R = 2 produces the ""Halley method""-
    flavoured shape.

=== Historical Notes ===

  • Cayley (1879) studied the d = 2 case. Cayley conjectured —
    incorrectly — that the d = 3 case would behave just as
    cleanly, but boundary basins are dense.
  • The intricate boundary was first drawn by Peitgen, Saupe &
    Jürgens (1980s).

=== Parameters ===

  NewtonExponent   : int      Polynomial degree d. Default 3.
                              Clamped to [2, 8].
  NewtonRelaxation : double   Relaxation factor R. Default 1.0.
  NewtonPolyCoeffs : Complex[]?  Optional custom polynomial
                                 coefficients (currently unused
                                 by the default kernel; reserved
                                 for future custom-polynomial
                                 path).
";

        public const string MathMandelbulbText =
@"=== The Mandelbulb ===

Daniel White & Paul Nylander (2007–2009). A 3D analogue of the
Mandelbrot set using the ""triplex"" power formula:

        for v = (x, y, z) in ℝ³:
          r     = |v|
          θ     = arctan2(√(x² + y²), z)        (polar angle)
          φ     = arctan2(y, x)                 (azimuth)

          v^p   = r^p · ( sin(p·θ)·cos(p·φ),
                          sin(p·θ)·sin(p·φ),
                          cos(p·θ) )

          vₙ₊₁ = vₙ^p + c                       (c = pixel ray)

Power p = 8 is the classic Mandelbulb; other powers give
different bulb shapes.

=== Rendering ===

Mandelbulb is rendered via DISTANCE ESTIMATION + RAYMARCHING:

  1. For each pixel cast a ray from the camera.
  2. At each ray position v, iterate the triplex formula and
     track an analytic running derivative dr.
  3. Distance estimate:
       DE(v) ≈ 0.5 · log(r) · r / dr
  4. Step the ray forward by DE(v) until DE < ε (hit) or
     ray exits bounding sphere (miss).
  5. Estimate the surface normal by central differences of DE
     and shade with a directional light.

=== Parameters ===

  BulbPower           : double  Triplex power p. Default 8.
  BulbIterations      : int     DE inner iters. Default 8.
  BulbMaxSteps        : int     Raymarch step cap. Default 96.
  BulbEpsilon         : double  Hit threshold. Default 0.0015.
  BulbCameraDistance  : double  Camera-origin distance. Default 3.
  BulbCameraTheta/Phi : double  Camera spherical angles.
  BulbLightTheta/Phi  : double  Light spherical angles.
";

        public const string MathOverviewText =
@"=== Fractals in Fracturing Fog ===

The Mathematics tab groups every fractal family the renderer can
produce.  Each subtab covers one family:

  • Mandelbrot    Escape-time z² + c on the parameter plane
  • Julia         Escape-time z² + c with c fixed (dynamical plane)
  • Burning Ship  z → (|Re z| + i|Im z|)² + c
  • Tricorn       z → conj(z)² + c
  • Multibrot     z → z^d + c, integer d ≥ 2
  • Phoenix       z → z² + c + p·z_(n−1)  (two-step memory)
  • Newton        Root-finding basins of f(z) = z^d − 1
  • Nova          Newton-style with c offset (relaxation map)
  • Buddhabrot    Density plot of escaping Mandelbrot orbits
  • IFS           Affine contractions via chaos game
  • L-System      String-rewriting + turtle graphics
  • Attractor     Strange attractors (Clifford, De Jong, Lorenz)
  • Mandelbulb    3D triplex-power distance-estimation render
  • User Equation Your own escape-time map in the equation language

=== Categories ===

Escape-time          Mandelbrot, Julia, Burning Ship, Tricorn,
                     Multibrot, Phoenix, User Equation
Root-finding         Newton, Nova
Density / point      Buddhabrot, IFS, Attractor
Geometric rewrite    L-System
3D distance estimate Mandelbulb

=== Common Render Pipeline ===

For escape-time families the renderer:

  1. Maps each pixel to a complex c via
       cx = CenterX + (x − W/2)·scale
       cy = CenterY + (y − H/2)·scale
       scale = (3.5 / max(W,H)) / Zoom
  2. Iterates the family's recurrence with z₀ = 0 (or z₀ = c).
  3. Stops at |z|² ≥ bailout² or iter ≥ MaxIterations.
  4. Smooths the iteration count:
       smooth = n + 1 − log₂(log₂(|z|))
  5. Feeds smooth (and derivative-derived distance / normal data)
     into the active IColorMap to produce the pixel ARGB.

For non-escape families the rendering pipeline differs — see the
individual subtabs.

=== Why Each Family Looks Different ===

Tiny algebraic tweaks to a single recurrence produce wildly
different geometry.  Changing z² → conj(z)² gives Tricorn;
absolute-value the components and you get Burning Ship; raise the
power d to get Multibrot.  All share the same skeleton; the
difference is one or two lines of arithmetic inside Step().
";

        public const string MathBurningShipText =
@"=== The Burning Ship ===

Discovered by Michael Michelitsch & Otto E. Rössler (1992).  Same
escape-time framework as Mandelbrot, but each iteration takes the
ABSOLUTE VALUE of both components before squaring:

        zₙ₊₁ = ( |Re(zₙ)| + i·|Im(zₙ)| )² + c

Expanding:

        zr' = zr² − zi² + cx          (after |zr|, |zi|)
        zi' = 2·|zr|·|zi| + cy

The map is NOT analytic — the derivative is discontinuous along
the real and imaginary axes.  This sharp cusp behaviour creates
the characteristic ""flames"" and ""mast"" structures.

=== Why ""Burning Ship""? ===

Rendered conventionally with the imaginary axis inverted, the main
body resembles a three-masted galleon engulfed in flames.

=== Set Location ===

Re ∈ [−2.5, 1.5],  Im ∈ [−2, 1.5] (with axis convention above).
Notable features:

  • Main hull around c ≈ (−1.75, 0)
  • Antenna spike along the negative real axis past −1.94
  • Mini-ship at c ≈ (−1.7568, −0.0381)

=== As a User Equation ===

  --- Burning Ship ---
  // fold(z) = |Re z| + i|Im z|; square it, then + c
  fold(z)^2 + c
";

        public const string MathTricornText =
@"=== The Tricorn / Mandelbar ===

Studied by Crowe, Hasson, Rippon & Strain-Clark (1989).  Replace
z² with its complex CONJUGATE squared:

        zₙ₊₁ = conj(zₙ)² + c

In component form (zr + i·zi):

        zr' =  zr² − zi² + cx
        zi' = −2·zr·zi + cy        ← sign flip vs Mandelbrot

The conjugation makes the map ANTI-holomorphic; its even-order
iterate conj(conj(z)²)² = (z̄²)̄² = (z²)² is holomorphic, so
period-2 dynamics behave like a Multibrot of degree 4.

=== Geometry ===

Threefold symmetric: the set looks like a three-cornered
""Mandelbar"" with three large lobes meeting at the origin.  Mini
Mandelbrot-like islands appear in the antenna.  Boundary has
fractal dimension 2 (Inou 2000).

=== Connection to Bicomplex Dynamics ===

The Tricorn is the connectedness locus of the family
fc(z) = conj(z)² + c, and is the parameter plane of the
""anti-quadratic"" maps.  Period-2 bulbs are PARABOLIC (semi-stable),
giving sharp cusp boundaries instead of smooth bulbs.

=== As a User Equation ===

  --- Tricorn ---
  // conjugate before squaring
  conj(z)^2 + c
";

        public const string MathMultibrotText =
@"=== Multibrot Sets ===

Generalize Mandelbrot by raising z to an integer power d ≥ 2:

        zₙ₊₁ = zₙ^d + c                d ∈ ℤ, d ≥ 2

  d = 2   Standard Mandelbrot
  d = 3   ""Tribrot"" — twofold symmetric, two cardioids
  d = 4   Threefold symmetric, three cardioids
  d = n   (n−1)-fold rotational symmetry

The bulb structure rotates by 2π/(d−1) around the origin.  Each
multibrot has the same self-similarity properties as Mandelbrot
but with more arms.

=== Implementation Note ===

Repeated complex multiplication for d > 4 accumulates floating-
point error.  The kernel uses polar form:

        r     = |z|,  θ = arg(z)
        z^d   = r^d · (cos(d·θ) + i·sin(d·θ))

Derivative tracking (for distance estimation / normals) uses
d·z^(d−1) similarly converted through polar form.

=== Parameters ===

  MultibrotExponent : int   Power d.  Default 3.  Clamped to ≥ 2.

=== Fractional d ===

Real (non-integer) exponents are mathematically defined via
z^d = exp(d·log z), but produce branch-cut artifacts.  Fracturing
Fog uses integer d to avoid this.

=== As a User Equation ===

  --- Multibrot (d = 3) ---
  // any power works, e.g. z^2.5 + c
  z^3 + c
";

        public const string MathPhoenixText =
@"=== The Phoenix Fractal ===

Introduced by Shigehiro Ushiki (1988).  Second-order recurrence:

        zₙ₊₁ = zₙ² + c + p · zₙ₋₁

Carries TWO-STEP memory.  The extra p·z_(n−1) term couples the
current iterate to its predecessor, allowing dynamics that pure
first-order maps cannot produce.

=== Properties ===

  • For p = 0 it collapses to the standard Mandelbrot (or Julia)
    family.
  • For p real and small (|p| < 0.1) the resulting set looks like
    a deformed Julia set with phoenix-feather plumes.
  • The set lies in the dynamical plane (z₀ = pixel) — c is the
    parameter, but in Fracturing Fog the pixel coordinate plays
    the role of c and the iterate z starts at 0, mirroring the
    Mandelbrot convention.
  • A famous orientation (p ≈ 0.5667, c ≈ 0) produces an iconic
    flame-bird outline — the namesake ""phoenix"".

=== Implementation ===

  zr', zi'   = zr² − zi² + cx + Re(p·prev),  2·zr·zi + cy + Im(p·prev)
  prev       ← (zr, zi)                     before the new value lands

State carried per pixel: (zr, zi, prevZr, prevZi).

=== Parameters ===

  PhoenixP : Complex   Coupling constant p.  Default (0.56667, 0).

=== As a User Equation ===

  --- Phoenix (p = 0.56667) ---
  // prev is the previous iterate z_(n−1): the same recurrence
  z*z + c + 0.56667*prev
";

        public const string MathNovaText =
@"=== Nova Fractal ===

Variant of the Newton fractal — Paul Derbyshire (1990s).  Adds a
constant offset c to the Newton iteration so the parameter c can
be varied per pixel (parameter-plane Newton):

        zₙ₊₁ = zₙ − R · f(zₙ) / f'(zₙ) + c

For f(z) = z^d − 1 this gives a Mandelbrot-flavoured Newton:
roots, basins, and a Mandelbrot-style boundary all in one image.
Setting c = 0 collapses Nova back to the pure Newton fractal.

=== Properties ===

  • Combines basin colouring (root convergence) with escape-time
    colouring (when iteration fails to converge).
  • Tends to produce intricate ""embedded Mandelbrot"" copies on
    the basin boundaries.
  • Heavily dependent on the start point convention.  Two common
    choices: z₀ = 1 (yields the canonical Nova) and z₀ = c
    (parameter-plane variant).

=== Implementation Note ===

Fracturing Fog currently routes Nova through the same calculator
as Newton.  Selecting Nova in the UI uses the Newton kernel with
the user's exponent and relaxation; full Nova c-offset support is
on the roadmap.

=== As a User Equation ===

  --- Nova (z³ − 1, c offset) ---
  // start from z = 1 (or set z₀ seed = 1)
  if (n == 0) z = 1;
  z - (z^3 - 1) / (3*z^2) + c
";

        public const string MathBuddhabrotText =
@"=== The Buddhabrot ===

Melinda Green (1993).  A density plot of the MANDELBROT orbits —
not the set itself.  For each randomly sampled c whose orbit
escapes within a chosen iteration band, the entire orbit
(z₀, z₁, z₂, …) is replayed and each visited pixel gets a +1
increment.  The accumulated density buffer is then tone-mapped
through the active color map.

Mathematical definition:

        ρ(z) = Σ_{c escaping}  Σ_{n ≥ 0}  𝟙[ zₙ(c) ≈ z ]

=== Nebulabrot ===

Three iteration bands feed R, G, B channels separately:
short orbits → red, mid orbits → green, long orbits → blue.
The result has a luminous, nebula-like appearance — hence
""Nebulabrot"".

=== Rotation ===

Conventionally displayed rotated 90° so the cardioid is vertical
and the structure resembles a seated Buddha figure (Daniel Green
2003, the namesake).

=== Parameters ===

  BuddhaSamples   : int   Number of c samples.  Default 500 000.
  BuddhaIterLow   : int   Low band cutoff iters.  Default 500.
  BuddhaIterMid   : int   Mid band cutoff iters.  Default 5 000.
  BuddhaIterHigh  : int   High band cutoff iters.  Default 50 000.
  BuddhaMinIter   : int   Shortest escape that is drawn.  Default 12.
                          Faster escapers wash the |c| <= 2 disc with haze;
                          0 draws every orbit.  Anti types ignore it.

More samples → smoother density and brighter output.  Render time
scales linearly with samples and roughly linearly with band size.

=== Stochastic — Not Deterministic ===

Two runs at the same view produce slightly different images
because samples are random.  Higher sample counts converge toward
the underlying density distribution.

=== As a User Equation ===

Buddhabrot is a HISTOGRAM render, not an escape-time recurrence —
it can't be expressed in the User Equation kernel.  Choose
FractalType = BuddhaBrot for the real thing.
";

        public const string MathIFSText =
@"=== Iterated Function Systems (IFS) ===

Hutchinson (1981), popularized by Michael Barnsley.  A finite set
of CONTRACTIVE affine maps

        T_i(x, y) = ( a_i·x + b_i·y + e_i ,
                      c_i·x + d_i·y + f_i )

has a unique compact attractor A satisfying

        A = ⋃ T_i(A)

The attractor is rendered via the CHAOS GAME (Barnsley): pick a
random point, apply a randomly chosen map (weighted by w_i), and
plot the result.  After N iterations the plotted points fill the
attractor with arbitrary precision.

=== Classic Examples ===

  Sierpinski Triangle   3 half-scale maps to triangle corners
  Sierpinski Carpet     8 third-scale maps (excludes center)
  Barnsley Fern         4 maps with a single tiny stem map
  Koch Snowflake        4 third-scale maps, one rotated
  Dragon Curve          2 half-scale rotated maps
  Tree                  2 + branch maps

=== Self-Similarity Dimension ===

For N maps each of contraction ratio r the similarity dimension
is

        d = log N / log (1/r)

Sierpinski triangle (N=3, r=1/2):  log 3 / log 2 ≈ 1.585
Koch curve         (N=4, r=1/3):  log 4 / log 3 ≈ 1.262

=== Parameters ===

  IFSPresetName : string   ""Sierpinski Triangle"", ""Barnsley Fern"",
                           ""Koch"", ""Dragon"", ""Tree"", etc.
  IFSIterations : int      Total chaos-game iterations.  Default
                           2 000 000.  More iterations → smoother
                           coverage of fine attractor branches.
  IFSMaps       : List<AffineMap>?
                           Optional override.  Each AffineMap is
                           (A, B, C, D, E, F, Weight).  Picks are
                           weighted by Weight; the first 50
                           settle iterations are discarded.

=== As a User Equation ===

IFS uses the chaos game, not per-pixel iteration — it cannot be
expressed through the User Equation kernel.
";

        public const string MathLSystemText =
@"=== L-Systems ===

Aristid Lindenmayer (1968) — formal grammar for modelling plant
growth.  An L-system is

  axiom    A finite starting string.
  rules    Productions ""X → string""; rewrite every occurrence
           of X simultaneously each generation.
  alphabet Symbols.  Standard turtle interpretation:
              F   forward, draw line
              f   forward, no draw
              +   turn left by Δθ
              −   turn right by Δθ
              [   push state (position + heading)
              ]   pop state

After N generations the resulting string is walked as TURTLE
GRAPHICS, drawing the fractal curve.

=== Classic Curves ===

  Hilbert            Space-filling curve.  Axiom: A.
                     A → −BF+AFA+FB−, B → +AF−BFB−FA+
  Koch Snowflake     Axiom: F++F++F.   F → F−F++F−F
  Koch Curve         Open Koch.  Axiom: F.   F → F+F−−F+F   (60°)
  Dragon Curve       Axiom: FX.        X → X+YF+,   Y → −FX−Y
  Sierpinski Curve   Many variants — F + rotation rules
  Plant              Axiom: X.   X → F[+X][−X]FX,   F → FF
  Penrose            Sub-tiling rules over multiple symbols
  Pythagoras Tree    Branching binary tree (45°).  Axiom: A.
                     A → B[+A]−A,   B → BB
  Peano              Space-filling, 9-segment (90°).  Axiom: X.
                     X → XFYFX+F+YFXFY−F−XFYFX
                     Y → YFXFY−F−XFYFX+F+YFXFY
  Levy C Curve       Self-similar C (45°).  Axiom: F.   F → +F−−F+
  Pentigree          Five-fold McWorter (36°).  Axiom: F.
                     F → +F++F−−−−F−−F++F++F−

=== Dimension ===

For self-similar curves the dimension matches the IFS formula:
log N / log (1/r).  The Hilbert curve has dimension 2 — it FILLS
the plane in the limit.

=== Parameters ===

  LSystemPresetName : string   Preset name (""Hilbert"", ""Koch"",
                               ""Dragon"", ""Plant"", …).
  LSystemDepth      : int      Generation count N.  Default 5.
                               Clamped to [0, 12]; strings grow
                               exponentially with depth.

=== As a User Equation ===

L-Systems use string-rewriting + turtle graphics — they cannot be
expressed through the User Equation per-pixel kernel.
";

        public const string MathAttractorText =
@"=== Strange Attractors ===

A 2D / 3D non-linear discrete dynamical system

        (xₙ₊₁, yₙ₊₁) = F(xₙ, yₙ; a, b, c, d)

whose orbits, after a brief transient, settle onto a set of
fractional (non-integer) Hausdorff dimension — a STRANGE
ATTRACTOR.  Rendered by iterating millions of points and
accumulating a per-pixel hit density.

=== Built-in Attractors ===

  Clifford    xₙ₊₁ = sin(a·yₙ) + c·cos(a·xₙ)
              yₙ₊₁ = sin(b·xₙ) + d·cos(b·yₙ)
              Defaults: (a, b, c, d) = (−1.4, 1.6, 1.0, 0.7)
              Smooth, butterfly-shaped.

  De Jong     xₙ₊₁ = sin(a·yₙ) − cos(b·xₙ)
              yₙ₊₁ = sin(c·xₙ) − cos(d·yₙ)
              Defaults: (1.4, −2.3, 2.4, −2.1).
              Wispy filaments with delicate symmetry.

  Hopalong    Barry Martin (1986).  Iterates an absolute-value
              and sign-of-x formula; produces concentric arcs.
              Defaults: (2.0, 1.0, 0.0, 0.0).

  Lorenz      3D continuous system (a = σ, b = ρ, c = β):
                ẋ = σ(y − x)
                ẏ = x(ρ − z) − y
                ż = xy − βz
              Projected to 2D via (x, z) for display.
              Defaults: σ=10, ρ=28, β=8/3.  The canonical
              ""butterfly"" attractor.

=== Mathematical Properties ===

  • Sensitive dependence on initial conditions: nearby orbits
    diverge exponentially (positive Lyapunov exponent).
  • Bounded yet unstable — orbits stay within a finite region.
  • Hausdorff dimension fractional and typically irrational.

=== Parameters ===

  AttractorPresetName  : string  ""Clifford"", ""De Jong"", ""Hopalong"",
                                 ""Lorenz"".
  AttractorIterations  : int     Points to plot.  Default 2 000 000.
  AttractorA/B/C/D     : double  Per-attractor parameters.

=== As a User Equation ===

Strange attractors are point-density renders, not per-pixel
escape time — they cannot be expressed through User Equation.
";

        // #1090 — the equation language (#937) shared by the User Equation and
        // Sandbox help panels. Spec: Docs/Technical/Equation-Language.md.
        public const string EquationGrammarText =
@"=== The equation language ===

One language for the User Equation editor, the Sandbox editor,
the z₀ seed and the Bail if condition, and CalcGen (#937).  You
write the NEXT iterate, z_(n+1), as an expression.

  Variables   z      the current iterate (starts at z₀; 0 by default)
              c      the pixel (constant per pixel)
              n      iteration index, a real (alias: iter)
              prev   the previous iterate z_(n−1); 0 before step 1
  Constants   pi  e  i

=== Operators (loosest first) ===

  let x = a in b          local name (also: x = a; on its own line)
  if cond then a else b   conditional (also: cond ? a : b)
  ||   &&   !             logic; 0 is false, anything else true
  <  >  <=  >=  ==  !=    compare REAL values; project a complex
                          value first with re/im/abs/norm/arg
  +  −                    add, subtract
  *  /                    multiply, divide
  unary −                 −z^2 means −(z^2); write (−z)^2 for the other
  ^                       any exponent, right-associative: z^2.5, z^−1

  Comments:  // to end of line,  /* block */.  One trailing ; is fine.

=== Functions ===

  sin cos tan  sinh cosh tanh  asin acos atan  asinh acosh atanh
  exp  log  sqrt  sqr  pow(a, b)  conj
  re(x)  im(x)  arg(x)  atan2(y, x)           real-valued
  abs(x)    the magnitude |x|  (everywhere, conditions included)
  norm(x)   the squared magnitude |x|² — the cheap bailout-style test:
            if norm(z) > 4 … is |z| > 2
  fold(x)   (|Re x|, |Im x|)  — the Burning Ship fold
  floor ceil round trunc fract sign    per component
  min(a, b)  max(a, b)  mod(x, p)  clamp(x, lo, hi)

  Before #1088, -x^y meant (-x)^y and a condition abs(x) meant |x|².
  Saved equations were rewritten automatically so they look the same;
  the old files were kept beside them as *.equation-language-v2.bak.

=== Statements ===

Longer equations can use statements; the last line is the value:

    var w = fold(z);
    return w*w + c;

  if (n == 0) z = c;      seeds z on the first step
  if (cond) return a;     early value

Errors name the spot:  Unknown function 'sni' at line 1, col 7.
Did you mean 'sin'?  — the editor highlights it and Ctrl+. fixes it.
";

        public const string MathUserEquationText =
@"=== User Equations — Overview ===

Type your own escape-time map as an equation; Fracturing Fog
iterates it per pixel:

        z = z₀                           (z₀ seed; blank = 0)
        for n in 0 … MaxIterations:
            if |z| ≥ Escape r or ""Bail if"" is true: stop
            z = <your equation>

The safe interpreter renders every edit live (about two seconds
after you stop typing).  It has no access to files, the network
or .NET — equations are safe to share and import.

Open via:  Type → User Equation, then Params.

=== CalcGen toggle ===

Turn on CalcGen to compile the equation to a native calculator:

  Compile & Load        compile in the background and swap it onto
                        the live render (SIMD, GPU, deep zoom)
  Compile + Save        the same, and reload it on the next launch
  Generate via CalcGen  write Calculators\Generated\{Name}Calculator.cs

The state line reads Not compiled → Compiling… → Compiled (or
Compile failed).  The report line says what CalcGen accelerates for
THIS equation: perturbation (deep zoom), series approximation and
DE / normals — or why it can't compile it.  The z₀ seed and Bail if
are interpreter-only.  An edit returns the view to the interpreter.

C#-style text (return Complex.Pow(z, 2) + c;) is still accepted and
translated; Ctrl+. converts it to the equation language.

" + EquationGrammarText + @"
=== Orbit controls ===

  Escape r   bailout radius; 0 = auto (interpreter 32, CalcGen 512)
  z₀ seed    start value, e.g. c for a Julia-style seed
  Bail if    stop early when true, e.g. abs(z - prev) < 0.0001
             (converge: Newton / Magnet / Nova)

=== Examples — Drop-in Snippets ===

Each block below is a complete equation — paste it into the editor.

  --- Mandelbrot ---
  z*z + c

  --- Julia (constant baked; set z₀ seed = c) ---
  z*z + (-0.7 + 0.27015*i)

  --- Burning Ship ---
  fold(z)^2 + c

  --- Tricorn / Mandelbar ---
  conj(z)^2 + c

  --- Multibrot (any power) ---
  z^5 + c

  --- Phoenix (uses the previous iterate) ---
  z*z + c + 0.56667*prev

  --- Newton (z³ − 1; set z₀ seed = c) ---
  z - (z^3 - 1) / (3*z^2)

  --- Nova (z³ − 1 with c offset; set z₀ seed = 1) ---
  z - (z^3 - 1) / (3*z^2) + c

  --- Magnet-1 (Lord Kelvin) ---
  ((z*z + c - 1) / (2*z + c - 2))^2

  --- Exponential map ---
  exp(z) + c

  --- Sine map (Devaney; set z₀ seed = c) ---
  c * sin(z)

  --- Lambda map (set z₀ seed = 0.5) ---
  c * z * (1 - z)

  --- Celtic Mandelbrot ---
  abs(re(z*z)) + im(z*z)*i + c

  --- Buffalo ---
  let w = fold(z) in w*w - w + c

  --- Branch on the iterate ---
  if norm(z) < 1 then z*z + c else z*z - c

  --- Time-varying twist ---
  var k = 0.005 * n;
  (cos(k) + sin(k)*i) * z*z + c

  --- Heron step (set z₀ seed = c) ---
  0.5 * (z + c / z)

=== Save / Load ===

  Save…    Name + persist.  The CalcGen toggle, Escape r, z₀ seed,
           Bail if and Colour interior are saved with it.
  Import…  Add equations from a JSON file (one or many).
  Delete   Remove the selected saved equation.
  Combo    Pick a saved equation to load it (and its settings).

Saved equations live in %APPDATA%\FracturingFog\userequations.json.
Regions refer to them by name, so editing one updates every region.

=== Limitations ===

  • The interpreter is double precision — useful to about 1e13.
    Turn on CalcGen and Compile & Load for deep zoom.
  • CalcGen ignores the z₀ seed and Bail if (#860).
  • An equation whose value is real on some pixels and complex on
    others can't be compiled; wrap it in re(), im(), abs() or norm().
";

        public const string MathUserBulbText =
@"=== User Bulb (3D) — Overview ===

The User Bulb engine is the 3D analogue of User Equation.  You
write ONE STEP of the iteration — the next z — in the bulb
language (the equation language with 3D values, see below).  A
safe interpreter runs it (no file, network or .NET access), and
renders the resulting escape-time set as a real 3D surface
using Mandelbulb-style raymarching with an analytic distance
estimator (for recognized closed-form maps) or a numerical
Jacobian distance estimator (for arbitrary maps).

Where User Equation produces flat 2D images of complex maps, User
Bulb produces shaded 3D bulbs, foam, sponges, and shells in
genuine three-space — lit by up to three directional lights,
shaded by surface normals, drawn from any camera angle, optionally
animated by a global time parameter t, and exportable to OBJ
mesh files for printing or external 3D editors.

Open via:  Fractal Type → ""User Bulb (3D)""
           Floating Menu → gear icon (when User Bulb is active)

The dialog is modeless and re-compiles shortly after the last
keystroke.  Errors show in yellow below the editor, with the bad
name selected; when the message suggests a fix, Apply fix
(Ctrl+.) applies it — in the source box or in a chain step.  Camera,
lighting, iteration count, epsilon, bailout, Jacobian h, params,
animation, color driver, lighting weights, and all view knobs
update without recompiling — only changes to the source body,
algebra mode, chain steps, or param names trigger a fresh compile.

=== Dialog Layout (two columns) ===

Left column (top → bottom):
  Hint line + Saved row (combo + Save / Delete / Import / Export
    / Promote-to-fractal-list)
  Editor (the step equation)
  Error line + Apply fix (Ctrl+.)
  Camera        Distance, Theta°, Phi°, Light θ°/φ°, Reset cam
  Render        Iterations, Bailout, Max steps, Epsilon, Jac h,
                Cull r, DE mode, Backend, Algebra, Slice W
  Params        Named scalar sliders (Add / Remove)
  Animation     ▶/■ Play, Speed, t
  Julia mode    Enable + c.X/c.Y/c.Z/c.W

Right column (top → bottom):
  Color driver  Combo + trap XYZ + iter axis
  Lighting      L1 / L2 / L3 intensity, AO samples, fog density
  View          FOV°, Clip+Y, Supersample (1x/2x/4x)
  Chain         Named-output step list (+ Step / X)
  Export        Export mesh (OBJ)…

=== How 3D Escape-Time Works ===

The 2D Mandelbrot maps each pixel to a complex c, runs
zₙ₊₁ = zₙ² + c starting from z₀ = 0, and asks ""does |z| stay
bounded?""  Plot escape time → fractal.

3D escape-time generalises by replacing the complex value with a
3D vector v ∈ ℝ³.  Each point p in 3-space becomes c.  We iterate
some user-defined map vₙ₊₁ = f(vₙ, c, n) and again ask ""does
|v| stay bounded?""  The SET of c-points that stay bounded is now
a 3D solid, not a 2D plane.

Rendering this solid as an image requires raymarching:

  1. The CAMERA sits on a sphere around the origin (controlled
     by Distance, Theta, Phi).  Each pixel emits a RAY.
  2. A bounding-sphere clip skips rays that miss the cull radius.
  3. A cone-march tile prepass estimates a per-tile entry t-hint
     so per-pixel marches can start past empty space.
  4. The ray marches forward one ""safe step"" at a time.  Step
     length = DISTANCE ESTIMATE (DE) at the current point — a
     lower bound on how far we can move without hitting surface.
  5. When DE < ε, declare a HIT and record the surface position.
  6. SHADE the hit: surface normal (forward-difference of DE),
     three directional lights (L1/L2/L3 intensities), optional
     screen-space AO, optional fog mix to sky color.
  7. COLOR the pixel through the active IColorMap, modulated by
     the active Color Driver (StepDepth / OrbitTrap / etc.).

The user only writes f.  Everything else — raymarching, DE,
normals, lighting, color, AO, fog, supersampling — is handled
by the engine.

" + EquationGrammarText + @"
=== The 3D language (what differs from 2D) ===

Values are real numbers, 3-vectors (Vec3 mode) or quaternions
(Quat mode).  z and c have the mode's type; n, t and params are
real.

  Variables     z  c  n  t  pi  e,  plus one real per Params row
                (by name), and each earlier chain step's output
                name.  No prev / iter, no i (no complex numbers).
  Components    v.x  v.y  v.z   (and q.w in Quat mode)
  Build         vec(x, y, z)          qvec(x, y, z, w)

  Arithmetic    vec ± vec componentwise; vec * vec componentwise
                (Hadamard); vec * real, vec / real broadcast.
                In Quat mode q * q is the Hamilton product.
  Power  ^      vec ^ real is the TRIPLEX (Mandelbulb) power —
                z^8 + c is the canonical bulb.  quat ^ real is the
                quaternion power.  -z^2 means -(z^2).

  abs(v)        COMPONENTWISE on vectors and quaternions — the fold
                idiom (let v = abs(z) in …).  |x| on reals.
  length(v)     the magnitude |v|
  norm(v)       the squared length dot(v, v) (x² on reals)

  Vector        dot(a, b)  cross(a, b)  normalize(v)
                triplex(v, p)          triplex power (same as ^)
                rot(v, axis, angle)    Rodrigues rotation
                boxfold(v, limit)      Mandelbox box fold
                spherefold(v, rmin, rmax)   Mandelbox sphere fold
                absx(v) absy(v) absz(v)     one-axis folds
                mod(v, period)         tile space per axis
  Scalar        smin(a, b, k)  min  max  clamp  floor  sign  pow
  Elementwise   sin cos tan sinh cosh tanh exp log sqrt
                (on reals and vectors; not on quaternions)
  Quaternion    qmul qpow qconj qinv  qexp qlog qsqrt
                qsin qcos qtan qsinh qcosh qtanh
                qasin qacos qatan qasinh qacosh qatanh
                qcsc qsec qcot qcsch qsech qcoth

=== Algebra Mode (Vec3 vs Quat) ===

  Vec3 (3D)    Default.  z and c are Vec3 (3 components).
               Slice W ignored.  Fastest.

  Quat (4D)    z and c are Quat (4 components).  c.W comes from
               the Slice W slider, NOT from the pixel.
               Julia mode's c.W field becomes active.
               Each algebra change triggers a recompile.

=== Iteration Loop ===

The engine ALWAYS starts the orbit at z = Zero (Vec3 or Quat).
At each sample point P (a position in 3-space along the camera
ray):

        c = (P.x, P.y, P.z)     // Vec3 mode
        c = (P.x, P.y, P.z, W)  // Quat mode (W = Slice W slider)
        z = Zero
        for n in 0 … Iterations:
            if |z| > Bailout: break
            z = Step(z, c, n, p)

In Julia mode, `c` is replaced with the fixed user-supplied
JuliaC for every iteration.  z is still seeded to Zero.

=== Distance Estimation — DE mode ===

Four DE modes, chosen from the ""DE mode"" combo:

  Auto       Engine attempts to detect a closed-form ""power-N
             triplex"" pattern in your source.  If matched, uses
             the fast Hubbard-Douady single-trajectory analytic
             DE.  Otherwise falls back to Numerical.  Default.

  Analytic   Forces Hubbard-Douady analytic DE:
                 DE(p) = 0.5 · ln(|z|) · |z| / dr
             where dr is updated analytically as
                 dr = p · r^(p−1) · dr + 1.
             ~4× faster than Numerical but only valid for triplex
             power maps.  Using it on the wrong map gives WRONG
             surfaces.  Pick this for vanilla Mandelbulb /
             z^N + c equations.

  Numerical  Always use the numerical Jacobian DE.  Four trajectories
             run in lockstep:
                 z_base   c
                 z_px     c + h·êx
                 z_py     c + h·êy
                 z_pz     c + h·êz
             dr = max(|z_px−z_base|, |z_py−z_base|, |z_pz−z_base|) / h.
             Works for ANY map; ~4× slower than Analytic.

  Non-       For maps whose orbit NEVER escapes — pseudo-Kleinian
  escaping   / lattice / double-periodic maps such as the Amoser
             complex-sine.  See the dedicated section below.  The
             escape-time estimators (Auto/Analytic/Numerical) all
             assume the orbit eventually leaves the bailout sphere;
             a non-escaping orbit sits inside it forever, so those
             modes read a nonsense DE and the surface is unreachable
             by tuning.  This mode swaps the escape test for a
             stability clamp and a running-derivative bound.

The ""Jac h"" slider sets the finite-difference perturbation:
1e-4 default.  Too small → cancellation noise; too large →
soft-edged surface.  (Non-escaping mode does not use Jac h when a
DE body is supplied; it uses your analytic dr instead.)

=== Non-Escaping DE — maps that never escape ===

Some of the most interesting 3D maps are NOT escape-time. Their
orbit is trapped forever inside a bounded slab (pseudo-Kleinian,
lattice, Kleinian-limit, the Amoser complex-sine
    z = ( sin(z.x)·cosh(z.y),
          cos(z.x)·cos(z.z)·sinh(z.y),
          sin(z.z)·cosh(z.y) ) + c ).
Because |z| never crosses the bailout, the escape-time DE modes
produce a blank or garbage surface no matter how you tune
Iterations / Bailout / Max steps / Epsilon.

Non-escaping DE replaces the escape test with a STABILITY CLAMP and
accumulates a distance bound from the running derivative dr:

    de = ∞
    dr = 1
    for n in 0 … Iterations:
        z  = Step(z, c, n, p)           // your step map
        dr = DrBody(z, c, n, dr, …)     // your dr recurrence (or auto)
        de = min(de, 1 / dr)            // tightest bound so far
        if |z.axis| > StabilityLimit:   // clamp, not escape
            break
    return DEMultiplier · de

  Settings (visible only when DE mode = Non-escaping):

    DEMultiplier      Pure gain on the final distance.  0.5 is the
                      usual starting point (matches the reference
                      Fragmentarium renders).  Lower = the raymarch
                      takes smaller steps → crisper but slower; too
                      high → the surface is over-stepped and holes
                      appear.  Range ~0.1 … 2.0.

    Stability axis    Which component (x / y / z) the clamp watches.
                      Pick the axis the map grows along — for the
                      Amoser map that is y (the cosh / sinh axis).

    Stability limit   The clamp threshold on that axis.  When the
                      component exceeds it the inner loop stops for
                      that ray.  ~8 for the Amoser map.  Larger =
                      more interior detail but slower and noisier;
                      smaller = smoother but eats fine structure.

=== Non-Escaping DE — the DE body editor ===

The ""DE body"" editor (shown in Non-escaping mode) lets you write
the running-derivative recurrence yourself, in the same safe DSL as
the step.  You write ONE scalar expression that returns the NEXT dr.
The engine owns the rest: it computes de = min(de, 1/dr) each
iteration and multiplies the result by DEMultiplier.  You never
write the min or the multiplier — only the dr growth.

  In scope inside the DE body:
    z          the PRE-step position this iteration (Vec3)
    c          the constant / Julia c (Vec3)
    n          iteration index (scalar)
    t          animation time (scalar)
    <params>   every named param from the Params bank
    dr         the PREVIOUS iteration's dr (scalar)
    de         the PREVIOUS iteration's de (scalar)

  Rules of thumb:
    • The body is SCALAR — return a number, not a vec.
    • The classic shape is  dr = stretch · dr + offset  where
      ""stretch"" is the local magnitude of the map's derivative and
      ""offset"" is usually 1 (the seed ray's own length).
    • If the body fails to parse it does NOT break your step: the
      engine falls back to a two-trajectory numerical tangent and
      surfaces the error text, so the render still appears.

  Example 1 — constant growth (teaching example):
      dr*2
    Doubles dr every iteration regardless of z.  With N iterations
    de = 1/2^N.  Useless as a real fractal, handy to see the
    mechanism: bigger dr → smaller de → tighter surface.

  Example 2 — Amoser complex-sine stretch (the shipped preset):
      let ez = exp(z.z) in
      let s  = max(StretchMax, StretchScale*sqrt(0.5*(ez*ez + 1/(ez*ez)))) in
      drScale*s*dr + drOffset
    The map's dominant stretch is the cosh growth along the
    sinh/cosh axis; sqrt(0.5·(e^2z + e^-2z)) = sqrt(cosh(2·z.z)) is
    that magnitude.  StretchMax floors it so the bound never
    collapses to zero; StretchScale trims it; drScale / drOffset are
    the classic ""stretch · dr + offset"" gain and seed.  Params:
    StretchScale 0.81, StretchMax 1.04, drScale 1.0, drOffset 1.0.

  Example 3 — length-based stretch (generic non-escaping starter):
      max(1.0, length(z)) * dr + 1.0
    A crude but often-usable bound for an unknown non-escaping map:
    grow dr by how far the point has travelled, floored at 1 so it
    is monotone.  Tune the floor and add a scale param once you can
    see the surface.

  Example 4 — folded / periodic map with a per-axis stretch:
      let g = max(abs(cos(z.x)), abs(cosh(z.y))) in
      Scale*g*dr + 1.0
    When the step is a product of trig / hyperbolic factors, the
    stretch is the largest per-factor magnitude.  Expose Scale as a
    param so you can dial the bound tightness live.

  Leaving the DE body BLANK is valid: Non-escaping mode then uses an
  automatic two-trajectory numerical tangent for dr.  It works for
  any non-escaping map but is softer and ~2× the cost of a good
  analytic body — write the body once you know the map's stretch.

  One-click: the ""Amoser complex-sine (non-escaping DE)"" saved
  preset wires the step, the Example-2 dr body, the four params, and
  the Non-escaping settings together.  Load it, then edit from there.

=== Getting a crisp render AND a detailed mesh export ===

The DE quality drives BOTH the on-screen render and the exported
mesh.  The numerical Jacobian DE is smooth enough to raymarch but
is an APPROXIMATION — it reads as ""rough"", mottled, or shows faint
interference rings on screen, and marching-cubes export turns that
into a soft blob.  The analytic DE is exact and gives a crisp
surface + a faithful mesh.  To get the best result:

  1. DE mode → Analytic (or Auto).  For recognised power maps
     (z^N + c, triplex(z, N) + c, the explicit square triplex,
     and the quaternion square z*z + c / qmul(z, z) + c in
     Quat mode) this uses the exact running-derivative DE.  Numerical
     mode always meshes softer — use it only for maps with no
     closed form.

  2. Quaternion fractals → Axis Mode = Quat + Julia mode on.  The
     ""Quaternion Julia"" saved preset now sets these for you.  A
     quaternion square (z*z + c) in Quat mode meshes as cleanly as
     the built-in Quaternion Julia fractal type.

  3. KIFS folds (Menger, Sierpinski, Mandelbox, kaleidoscopic) →
     set KIFS Scale to the fold's per-iteration scale (3 for Menger,
     2 for Sierpinski / Mandelbox).  Inserting a fold primitive or
     loading a fold hybrid now sets this automatically.  Without it
     the numerical DE cannot cross the fold discontinuities and the
     export produces ZERO triangles.

  4. Raise Iterations for geometry.  The render default (8) is low;
     native quaternion types use 11–14.  For export try 14–16.

  5. Export knobs (by the Export button): Grid = marching-cubes
     resolution (96 = native parity, 128 for fine detail, cost ~N³);
     Range = object-space half-extent, must ENCLOSE the fractal (too
     small clips it; a 0-triangle export usually means Range is off
     or a fold needs KIFS Scale).  Grid + Range + the panel's
     Iterations / Jac h all persist with a saved bulb.

  Reality check: User Bulb is a general interpreter — for an
  ARBITRARY novel equation it can only estimate the DE numerically,
  so its mesh is inherently softer than a hand-written analytic
  calculator.  For a faithful mesh of a KNOWN fractal (Quaternion
  Julia / Mandelbrot, Mandelbulb, Mandelbox, KIFS) prefer that
  concrete Fractal Type, whose exact DE meshes at full detail.

=== Backend (CPU vs GPU) ===

  CPU                   The interpreter (compiled to an expression
                        tree) via Parallel.For over rows.  Always
                        available; correct for every map.

  GPU (experimental)    ILGPU kernel.  z^N + c uses a pre-baked
                        triplex kernel; other equations are emitted
                        and compiled for the GPU (a product or
                        quotient mixing a vector and a quaternion,
                        and Vec3 Julia / numerical-DE renders, stay
                        on the CPU, silently).  5–20×
                        faster on stock z^N + c renders.

=== Cull Radius ===

Each ray is clipped against a sphere of radius ""Cull r"" centered
at origin BEFORE marching.  Rays that miss the sphere render the
sky color directly — zero march cost.

  • Default 2.0 fits the canonical Mandelbulb.
  • Mandelbox / large folds need 4–8.
  • Aggressive cull (1.5) speeds exploration of small bulbs.

=== Camera, Lighting, Render Knobs ===

  Camera (sphere around origin):
    Distance      orbit radius.  Smaller = closer.
    Theta°        azimuth (around Y).
    Phi°          elevation (1..179°).  90° = equator.
    Reset cam     Restore the canonical default view.

  Lighting (KEY direction):
    Light θ°, φ°  Direction of the primary light L1.

  Render:
    Iterations    DE inner-loop count.  4–12 sane range.
    Max steps     Raymarch step cap.  64–192 typical.
    Bailout       |z| escape threshold.  2.0–4.0 standard.
    Epsilon       Surface hit threshold.  0.0005–0.005.
    Jac h         Numerical-DE perturbation.  1e-4 default.
    Cull r        Bounding-sphere radius.

  Pan / Zoom (top-level toolbar):
    CenterX/Y     Screen-space pan in NDC units (drag canvas).
    Zoom          Scales Distance / Zoom → cam moves in/out.

Mouse:
    Mouse wheel             Zoom (smaller/larger Distance).
    Left-click drag         Pan in screen space.
    Right-click drag X      Orbit Theta (spin horizontally).
    Right-click drag Y      Orbit Phi.  Y is INVERTED in
                            User Bulb so drag-down → camera tips
                            UP, matching standard 3D editors.
    Middle-drag             Marquee zoom.  Hold the middle button
                            and drag a box, release to zoom into
                            it — the same outline-to-zoom as the
                            2D right-drag, on a different button so
                            right-drag stays camera orbit.  Esc
                            cancels before release.

=== Params Bank (named scalar sliders) ===

Add arbitrary scalar params from the dialog's ""Params"" panel.
Each row gives Name, Value, Min, Max, and an X (remove) button.

In source code, reference a param BY NAME — the wrapper exposes
each as a real variable with its name:

  Params:  k = 2.0, twist = 0.3, freq = 4.0

  Source:
    z^k + c + sin(z * freq) * twist

Changing a param VALUE re-renders only (no recompile).  Changing
a param NAME, adding, or removing one triggers a recompile.

=== Animation (global t) ===

The Animation bar plays a continuously increasing clock t (units
of seconds × Speed).  The ""t"" numeric directly drives a global
double named `t` available in your source.  Use it to morph or
spin maps:

    z^(4 + 2*sin(t)) + c

▶ starts the timer (~30 Hz updates).  ■ pauses.  Speed slider
multiplies the per-tick delta.  Setting t manually fires a render.

=== Julia Mode ===

Tick ""Enable (fix c)"" in the Julia group to swap the per-pixel
`c` with a single user-supplied constant for EVERY iteration.

  c.X / c.Y / c.Z   Vec3 mode: the constant in 3-space.
  c.W               Quat mode: 4th component of the constant.
                    Disabled when Algebra = Vec3.

Pixel coordinate still drives the raymarch position; only the
iteration's `c` is overridden.  This produces a 3D Julia set for
the chosen formula.

=== Color Drivers ===

Selects what the IColorMap receives as input per pixel.

  StepDepth         Number of march steps before hit (default).
                    Highlights silhouette depth.

  OrbitTrap         Min distance from orbit to the user-set trap
                    point (tx, ty, tz).  Reveals tendrils that
                    pass close to the trap.  ""tx/ty/tz"" numerics
                    set the trap location.

  EscapeAngle       Atan2 of the escape vector projected onto
                    user-chosen axis.  Highlights spiral structure.

  FinalMagnitude    Log of |z| at escape.  Smooth gradient
                    across the surface.

  IterComponent     Specific axis of the final iterate
                    (X / Y / Z chosen by the ""axis"" combo).
                    Anisotropic color across the bulb.

  Normal            Surface normal mapped to RGB.  Pure shading
                    debug.  No palette involvement.

=== Lighting (3-light + AO + fog) ===

The shader sums three directional-light contributions, an
optional ambient-occlusion term, and a fog mix to sky color.

  L1 intensity     0..N.  Primary key light (uses dialog's
                   Light θ°/φ°).
  L2 intensity     Secondary fill light (fixed offset).
  L3 intensity     Tertiary rim light (back-light).
  AO               Cone-march AO sample count.  0 disables.
                   4–8 reveals concavities.
  Fog              Beer's-law density.  0 disables; 0.2–0.5
                   gives atmospheric depth.

=== View (FOV / clip / supersample) ===

  FOV°             Perspective field of view.  60° default.
                   < 30° = telephoto (flat); > 90° = fisheye.
  Clip+Y           When ticked, half-space clip removes geometry
                   above the y=0 plane.  Useful for cross-section
                   views of solid bulbs.
  SS               Supersample: 1x, 2x, or 4x grid OGSS.
                   4x = render at 4× linear resolution and downsample.
                   Use for finished frames; SLOW.

=== Chain (multi-step, named outputs) ===

When the Chain panel has ≥ 1 step, the single-source editor is
IGNORED.  Each chain step has:

  Output name    Identifier added as a local Vec3 (or Quat)
                 available to subsequent steps and to the
                 ""Output"" expression of the chain.
  Source         A step equation (same language as the single
                 editor) that may use earlier steps' names.

The chain runs sequentially per iteration; the LAST step's
output becomes the new z.  Earlier steps' named outputs are
visible to later steps:

  Step 1   name = pre
           source = rot(z, vec(0, 1, 0), t)

  Step 2   name = sq
           source = pre^8 + c

Iteration 0: z = Zero → step1 makes `pre` from Zero rotated;
step2 makes `sq` from pre^8 + c.  z ← sq.

To revert to the single-editor flow, delete every chain row.

=== Save / Load and Promote ===

Saved bulbs persist to
    %APPDATA%\FracturingFog\userbulbs.json

  Save…       Stores the current editor text under the typed
              name (replaces an existing entry of the same name).
  Delete      Removes the selected saved entry.
  Import…     Reads a single-entry .fbulb JSON file.  Renames
              on name collision.
  Export…     Writes the selected entry to a .fbulb JSON file.
  Promote to fractal list
              When ticked, the saved bulb appears in the main
              Fractal-Type dropdown as a first-class option.

The store ships 10 default presets seeded on first run.  Delete,
edit, and re-save freely — defaults are not protected.

=== Mesh Export (OBJ) ===

Click ""Export mesh (OBJ)…"" to sample the DE field on a uniform
N³ grid inside a cube of side 2·Range centered on the origin.
Each grid cell with a surface crossing emits a voxel cube of
triangles.  Output is ASCII OBJ.

  Grid N      8 … 256.  N=64 ≈ 32k voxels, fast; N=128 ≈ 256k,
              slow (10s+).
  Range       Half-extent of the sample cube.  2.0 fits most
              canonical bulbs.

The result is BLOCKY (voxel cubes, not interpolated triangles).
Adequate for 3D printing or external smoothing.  Marching-cubes
with the 256-entry triangulation table is a follow-up.

=== Quick Reference ===

  The language:   see ""The equation language"" and ""The 3D
                  language"" above.
  Globals:        t (animation clock), one real per Params row.
  Statements:     var v = boxfold(z, 1.0);
                  if (length(v) < 0.5) v = v * 4.0;
                  return v * 2.0 + c;

=== Pitfalls ===

  • NO-ESCAPE MAPS look BLANK.  Sin-only formulas or hyperbolic
    formulas that orbit forever never escape.  DE returns a flat
    value every step; the ray never converges to a surface.
    Raise Bailout substantially or rephrase so |z| grows.

  • Z₀ = 0 FIXED POINTS.  Any f where f(0, 0) = 0 produces an
    all-in-set image when c = 0 is at the center.  Enable Julia
    mode (fixes c) or pre-rotate / pre-translate in source.

  • EXPLODING MAPS.  z^z, double-exp, and similar grow to ∞
    within one or two iterations.  Drop Iterations to 2–4, raise
    Bailout to 1e6 — or rescale inputs.

  • NaN/INF.  log / sqrt of negatives and division by zero
    propagate as NaN through vector arithmetic silently.  Guard
    with + 1e-6 in denominators and max(r, 1e-12) before log.

  • DE MODE MISMATCH.  Analytic DE on a non-triplex map gives
    WRONG surfaces.  Use Auto (the detector will fall back to
    Numerical for unknown shapes) or pick Numerical explicitly.

  • TIGHT JAC h on DISCONTINUOUS MAPS.  Mandelbox-style folds
    have piecewise derivatives.  Raise Jac h to 1e-3 for folds.

  • CULL R TOO SMALL.  If your fractal extends beyond Cull r, the
    bounding sphere clips silhouettes.  Mandelbox needs 4–8;
    canonical bulbs are happy with 2.

  • GPU BACKEND FALLBACK.  A render the GPU route can't take
    (see Backend) falls back to CPU silently.  If GPU was
    expected and you don't see a speedup, that's why.

  • PERF.  The interpreter is compiled to an expression tree;
    transcendental calls dominate.  Prefer x*x*x over pow(x, 3)
    and name repeated sub-expressions with let / var.

=== Troubleshooting ===

Black screen, no shape:
  • Status line green ✓ Compiled?  Yellow = a parse error.
  • Bump Bailout to 16 — your map may not escape at 4.
  • Drop Iterations to 4 — may be hitting NaN partway.
  • Spin Camera Theta — initial view may face empty side.
  • Raise Cull r — fractal may sit outside the bounding sphere.

Speckled normals / noisy shading:
  • Raise Jac h from 1e-4 → 1e-3.
  • Drop Epsilon to 0.0005.

Bulb looks ""melted"" / soft edges:
  • Lower Epsilon to 0.0008.
  • Raise Max steps to 192.

Banding / tile boundaries visible:
  • DE mode may be wrong.  Force Numerical and re-render.
  • Drop the temporal cache by toggling re-compile (edit source
    and revert) to flush stale tiles.

Render is unbearably slow:
  • Drop Iterations to 4, Max steps to 48 for exploration.
  • Switch Backend → GPU (z^N + c gets the fastest kernel).
  • Set SS back to 1x.  Resize window smaller.

=== Limitations ===

  • GPU: a product or quotient mixing a vector and a quaternion,
    a ternary whose branches have different kinds, and Vec3 Julia /
    numerical-DE renders run on the CPU.
  • Mesh exporter is voxel-cube (blocky).  Real marching cubes
    with the 256-entry triangulation table is a follow-up.
  • Numerical DE uses max column norm — a conservative spectral-
    radius proxy.  Some maps render with surfaces slightly
    inside the true set boundary.
  • Region save/recall round-trips the saved bulb NAME, not the
    raw source.  Save your edits to the library first.

=== Examples — Starting Points ===

Each Source block is a complete step equation.  Paste it into the editor
(or save with the Saved combo for re-use).  Each example lists a
suggested CONFIG block — adjust Distance / Cull / etc. from the
canonical defaults.

────────────────────────────────────────────────────────────────
  1. SQUARE TRIPLEX  (the default — fast 3D Mandelbrot analogue)
────────────────────────────────────────────────────────────────
Source:
  vec(z.x*z.x - z.y*z.y - z.z*z.z,
      2*z.x*z.y,
      2*z.x*z.z) + c

Config:
  Algebra Vec3 · Backend CPU (or GPU)
  DE mode Auto (detects analytic pattern)
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0015 · Jac h 1e-4 · Cull r 2.0
  Camera Distance 3 · Theta 45° · Phi 63°

────────────────────────────────────────────────────────────────
  2. MANDELBULB p=8  (canonical Mandelbulb: triplex power ^)
────────────────────────────────────────────────────────────────
Source:
  z^8 + c

Config:
  Algebra Vec3 · Backend GPU (analytic kernel)
  DE mode Auto (analytic Hubbard-Douady)
  Iterations 8 · Bailout 2 · Max steps 128
  Epsilon 0.0008 · Cull r 1.5
  Camera Distance 2.8 · Phi 65°

────────────────────────────────────────────────────────────────
  3. POWER-12 RIDGED BULB  (deeper folds, more spines)
────────────────────────────────────────────────────────────────
Source:
  z^12 + c

Config:
  Algebra Vec3 · Backend GPU · DE mode Auto
  Iterations 10 · Bailout 2 · Max steps 160
  Epsilon 0.0006 · Cull r 1.5
  Lighting L1 1.0  L2 0.4  L3 0.3  AO 4  Fog 0.08

────────────────────────────────────────────────────────────────
  4. ANIMATED BREATHING BULB  (uses global t)
────────────────────────────────────────────────────────────────
Source:
  z^(4 + 2*sin(t)) + c

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 6 · Bailout 4 · Max steps 64
  Epsilon 0.002 · Jac h 1e-4 · Cull r 1.5
  Animation ▶ Play  Speed 0.5
  SuperSample 1 (turn off SS for live playback)
  Power oscillates between 2 and 6 — bulb pulses on the beat.

  Notes:
  · Each tick is a full CPU raymarch. Analytic + GPU path requires
    the power to be a literal numeric constant (pattern-detected), so
    animated power always falls to numerical Jacobian.
  · Drop Iterations / Max steps / window size to keep frame time
    under ~2 s. At Iter 6 / Steps 64 / 600x400 expect ~1-3 s/frame
    on midrange CPUs. Status bar showing ""calculating"" between
    frames is expected.
  · Temporal cache now keys on t — earlier builds would freeze the
    first frame and skip subsequent ticks. Rebuild if you see that.

────────────────────────────────────────────────────────────────
  5. QUARTIC + SIN PERTURBATION  (power escape + trig folds)
────────────────────────────────────────────────────────────────
Source:
  z^4 + sin(z) * 0.5 + c

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Jac h 1e-4 · Cull r 1.5
  Camera Distance 2.8 · Phi 65°
  Color driver OrbitTrap  tx 0  ty 0  tz 0  (highlights folds)

  Why not plain sin(z)*k + c: pure sin is bounded (|sin|≤1)
  so |z| stays bounded → never crosses bailout → DE meaningless
  → blank or filled-sphere render. Same for Cos. Trig that GROWS
  (sinh / cosh / exp) can also explode to Inf in
  the numerical Jacobian and stall the raymarch. The reliable
  pattern is a power term (escapes) + a bounded trig perturbation
  (adds visual texture). For a pure bounded-trig look, switch
  Color driver to OrbitTrap or FinalMagnitude — escape-time has
  no meaning for non-escaping maps.

────────────────────────────────────────────────────────────────
  6. ABS-BULB p=8  (Burning-Ship-style fold before squaring)
────────────────────────────────────────────────────────────────
Source:
  abs(z)^8 + c

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 2 · Max steps 128
  Epsilon 0.001 · Cull r 1.5
  Distinctive flat-top + sharp ridge silhouette.

────────────────────────────────────────────────────────────────
  7. MANDELBOX  (Tglad box fold + sphere fold + scale)
────────────────────────────────────────────────────────────────
Source:
  var v = spherefold(boxfold(z, 1.0), 0.5, 1.0);
  v * 2.0 + c

Config:
  Algebra Vec3 · Backend CPU · DE mode Numerical
  Iterations 12 · Bailout 16 · Max steps 192
  Epsilon 0.0006 · Jac h 1e-3 · Cull r 6.0
  Camera Distance 8 · Phi 75°
  Lighting AO 6 · Fog 0.15  (cavities benefit from AO)
  Variants: try scale = −1.5 (negative Mandelbox).

────────────────────────────────────────────────────────────────
  8. QUATERNION JULIA  (4D quaternion squaring, sliced)
────────────────────────────────────────────────────────────────
Source (Quat algebra):
  z * z + c

Config:
  Algebra Quat (4D) · Backend CPU · DE mode Numerical
  Slice W 0.3  (try 0, 0.1, 0.5, −0.4)
  Iterations 10 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Cull r 2.0
  Julia mode ON · c = (−0.2, 0.4, −0.4, 0.0)
  Each Slice W is a different 3D slice of the same 4D set.

────────────────────────────────────────────────────────────────
  9. VEC3 JULIA  (3D triplex with fixed c)
────────────────────────────────────────────────────────────────
Source:
  vec(z.x*z.x - z.y*z.y - z.z*z.z,
      2*z.x*z.y,
      2*z.x*z.z) + c

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 12 · Bailout 4 · Max steps 128
  Epsilon 0.001 · Cull r 2.5
  Julia mode ON · c = (0.30, 0.50, −0.20)
  Re-render and vary c.X/Y/Z to morph the dendrite topology.

────────────────────────────────────────────────────────────────
  10. ROTATED-TRIPLEX HELIX  (Rodrigues + animated t)
────────────────────────────────────────────────────────────────
Source:
  var sq = vec(z.x*z.x - z.y*z.y - z.z*z.z,
               2*z.x*z.y,
               2*z.x*z.z);
  rot(sq, vec(0, 1, 0), t * 0.3) + c

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0012 · Cull r 2.5
  Animation ▶ Play  Speed 0.4
  Bulb spins around Y; t-rotated input warps each iteration.

────────────────────────────────────────────────────────────────
  11. PERIODIC KALEIDO  (mod tiles a fractal across space)
────────────────────────────────────────────────────────────────
Source:
  var p = mod(z, 2.0);
  p^8 + c

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 6 · Bailout 4 · Max steps 192
  Epsilon 0.001 · Jac h 5e-4 · Cull r 5.0
  Camera Distance 6 · FOV 80°
  Creates infinite lattice of mini-bulbs.

────────────────────────────────────────────────────────────────
  12. CROSS-PRODUCT RIBBONS  (twisted square triplex)
────────────────────────────────────────────────────────────────
Source:
  var sq = vec(z.x*z.x - z.y*z.y - z.z*z.z,
               2*z.x*z.y,
               2*z.x*z.z);
  sq + c + cross(z, c) * 0.5

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.0012 · Cull r 3.0
  Color driver EscapeAngle  axis Y  (highlights spiral flow)

────────────────────────────────────────────────────────────────
  13. TWO-STEP CHAIN  (one bulb power feeding another)
────────────────────────────────────────────────────────────────
Use Chain (right column) with TWO steps:

  Step 1   name = a
           source = z^8 + c
  Step 2   name = b
           source = z^4 + c

  Single editor (overridden by chain — used here as scratch).

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 128
  Epsilon 0.0012 · Cull r 2.5
  Chain runs sequentially; LAST step's output becomes new z.
  Try swapping powers (8/3, 12/6) to morph between bulbs.

────────────────────────────────────────────────────────────────
  14. PARAMETRIC TWIST-BULB  (uses named param sliders)
────────────────────────────────────────────────────────────────
Params (Add three rows):
  p     Value 8.0   Min 2    Max 16
  k     Value 0.3   Min 0    Max 2
  freq  Value 4.0   Min 1    Max 20

Source:
  var v = z^p;
  var twist = sin(z * freq) * k;
  v + c + twist

Config:
  Algebra Vec3 · DE mode Numerical
  Iterations 8 · Bailout 4 · Max steps 96
  Epsilon 0.001 · Jac h 5e-4 · Cull r 2.5
  Drag p slider 4→12: morph between cubic and high-order bulb.
  Drag freq 2→16: add fine high-frequency surface detail.
  Drag k 0→1: blend from clean bulb to twisted surface.
";

        public const string MathSandboxText =
@"=== Sandbox — Overview ===

The Sandbox is the light editor for the SAME equation language as
the User Equation editor (#937): one expression, rendered live by
the safe interpreter.  It has no CalcGen toggle and no z₀ seed or
Bail if fields — use User Equation for those.  The interpreter has
no access to files, the network or .NET, so Sandbox equations are
safe to share, import from JSON, or paste from a web page.

Open via:  Fractal Type → ""Sandbox""  →  Params button
The editor re-renders shortly after each keystroke.  Errors show
in yellow below the editor.

Saved equations are persisted to:

    %APPDATA%\FracturingFog\sandboxequations.json

When a Region is saved while Fractal Type = Sandbox, the name of
the active saved equation is recorded with the Region.  Recalling
the Region looks the source back up by name, so editing a saved
equation propagates to every Region that references it.

=== Iteration Model ===

The runtime drives the standard escape-time loop:

        z = 0
        for n in 0 … MaxIterations:
            if |z|² ≥ 1024: break
            z = <your expression>

Bailout (radius 32), smoothing, and color mapping mirror the
Mandelbrot path.

" + EquationGrammarText + @"
=== Type Rules ===

The parser tracks two value kinds — Real and Complex — and
promotes Real → Complex automatically whenever a binary op mixes
them.  A few consequences:

  • Comparisons (< > <= >= == !=) require Real operands.  Wrap
    a complex value with abs(), re(), im(), or arg() to compare.
  • Logical operators read any value's magnitude — 0 is false,
    everything else is true.
  • The expression's final value is implicitly converted to
    Complex before being returned to the iterator.

=== Reserved Names ===

You may not rebind:  z, c, n, iter, prev, pi, e, i, let, in, if,
then, else.

User let-bindings get their own slot — shadow-rebinding an outer
let with the same name in an inner let is allowed and follows
lexical scope.

=== Save / Load ===

  Save…    Prompts for a name; persists current source under it.
           Re-saving an existing name replaces.
  Delete   Removes the currently-selected saved equation.
  Combo    Picking a saved entry loads it into the editor and
           recompiles immediately.

Region recall:  the Region store records the SandboxName field
on save.  On Region select, the shell asks the SandboxEquationStore
for the entry by name and loads its source.  If the saved entry
has been deleted, the Region falls back to whatever source was
last typed.

=== Examples — Drop-in Snippets ===

Each block below is a complete expression — paste it into the
editor and the renderer compiles + previews.  No trailing
semicolons; the expression IS the body.

  --- Mandelbrot ---
  z*z + c

  --- Mandelbrot using pow ---
  pow(z, 2) + c

  --- Multibrot (cubic) ---
  z^3 + c

  --- Multibrot (degree d, baked in) ---
  z^5 + c

  --- Tricorn / Mandelbar ---
  conj(z)^2 + c

  --- Burning Ship ---
  // Build w with absolute-valued real + imaginary parts.
  let w = abs(re(z)) + abs(im(z)) * i in w*w + c

  --- Phoenix (uses the previous iterate) ---
  z*z + c + 0.56667 * prev

  --- z² + c with sine perturbation ---
  z*z + c + 0.1 * sin(z)

  --- Exponential map ---
  exp(z) + c

  --- Sine map ---
  // sin(0) = 0, so we need a non-zero seed on iteration 0.
  // Ternary swaps in c whenever n == 0.
  c * sin(n == 0 ? c : z)

  --- Cosine map ---
  c * cos(n == 0 ? c : z)

  --- Lambda / logistic map ---
  // Critical point z = 0.5 is the canonical start.
  let z0 = (n == 0 ? 0.5 : z) in c * z0 * (1 - z0)

  --- Heron iteration (Newton for √c) ---
  // Skip the c/0 singularity by seeding z = c on n == 0.
  let z0 = (n == 0 ? c : z) in 0.5 * (z0 + c / z0)

  --- Newton (z³ − 1) ---
  let z0 = (n == 0 ? c : z) in
    let z2 = z0*z0 in
      z0 - (z0*z2 - 1) / (3 * z2)

  --- Nova (z³ − 1 with c offset) ---
  let z0 = (n == 0 ? 1 : z) in
    let z2 = z0*z0 in
      z0 - (z0*z2 - 1) / (3 * z2) + c

  --- Magnet-1 (Lord Kelvin) ---
  let num = z*z + c - 1 in
    let den = 2*z + c - 2 in
      (num / den) ^ 2

  --- Time-varying twist on z² + c ---
  let k = 0.005 * n in
    (cos(k) + sin(k) * i) * z*z + c

  --- Celtic Mandelbrot ---
  // |Re(z²)| + i * Im(z²), then + c.
  let zr2 = re(z)*re(z) - im(z)*im(z) in
    let zi2 = 2 * re(z) * im(z) in
      abs(zr2) + zi2 * i + c

  --- Buffalo fractal ---
  let w = abs(re(z)) + abs(im(z)) * i in w*w - w + c

  --- Bird-of-prey (cubic Burning Ship) ---
  let w = abs(re(z)) + abs(im(z)) * i in w^3 + c

  --- Conditional kernel switch by n ---
  // Run z² + c for the first 8 iterations, then z³ + c.
  n < 8 ? z*z + c : z^3 + c

  --- Polar transform on c ---
  // Treat c in polar form, square the radius, rotate by phase.
  let r = abs(c) in
    let a = arg(c) in
      (r*r) * (cos(2*a) + sin(2*a) * i) + z*z

  --- Spiral perturbation ---
  z*z + c + 0.05 * (cos(n * 0.1) + sin(n * 0.1) * i)

  --- Mix two maps by n ---
  // Linear blend between z² + c and exp(z) + c over n.
  let t = n / 64 in
    (1 - t) * (z*z + c) + t * (exp(z) + c)

  --- Smooth ""abs"" via sqrt(z*conj(z)) ---
  // Just a demonstration of conj/sqrt; equivalent to abs(z).
  sqrt(z * conj(z)) + c

  --- Compare-driven branch ---
  // Diverge faster for pixels far from origin.
  abs(c) > 0.5 ? z*z + 2*c : z*z + c

  --- Squared-magnitude test (no square root) ---
  if norm(z) < 1 then z*z + c else z*z - c

=== Performance Notes ===

  • Pure-managed interpreter — no SIMD, no JIT-emitted IL.  The
    same interpreter renders User Equation; for speed and deep zoom
    use User Equation's CalcGen toggle.
  • Each render thread allocates one environment array once
    (slots for z, c, n + every let-binding) and reuses it for
    every pixel.  No per-pixel allocation in the hot loop.
  • Deep let-binding chains pay a small recursive-eval cost —
    flatten where you can.
  • Real-only arithmetic stays in scalar mode internally and is
    cheaper than Complex arithmetic.  Use re()/im() projections
    when only a scalar is needed.

=== Safety Model ===

The DSL parser only emits AST nodes for the operators, functions,
and identifiers documented here.  It cannot:

  • Open, read, write, or enumerate files.
  • Invoke methods on the .NET BCL or any user assembly.
  • Allocate native memory, call P/Invoke, or use reflection.
  • Spawn processes, open sockets, or talk to the GPU directly.
  • Reach the file system or environment at all.

The only side effects an expression can have are:

  • Returning a Complex value the iterator consumes.
  • Throwing a runtime arithmetic exception (e.g. divide by 0,
    log of 0, sqrt of negative real), which the calculator
    catches per pixel and treats as ""escaped"".

User Equation runs the same interpreter with the same safety; only
its optional CalcGen compile generates code, from equations you
compile yourself.

=== Limitations ===

  • No high-precision (DD/QD) path — Sandbox is double only.
    Zoom usefully caps around ≈ 1e13.  For deep zoom use User
    Equation with CalcGen on.
  • No perturbation theory acceleration.
  • No user-defined functions yet.  Repeat sub-expressions stay
    expanded (or name them with let / statements).
";

        public const string ClientServerText =
@"=== Fracturing Fog — Client / Server ===

Render fractals on one machine, drive the render from another. The same
FracturingFog.exe runs in three modes:

    UI            FracturingFog.exe                      (default)
    Server        FracturingFog.exe --server [opts]      (headless)
    Remote batch  FracturingFog.exe --batch --remote ... (headless)

All traffic is mutual TLS (mTLS). Saved cert passwords on the client are
encrypted with AES-GCM under a master password the user enters once per
UI session.

=== 1. Starting the server ===

  FracturingFog.exe --server

On first run, the server creates a self-signed cert bundle in:

  %APPDATA%\FracturingFog\server-certs\
      ca.pfx       (trust root)
      server.pfx   (server identity)
      client.pfx   (client identity — give to each client)

By default the server binds loopback only (127.0.0.1). To accept
connections from other machines:

  FracturingFog.exe --server --bind 0.0.0.0

Server CLI flags:

  --bind ADDR        127.0.0.1 by default. 0.0.0.0 = all interfaces.
  --port N           TCP port. Default 47823.
  --max-minutes N    Per-job render ceiling. Default 240.
  --allow-override   Let client request a longer timeout.
  --queue-depth N    Max queued jobs. Default 1. Excess → 'busy'.
  --cert PATH        Override server identity PFX.
  --client-ca PATH   Override CA used to validate client certs.
  --log-dir PATH     %APPDATA%\FracturingFog\server-logs\ by default.
  --work-dir PATH    %APPDATA%\FracturingFog\server-work\ by default.

The Server… admin dialog (Floating Menu → Server…) shows the local
server's status, lets you edit max-minutes / allow-override / queue
depth, and offers Start / Restart / Kill for a local --server child
process. It does not control remote servers.

Status bar shows green ● Server when a local server is listening on
the configured port.

=== 2. Certificate setup ===

Same machine (loopback):

  1. Run FracturingFog.exe --server once.
  2. Client dialog → Client cert → browse to %APPDATA%\FracturingFog\
     server-certs\client.pfx.
  3. Server CA → ca.pfx from the same folder.
  4. Cert password is blank (dev certs have none).

Different machine:

  1. On the server host: FracturingFog.exe --server --bind 0.0.0.0.
  2. Copy client.pfx + ca.pfx to the client over a trusted channel
     (encrypted USB, SCP — do NOT email them).
  3. Client dialog browses to the copies. Leave Cert password blank.

Production / shared deployment:

  Generate per-user client certs signed by the same CA via your own
  PKI (openssl / dotnet / corporate CA). Each user gets their own
  client.pfx; all trust the same server. Store the .pfx with a
  password and enter that password in the Cert password field — it
  will be sealed under your master password.

=== 3. Master password ===

The first time you Save a connection with a non-empty cert password,
the master password becomes the vault key. Every subsequent session
must enter the SAME master password to decrypt saved entries.

  • Empty vault: any password works (the first save sets it).
  • Existing sealed entries: Unlock attempts a decrypt to verify.
  • No recovery. Forgotten master pw → delete
    %APPDATA%\FracturingFog\client-connections.json and re-add.

The master password is held in process memory only. Closing the UI
clears it.

=== 4. Client dialog ===

  Connection group:    pick / save / delete a named server target.
                       Browse to client cert + server CA. Cert pw
                       is optional.

  Render preset group: name + Mode (image / video — banner above
                       turns orange for video) + Fractal (filtered:
                       UserEquation / Sandbox / UserBulb removed) +
                       Region / Theme (editable comboboxes) +
                       Quality + Size + Manual coords + Video
                       sub-form.

  Output group:        path (blank = prompt on response) + return
                       mode (inline = bytes over TLS; saved-path =
                       server keeps + returns the path).

  Render button:       label tracks Mode — reads 'Render Image' when
                       Mode = image, 'Render Video' when Mode = video.
                       Runs once; disabled while in-flight. Status
                       line shows Connecting → Rendering → Done
                       (NNN ms, WxH). Errors in red.

=== 4a. Rendering a video from the Client dialog ===

The same dialog produces both stills and videos — the Mode combo
decides which, and the Render button relabels itself.

  1. Unlock master password.
  2. Pick a saved connection.
  3. In the Render Preset group, set Mode = video.
     • The banner above the form turns orange and reads
       '▶ VIDEO MODE — output will be an MP4/MKV'.
     • The Render button at the bottom reads 'Render Video'.
  4. Set the end target with a Region (recommended) or Manual
     coords/zoom.
  5. In the 'Video options' sub-form near the bottom:
        seconds      duration of the clip (0.5–600)
        fps          frame rate (1–240, typical 30 or 60)
        start zoom   how zoomed-out frame 0 is (e.g. 0.5 = full
                     set, then animates IN to the region's zoom)
        reverse      tick to animate OUT instead
        lossless     none = browser-friendly MP4 (default)
                     h264 = lossless MP4 via ffmpeg
                     ffv1 = lossless MKV via ffmpeg
                     h264hq = high-quality (near-lossless) MP4
  6. Output: set a local file path ending .mp4 (or .mkv for ffv1),
     or leave blank to be prompted on completion.
  7. Click 'Render Video'. Server renders, encodes, returns bytes.

Notes:
  • Lossless h264 / ffv1 / h264hq require ffmpeg.exe on the SERVER's
    PATH. The 'none' preset uses the built-in Mp4Writer.
  • Inline (default) streams the file back in 1 MB chunks. For long
    videos pick return mode 'saved-path' so the server keeps the file
    and just returns its path (you read it later over file share).
  • Total bytes can be large — a 60-second 4K lossless render is
    multi-GB. The 'none' preset is usually the right starting point.

=== 5. Batch / remote batch ===

Headless single render against a saved connection + saved preset.
Image OR video — the preset's Mode field drives which path runs.
The CLI prints 'mode : image' or 'mode : video' on startup so you
can confirm before the render begins.

  Image:
    FracturingFog.exe --batch --remote ^
        --connection NAME ^
        --render NAME ^
        --out C:\out\poster.png

  Video (the preset's Mode = video):
    FracturingFog.exe --batch --remote ^
        --connection NAME ^
        --render NAME ^
        --out C:\out\zoom.mp4

If your preset's Mode = video but you pass an .png path to --out,
the bytes are still video bytes — name --out to match what the
preset produces (.mp4 / .mkv).

The CLI prompts for the master password on stdin (no echo), unlocks
the vault, runs the same protocol the UI uses, writes returned bytes
to --out. Exit 0 on success.

=== 6. Common errors ===

  forbidden-fractal   Preset (or its region tag) names UserEquation /
                      Sandbox / UserBulb. Pick another type.
  unknown-region      Region not in the server's library. Save it
                      server-side or switch to manual coords.
  unknown-theme       Theme not in the server's library.
  bad-request         Out-of-range dimensions / video seconds / fps.
                      Limits: 16-32768 px, 0.5-600 s, 1-240 fps,
                      64 megapixel ceiling.
  timeout             Render took longer than --max-minutes.
  busy                Server queue full. Retry or raise queue depth.
  ArgumentException   Saved connection has no client cert path.
    'path'            Open it, browse to the .pfx, Save again.
  Wrong password      Master pw mismatch. See section 3.
  Hangs on Connect    Wrong host/port, firewall, or server bound
                      loopback-only.

=== 7. Security summary ===

  • Server defaults to loopback only. --bind 0.0.0.0 is a conscious
    step before exposing on a network.
  • mTLS — server rejects any client that does not chain to its CA.
  • User-code fractal types blocked at the protocol layer.
  • Per-job timeouts + queue cap + 32 concurrent TLS session limit.
  • Image cap 32768 × 32768; default 64-megapixel pixel ceiling.
  • Vault: AES-GCM, PBKDF2-SHA256 200k iterations, per-entry salt.
  • Dev certs are convenient for localhost. For network deployment
    issue real certs via your own PKI and pass them via --cert /
    --client-ca.

=== 8. File locations ===

  %APPDATA%\FracturingFog\server-config.json
  %APPDATA%\FracturingFog\server-certs\*.pfx
  %APPDATA%\FracturingFog\server-logs\*.log
  %APPDATA%\FracturingFog\server-work\
  %APPDATA%\FracturingFog\client-connections.json   (sealed)
  %APPDATA%\FracturingFog\client-render-presets.json
";

        public const string MathGeneratedZ2Text =
@"=== Mandelbrot Z² (Generated) ===

A drop-in z² + c calculator emitted by the in-tree CalculatorGen tool
from a single-line equation, NOT hand-written. Same recurrence and
visual output as the standard Mandelbrot set, but the entire C# class
— scalar reference loop, AVX2 lane loop, ILGPU GPU kernel, perturbation
deep-zoom path, and BLA acceleration — is mechanically generated from
the source string

    z*z + c

This tab documents what the generator produces and how to drive it.
For the math of the Mandelbrot set itself, see the Mandelbrot tab.

=== Why a generator? ===

  • Stop hand-writing one ~500-line C# file per fractal family.
  • Guarantee scalar / AVX2 / GPU / perturbation / BLA paths stay in
    lock-step — they are all derived from the same AST.
  • Make adding new polynomial fractals (z^3 + c, z^16 + c, custom
    polynomials) a one-line invocation instead of an LLM session.

=== Five execution paths ===

The generator emits these in a single class. All five are validated
against the scalar reference path by the auto-generated SelfTest.cs.

  1. Scalar           Reference path. Plain `double` arithmetic.
                      One pixel at a time. Used as the ground truth.

  2. AVX2 + FMA       Vector256<double>, four pixels per lane.
                      Complex multiply via Fma.MultiplyAdd /
                      MultiplyAddNegated. Per-lane bailout via
                      Avx.BlendVariable so escaped lanes freeze.

  4. Perturbation     Reference orbit at view centre + per-pixel δ
                      iteration using the symbolic Taylor expansion
                      of p(Z+δ, C+ε) - p(Z, C). For z²+c the
                      expansion is exact: ε + 2Zδ + δ². Used when
                      Zoom ≥ PerturbZoomThreshold (default 1e12).

  5. BLA              Bilinear approximation. Pre-computes
                      A_n = ∂p/∂z(Z_n) and B_n = ∂p/∂c(Z_n) along
                      the reference orbit. At each iter, takes a
                      linear step δ_new = A·δ + B·ε if validity
                      holds (|δ| ≤ 1e-3·|Z|); else falls back to
                      the full perturbation step.

  6. ILGPU GPU        Lazy-init ILGPU Context + Accelerator. One
                      work item per pixel. Reads back a struct of
                      (iter, zr, zi, dr, di) per pixel and runs the
                      colour map on the CPU so themes work.

Toggle paths via calculator properties:

    UseGpu                = true | false   (opt-in)
    UsePerturbation       = true | false   (opt-in)
    PerturbZoomThreshold  = 1e12           (gate)
    UseBla                = true | false   (requires UsePerturbation)

=== Selecting in the UI ===

Toolbar Type combo →  ""Mandelbrot Z² (Generated)""

The entry sits next to the hand-tuned ""Mandelbrot"" so direct A/B
comparisons against the legacy implementation are one click apart.
Default centre and zoom match the standard Mandelbrot view. Pan,
zoom, region jumps, theme switches, capture, and video all work
unchanged — the generated class implements the same
IFractalCalculator contract every other engine uses.

=== Self-test ===

Every --selftest invocation writes a sibling validator. Running

    FracturingFog --gentest MandelbrotZ2

renders a 64×64 grid through all five paths and reports drift:

    MandelbrotZ2CalculatorSelfTest — scalar ↔ AVX2 ↔ GPU agreement
      grid:           64×64 = 4096 pixels
      max iterations: 256
      mismatches:     0 (0.00%)
      mean |Δit|:     0.0000
      max  |Δit|:     0  (tolerance: 1)
      gpu in-set:     cpu=523  gpu=523  diff=0  → PASS
      perturbation:   cpu=523  pt=523  diff=0  → PASS
      bla:            cpu=523  bla=523  diff=0  → PASS
      result:         PASS

Tolerances:
  • Scalar ↔ AVX2:    maxAbsDiff ≤ 1 (one ULP shift in bailout test)
  • Scalar ↔ GPU:     ≤ 4 boundary pixels   (FMA / device math drift)
  • Scalar ↔ Perturb: ≤ 4 boundary pixels   (round-off in ref orbit)
  • Scalar ↔ BLA:     ≤ 8 boundary pixels   (linearised δ² omission)

Output is also mirrored to gentest.out next to the exe (the program
runs WinExe so stdout is detached from the parent console).

=== Adding more generated calculators ===

The repo ships one demo (Mandelbrot Z²). To generate another:

    dotnet run --project CalculatorGen -c Release -- ^
        --equation ""z*z*z + c"" ^
        --name MandelbrotZ3 ^
        --out Calculators\Generated ^
        --selftest

Grammar accepted:
    z, c                          complex variables
    real literals (incl. 1e-3)    treated as (n, 0) complex
    + - *                         complex arithmetic
    ^N                             integer power 0..16, base = z or c
    ( )                           grouping
    unary -                       complex negation

Not yet supported:
    /  sin  cos  exp  log  |z|  conj  conditional branches

Once generated, add a FractalType enum value, plumb through the four
FractalRenderHost touchpoints (field / ctor / colour map / Resize /
SelectAltCalculator dispatch), add a BuiltInFractalLabels entry, and
the new calc appears in the dropdown alongside Mandelbrot Z².

=== Where this lives ===

  CalculatorGen\                       — generator project (console exe)
  CalculatorGen\Parser\                — AST nodes, lexer, parser,
                                         symbolic differentiator,
                                         simplifier, Taylor expander
                                         for perturbation
  CalculatorGen\Emitters\              — scalar, AVX2, perturbation
                                         emitters (each subclasses
                                         EmitterBase)
  CalculatorGen\Templates\             — Calculator.template.cs +
                                         SelfTest.template.cs (emit
                                         placeholders)
  CalculatorGen\SampleOutput\          — reference outputs for diffing
  Calculators\Generated\               — actively built generated calcs

Full authoring guide:
    Docs\CalculatorGen-Authoring.md

Includes complete coverage of the AST node types, simplifier rules,
imag-zero optimisation, perturbation Taylor builder, BLA validity
criterion, the ILGPU lifecycle, and trade-offs / future work.
";

        // ── CalcGen (User Equation editor) ────────────────────────────────
        public const string CalcGenText =
@"=== CalcGen — User Equation editor ===

CalcGen turns one line of fractal math into a fully validated
calculator with five execution paths (scalar, AVX2, perturbation, BLA,
ILGPU GPU). Open via:

  Toolbar → Type → User Equation
  Then:    Params  (or the per-region default)

Turn on the editor's CalcGen toggle for its three actions:

  Compile & Load        Compile in the background, swap onto the live
                        render.  Lives until app close (or an edit).
  Compile + Save        The same, and reload it on the next launch.
  Generate via CalcGen  Write Calculators\Generated\{Name}Calculator.cs.
                        Rebuild the app to pick it up.

The report line beside them says what CalcGen accelerates for the
current equation.  CalcGen reads the same equation language as the
live view (see the User Equation help); the table below is the
quick reference.

=== Grammar ===

  z_{n+1} = <expression>

  Construct        Example                      Notes
  ---------------  ---------------------------  ------------------------
  Variable z       z                            Current iterate
  Variable c       c                            Pixel coordinate
  Real literal     2, 0.5, 1e-3                 Lifted to (n, 0)
  Add / Sub        z + c   z - c
  Multiply         z*z   2*c
  Divide           z / (z + 1)                  Disables PT / BLA
  Power            z^2   z^2.5   z^-1           Any exponent; -z^2 = -(z^2)
  Parens           (z + c) * (z - c)
  Unary minus      -z
  Conjugate        conj(z)                      Disables PT / BLA / DE
  Component fold   fold(z) = (|zr|, |zi|)       Burning Ship
  Square shortcut  sqr(z)                       = z*z
  Real / imag      re(z), im(z)                 Lifts real scalar
  Magnitude        abs(z)                       Lifts |z|
  Squared mag.     norm(z)                      |z|², cheap bailout test
  Trig / exp / log sin cos exp log              Holomorphic; DE kept
  Previous iter    prev                         z_{n-1} (Phoenix)
  Iter index       iter (or n)                  Real scalar
  Conditional      if cond then a else b        Also cond ? a : b
  Comparisons      < <= > >= == !=              Combine with && || !
  Local name       let w = z*z in w + c

=== Execution-path gating ===

  Construct used         Scalar AVX2 PT  BLA DE  DD/QD GPU
  ---------------------  ------ ---- --  --- --  ----- ---
  Polynomial in z (+ c)    Y     Y   Y   Y   Y    Y    Y
  Division                 Y     Y   .   .   Y    .    Y
  conj / fold              Y     Y   .   .   .    .    Y
  Transcendentals          Y     Y   .   .   Y    deg  Y
  if / else                Y     Y   .   .   Y    .    Y
  prev                     Y     Y   .   .   .    Y    Y
  iter / n                 Y     Y   .   Y   Y    Y    Y

The status bar shows the live path label: SP / AVX2 / PT / BLA / DD-HP
/ QD-PT etc.

=== Examples ===

Mandelbrot family
    z*z + c                              Classic
    z^3 + c                              Multibrot cubic
    z^4 + c                              Quartic
    z^3 - z + c                          Two-term cubic
    (z^4 + z^2)/2 + c                    Mixed-degree
    (z*z - 1)/(z + 1) + c                Rational (Mandelbrot-shell)

Burning Ship / Tricorn
    fold(z)*fold(z) + c                  Burning Ship
    conj(z)*conj(z) + c                  Tricorn
    conj(fold(z))^2 + c                  Hybrid

Phoenix
    z*z + c + 0.5*prev                   Classic Phoenix
    z*z - 0.5*prev + c                   Negative feedback
    z^3 + 0.4*prev + c                   Cubic Phoenix
    z*z + 0.3*prev - 0.1*prev*prev + c   Two-tap

Transcendental
    exp(z) + c
    sin(z) + c
    log(z*z) + c
    0.5*z + sin(z) + c                   Damped oscillator

Conditional / iteration aware
    if norm(z) < 1 then z*z + c else z*z - c
    if re(z) > 0 then z*z + c else conj(z)*conj(z) + c
    if (iter % 4) < 2 then z*z + c else z*z - c
    sin(z + 0.01*iter) + c               Iter-driven phase

=== Editor workflow ===

  Save…       Name + persist (%APPDATA%\FracturingFog\userequations.json).
  Delete      Remove the selected saved entry only.
  Promote to fractal list
              Surface the saved entry as a first-class dropdown item.
  Rotation°   Visual post-rotation of the iteration plane.
              +90 / -90 / Reset for quick adjustments.

The status line shows ""✓ Equation parses"" (green) or the error with
line + col (yellow); the live view re-renders about two seconds after
typing stops.  The CalcGen state line shows Compiling… / Compiled /
Compile failed.

=== CLI ===

  dotnet build CalculatorGen\CalculatorGen.csproj -c Release
  dotnet run --project CalculatorGen -c Release -- ^
      --equation ""z*z + c"" --name MandelbrotZ2 ^
      --out Calculators\Generated --selftest

Flags: --equation ""..."", --name <Name>, --out <dir>, --selftest,
       --bailout <R>.

=== Troubleshooting ===

  Unknown function 'X'      Typo; the message suggests the closest name
                            and Ctrl+. applies it.
  ""real or complex         The value's kind depends on the pixel; wrap
   depending on …""          it in re(), im(), abs() or norm().
  Deep zoom drops to scalar Construct disables PT/BLA — see gating table.
  Black past 1e13           Conj/Fold/Prev gate DD/QD HpDirect off.
                            Switch to a polynomial form.
  Compile failed            The generated-code diagnostic follows.

=== Full guide ===

  Docs\User\CalcGen-UserGuide.md
                               User-facing reference + 30 examples.
  Docs\Technical\Equation-Language.md
                               The language specification.
  Docs\CalculatorGen-Architecture.md
                               Generator internals (for modifiers).
";

        // ── ColorGen (algorithmic colour theme editor) ────────────────────
        public const string ColorGenText =
@"=== ColorGen — algorithmic colour theme editor ===

ColorGen turns a short DSL into a sealed IColorMap class. Each pixel's
escape data is exposed as named inputs; the program evaluates to a
Vec3 in [0,1]^3 and the runtime packs that into ARGB.

Open via: right-click on the render surface → ColorGen Editor…

Buttons:
  Compile & Load        Roslyn-compile, register, swap onto the live
                        palette. Session lifetime.
  Save…                 Persist DSL source to
                        %APPDATA%\FracturingFog\colorgen.json.
  Generate via ColorGen Write Models\ColorSchemes\Generated\{Name}Theme.cs.
                        Rebuild to ship.

=== Statements ===

  let <name> = <expr>;      // Scalar or Vec3 local
  return <vec3-expr>;       // last statement; must be Vec3

  // line comments and /* block comments */ supported.

=== Types ===

  Scalar  double
  Vec3    RGB triple (channels in [0,1]); access via .r .g .b

Binary + - * / % ^ auto-broadcast scalar↔vec3 (result Vec3 if either
side Vec3). Comparisons and logical ops require scalars; yield 1.0/0.0.

=== Built-in inputs ===

  Scalars: smooth dist iter maxIter t nx ny zr zi dzr dzi arg mag isInSet pxScale

  Constants: pi  tau (=2π)  e  phi (golden ratio)

=== Operators (high → low) ===

  Postfix    .r .g .b
  Unary      - + !
  Power      ^                       (right-assoc)
  Mul / Div  * / %                   (% = GLSL-style mod)
  Add / Sub  + -
  Compare    < <= > >= == !=
  Logical    && ||
  Ternary    ?:                      (branches same type)

=== Functions ===

  Scalar → Scalar
    sin cos tan asin acos atan sinh cosh tanh
    exp log log2 log10 sqrt abs sign floor ceil round fract
    saturate radians degrees

  Two-arg scalar
    atan2 hypot min max mod pow step

  Three-arg scalar
    clamp(x, lo, hi)   smoothstep(e0, e1, x)

  mix    polymorphic: (S,S,S) → Scalar, (Vec3,Vec3,Scalar) → Vec3

  Hash   hash(x), hash2(x, y)

  Vec3 constructors
    rgb(r, g, b)
    hsv(h, s, v)        hue is cyclic (fract applied automatically)
    hsl(h, s, l)

  Vec3 ops
    palette(t, c0, c1, c2, …)         cyclic n-stop palette
    brightness(v, s)                  add s to each channel
    contrast(v, s)                    s in [-1, 1] around 0.5
    gamma(v, g)                       channel pow(c, 1/g)

=== Example gallery ===

HSV cycler
    return hsv(smooth * 0.04, 0.9, 1.0);

HSV with in-set override
    let v = isInSet > 0.5 ? 0.3 : 1.0;
    return hsv(smooth * 0.05, 0.85, v);

Sinusoidal RGB
    let k = smooth * 0.1;
    return rgb(
      0.5 + 0.5 * sin(k),
      0.5 + 0.5 * sin(k + tau / 3),
      0.5 + 0.5 * sin(k + 2 * tau / 3));

Cyclic palette
    return palette(smooth * 0.02,
      rgb(0.05, 0.02, 0.10),
      rgb(0.40, 0.10, 0.55),
      rgb(0.95, 0.55, 0.10),
      rgb(1.00, 0.95, 0.70));

Banded gradient
    let k = fract(t * 8.0);
    return palette(k,
      rgb(0, 0, 0), rgb(1, 0.4, 0),
      rgb(1, 1, 0.7), rgb(0.2, 0.6, 1));

Distance-field glow
    let d = tanh(dist / pxScale * 0.5);
    let core = rgb(1.0, 0.95, 0.6);
    let halo = rgb(0.1, 0.3, 0.9);
    return mix(halo, core, smoothstep(0.0, 1.0, d));

Lambert shading
    let lit = clamp(nx*0.4 + ny*-0.4 + 0.8, 0.0, 1.0);
    let base = hsv(smooth * 0.03, 0.6, 1.0);
    return base * lit;

Argument (domain) coloring
    return hsv(arg / tau, 1.0, isInSet > 0.5 ? 0.3 : 1.0);

|z| chrome bands
    let band = fract(log(mag) * 4.0);
    let v = 0.4 + 0.6 * band;
    return rgb(v, v, v);

Two-tone toon
    let lit = nx*0.5 + ny*0.5 + 0.5;
    return lit > 0.6 ? rgb(1, 1, 1) : rgb(0.05, 0.05, 0.20);

Procedural noise
    let n = hash2(floor(smooth), floor(t * 50.0));
    return hsv(fract(t * 3 + 0.1*n), 0.85, 0.9);

Heatmap
    let t01 = saturate(smooth * 0.003);
    return palette(t01,
      rgb(0.00, 0.00, 0.10), rgb(0.30, 0.00, 0.50),
      rgb(0.90, 0.20, 0.00), rgb(1.00, 0.90, 0.20),
      rgb(1.00, 1.00, 1.00));

Aurora
    let mixT = 0.5
        + 0.25 * sin(t * tau * 4 + nx * 6)
        + 0.25 * sin(t * tau * 7 + ny * 9);
    return mix(rgb(0.05, 0.10, 0.20),
               rgb(0.10, 1.00, 0.40),
               saturate(mixT));

Plasma
    let p = sin(smooth*0.05) + sin(arg*4) + sin(mag*3);
    let q = fract((p + 3.0) * 0.16667);
    return palette(q,
      rgb(0.05, 0.00, 0.30), rgb(0.80, 0.10, 0.50),
      rgb(1.00, 0.85, 0.40), rgb(0.95, 1.00, 0.95));

Power-law gamma
    let base = palette(smooth*0.02, rgb(0,0,0), rgb(1,0.3,0.1), rgb(1,1,1));
    return gamma(base, 1.8);

Vintage sepia
    let g = saturate(smooth * 0.005);
    return gamma(rgb(g*1.1, g*0.95, g*0.7), 1.4);

|dz/dc| highlight
    let mag2 = sqrt(dzr*dzr + dzi*dzi);
    let glow = saturate(log(1 + mag2) * 0.2);
    let base = palette(smooth*0.02,
      rgb(0.05, 0.05, 0.10), rgb(0.4, 0.2, 0.8), rgb(1, 0.95, 0.4));
    return brightness(base, 0.3 * glow);

Phong-ish three-light blend
    let lit1 = clamp(nx*0.5 + ny*-0.5 + 0.7, 0.0, 1.0);
    let lit2 = clamp(nx*-0.4 + ny*0.4 + 0.3, 0.0, 1.0);
    let key  = rgb(1, 0.95, 0.85);
    let fill = rgb(0.2, 0.35, 0.7);
    let base = palette(t * 2,
      rgb(0.02, 0.02, 0.08), rgb(0.8, 0.6, 0.3), rgb(1, 1, 1));
    return base * 0.5 + key * lit1 + fill * lit2 * 0.5;

=== Workflow notes ===

  Save…     Persists the DSL source (not the generated class).
  Compile & Load    Adds map to ColorPalette.HotLoadedPalettes; lives
                    until app close. Re-compile replaces in place.
  Generate via ColorGen    Writes a permanent .cs file under
                    Models\ColorSchemes\Generated\. Theme name + class
                    name auto-sanitised. Rebuild to include.

=== Troubleshooting ===

  'return' must yield a Vec3        Wrap final expr with rgb / hsv /
                                    hsl / palette.
  Stray tokens after 'return'       'return' must be last statement.
  Unknown identifier 'foo'          Typo or unsupported name.
  Ternary branches must match       Both ?: arms same type.
  palette() arg 1 must be scalar    First arg is t; stops follow.
  palette() stops must be Vec3      Wrap each stop with rgb / hsv / hsl.
  Channel access requires a Vec3    .r/.g/.b only on Vec3 values.

=== Full guide ===

  Docs\ColorGen-UserGuide.md      User-facing reference + 30 examples.
";

        // ── Toolbar reference ────────────────────────────────────────────
        public const string ToolbarText =
@"=== Top Toolbar ===

The Avalonia MainWindow's top toolbar surfaces the controls you reach
for most often. Anything not here lives in the Floating Menu (press M)
or one of the modeless dialogs.

  Type combo       Active fractal family.
                   • 17 built-ins: Mandelbrot, Julia, Burning Ship,
                     Tricorn, Multibrot, Phoenix, Newton, Buddhabrot,
                     IFS, L-System, Strange Attractor, User Equation,
                     Mandelbulb (3D), Sandbox, User Bulb (3D), Tear
                     Drop, plus the four generated families
                     (Mandelbrot Z², Z³, Z⁴, Z⁵).
                   • A ""— Registered —"" divider follows, then every
                     User Equation / Sandbox / User Bulb saved with
                     ""Promote to fractal list"" ticked.
                   Selection updates the calculator + view defaults.

  Quality combo    Draft / Standard / High / Ultra / Extreme. Drives
                   the iteration scaler and DD / QD promotion. See
                   the Quality Presets section in Features.

  Region combo     Built-in tour + your saved regions. Right-click for
                   the sort menu (Default vs by-FractalType filter —
                   handy when the list grows large).

  Theme combo      Active color map. Right-click for sort menu
                   (Default / All A–Z / per-kind: Cycling / Phong3D /
                   PBR3D / Distance / Domain / …).

  Grid toggle      Cartesian complex-plane overlay.

  Watermark        Toggle the region + theme + program watermark
                   drawn into the BGRA buffer (CPU-composited so it
                   appears in screenshots / videos).

  Params           Open the per-type parameters dialog. Content
                   depends on the active fractal:
                     Julia      → c constant
                     Newton     → polynomial degree + relaxation
                     Multibrot  → exponent
                     Phoenix    → coupling p
                     Buddhabrot → sample count + band cutoffs
                     IFS        → preset + iteration count
                     L-System   → preset + depth
                     Attractor  → preset + (a, b, c, d)
                     Mandelbulb → power, iter, max steps, ε, camera
                     User Equation / Sandbox / User Bulb → editor

  Reset            Restore default center / zoom / iter for the
                   active fractal.

  Edit Theme       Open the Color Theme Editor (T).

  Menu             Toggle the Floating Menu (M).

  Help             Open this Help window.

=== Status bar (bottom) ===

  • Live: CX, CY, Zoom, Iter, active precision (SP / DD / QD),
    render-time / progress hint.
  • Right edge: ● Server indicator (green = local server up,
    grey = down, red = error). Click via Floating Menu → Server…
    to open the admin dialog.

=== Show / hide toggles ===

  • Toolbar:    hidden by Span mode; otherwise always visible.
  • Status bar: Floating Menu → ""Status"" checkbox.
  • Grid:       Floating Menu / toolbar.
  • Watermark:  Toolbar.

The toolbar and status bar each occupy their own layout band — the
GPU swap-chain HWND cannot occlude them.
";

        // ── Regions reference ────────────────────────────────────────────
        public const string RegionsText =
@"=== Regions — Coordinate Bookmarks ===

A region captures a complete view: center coordinates (with full
DD / QD limb fidelity), zoom factor, iteration count, fractal type
tag, and an optional preferred color theme.

=== Built-in vs User Regions ===

  Built-in    A curated tour of classic Mandelbrot landmarks —
              cardioid valley, period-bulbs, seahorse valley,
              elephant valley, double-spirals, deep-zoom showpieces.
              Read-only: applying them works; deleting does not.
  User        Anything you save via the Floating Menu (Save button
              or hotkey V). Write-able, delete-able, exportable.

=== Saving a region ===

  1. Pan / zoom / type to the view you want.
  2. Press V (or Floating Menu → Region → Save).
  3. Type a name. If the name already exists the prompt asks to
     confirm overwrite (built-ins are still protected — the
     overwrite UI lists only user regions).
  4. The new region appears in both the toolbar combo and the
     menu combo.

=== Applying a region ===

  • Toolbar combo / menu combo — select by name.
  • Slideshow — auto-cycles through regions on the configured
    interval (Slideshow Settings → Beats per Region).
  • Client dialog — pick a remote-server region by name.

Selection mutates the view state in-place: pan/zoom anchored at
the saved coordinate, iteration count restored, fractal type
re-selected if it differs, and the preferred color theme applied
if one is recorded.

=== Export / Import ===

  Exp…   Write the user library to JSON (file picker on save).
  Imp…   Merge a region JSON into your library. Name collisions
         prompt per-region: Skip / Overwrite / Rename.

Stored at %APPDATA%\FracturingFog\regions.json. Built-in regions
live in <install>\Resources\Regions\ and are baked into the EXE —
the on-disk file holds user regions only.

=== Sort / filter ===

Right-click the Region combo on the toolbar or in the Floating
Menu:

  Default         Built-ins first, then user regions, original order.
  By Fractal Type Filter so only regions for the active family
                  show. ""— select region —"" is a non-selectable
                  header injected by the filter.

=== Slideshow extreme-region filter ===

A checkbox in Slideshow Settings decides whether very-deep-zoom
regions are included in the auto-cycle. Useful when you want a
calmer rotation that stays at shallower zooms.

=== Region JSON schema ===

Each entry is a single Region object with these fields:

  name              user-visible name (unique within the file)
  type              fractal family enum (mirrors FractalType)
  centerXHi/Lo[3]   center.X DD/QD limbs
  centerYHi/Lo[3]   center.Y DD/QD limbs
  zoom              double
  iterations        int
  themeName         optional — preferred color theme
  sandboxName       optional — bound Sandbox equation
  userEquationName  optional — bound User Equation
  userBulbName      optional — bound User Bulb 3D source
  notes             optional — free-form description

JSON is indented (System.Text.Json) — easy to diff and share.
";

        // ── Server admin reference ───────────────────────────────────────
        public const string ServerAdminText =
@"=== Server Admin Dialog ===

The Server Admin dialog is the in-shell control surface for the
LOCAL FracturingFog render server — the headless --server worker
that accepts mTLS-protected render jobs from a client (this
program in --batch --remote mode, or another shell's Client
dialog).

Open via:  Floating Menu → ""Server…"" button.

The dialog manages only the local server. To control a server on
a different host, run the admin tool on that host (or use SSH +
the CLI flags below).

=== Sections ===

  Status            uptime · in-flight job count · completed count
                    · last error · current bind / port · active
                    queue depth.

  Lifecycle         Start · Restart · Kill buttons spawn or
                    terminate a local `FracturingFog --server`
                    child process. Restart applies pending config
                    edits and re-launches.

  Bind / Port       Network interface to bind (default 127.0.0.1
                    — loopback only). Set to 0.0.0.0 to accept
                    LAN connections. Port defaults to 47823.

  Limits            Max minutes / job (default 240) · Allow
                    client to override timeout · Queue depth
                    (default 1) · Max concurrent TLS sessions
                    (default 32) · 64-megapixel pixel ceiling.

  Rate limit        Per-IP accepted-connection rate (default
                    off) + burst allowance. Closes flood attacks
                    fast without harming legitimate retries.

  TLS hardening     Require TLS 1.3 only · Revocation policy
                    (none / online / offline) · Allowed client
                    cert thumbprints (pinning — empty = chain
                    trust alone).

  Paths             Server cert PFX path · Client CA PFX path ·
                    Cert directory override · Log dir · Work dir.

  Stale sweep       Work-dir auto-purge age (default 1 h);
                    leftover job-* dirs from a crash get deleted
                    on next startup.

Apply rewrites %APPDATA%\FracturingFog\server-config.json and
signals the running server to soft-restart on the next idle
window. Cancel discards edits in memory only.

=== Status-bar indicator ===

The main window's status bar shows a coloured ● Server pill on
the right edge:

  ● Server  (green)   Local server is up + listening on the
                      configured port.
  ● Server  (grey)    Local server is down.
  ● Server  (red)     Local server reported an error or is
                      unreachable through the management socket.

Hover for the last error string.

=== Self-signed cert bundle ===

The first `--server` run with no explicit cert paths generates a
fresh bundle in the configured cert directory:

  ca.pfx       Trust root (give the same one to every client).
  server.pfx   Server identity cert.
  client.pfx   Default client identity cert (copy to each client).

Dev certs are convenient for loopback and small LAN deployments.
For production:

  1. Issue per-user client certs from your own CA / corporate PKI.
  2. Drop the server.pfx + ca.pfx into a folder.
  3. Set Cert dir override OR Server cert PFX path + Client CA
     PFX path explicitly.
  4. Optionally enable cert pinning via Allowed client thumbprints.
  5. Optionally require TLS 1.3.
  6. Optionally set Revocation policy to ""online"".

See Docs\ServerAdmin-Guide.md for a deployment walkthrough.

=== Client dialog ===

The Client dialog (Floating Menu → ""Client…"") drives a remote
server. See the Client / Server tab for the full walkthrough.
";

        // ── Slideshow + video reference ──────────────────────────────────
        public const string SlideshowText =
@"=== Slideshow + Video ===

The slideshow engine cycles regions and color themes hands-free.
The same engine drives the optional video-slideshow recording mode.

Open the slideshow:
  Floating Menu → Slideshow button       (start / stop)
  Floating Menu → ""Slideshow Settings…"" (configure timings + audio)
  Shift+click Slideshow                  (lock current region — only
                                          themes cycle)

VCR transport bar (bottom of MainWindow):
  ◀◀     Skip back to the previous region
  ◀      Skip back one theme
  ▮▮     Pause / Resume
  ▶      Skip forward one theme
  ▶▶     Skip forward to the next region

The VCR row is only visible while the slideshow is running.

=== Timing ===

Default fixed-duration mode:
  Region every    30 s     (Beats per Region in audio mode)
  Theme every     10 s     (Beats per Theme in audio mode)
  Cross-fade      ~3 s     (~0.75 × beat with audio-reactive)

Set Beats per Region = 0 in Slideshow Settings to lock the active
region (Shift+click shortcut does the same).

=== Region filter ===

A checkbox in Slideshow Settings controls whether very-deep-zoom
regions are included. Off by default — gives a calmer tour.

=== Watermark ===

While the slideshow runs, region name + theme name are drawn into
the live frame. Toggleable from the toolbar (Watermark) — the same
toggle that controls the static watermark.

=== Fractured Waves Reactive Audio ===

Enable from Slideshow Settings → ""Fractured Waves Reactive Audio""
or from the Audio Settings dialog. When ON, transitions land on the
detected beat instead of a fixed timer. See the Fractured Waves tab.

=== Video Zoom (single shot) ===

Floating Menu → Video button → currently-selected region.

Two-phase animation:
  1. Pan phase  (first 5 % of duration)  — pan to the target
                center at the current zoom.
  2. Zoom phase (remaining 95 %)         — log-zoom into the
                target with center fixed.

Both phases use smoothstep easing. Frame rate is calculation-
bound, not wall-clock — total duration is honoured.

Recording (Floating Menu → Slideshow Settings → Video tab):

  None              Live playback only.
  MP4 (built-in)    Media Foundation H.264 — no external deps.
  Lossless H.264    libx264 -qp 0, MP4, yuv444p. Needs ffmpeg.
  Lossless FFV1     FFV1 v3 in MKV. Needs ffmpeg.
  H.264 HQ          libx264 -crf 18, MP4, yuv420p. Needs ffmpeg.
  PNG sequence      Frame-by-frame lossless dump (any mode).

MP4 and PNG-sequence can record simultaneously.

ffmpeg.exe is discovered in:
  1. The app folder.
  2. <install>\Tools\ and <install>\Resources\.
  3. PATH.

=== Video Slideshow ===

Continuous mode: zoom in → pause → zoom out → next region → repeat.
Each leg defaults to 30 s with a 7 s pause between videos. Stops
independently from the single-shot Video button (Esc or the
Slideshow button toggles off).

=== Per-region iteration override ===

Regions can carry a stored iteration target; the video engine
raises MaxIterations to at least that value during the leg so
deep targets don't render as all-in-set black just because the
quality preset's iter formula produced a smaller number.

=== Live TAA tuning during video ===

While a video zoom is rendering, the Floating Menu surfaces three
extra sliders:

  TAA Alpha    temporal blend strength between frames.
  Fade Start   zoom at which the deep-zoom artifact fade begins.
  Fade End     zoom at which the fade reaches full strength.

Use them to dial back persistent ghost trails on busy regions.
";

        // ── Poster / large-format capture reference ──────────────────────
        public const string PosterText =
@"=== Poster — Print-Resolution Capture ===

The Poster button renders a tiled composite image far larger than
the on-screen panel. Each tile is calculated separately at full
quality, then stitched into one PNG / TIFF / BMP. Ideal for
wallpaper, prints, or archive-quality stills.

Open via:
  Toolbar Poster button (or Floating Menu → View → Poster).
  Client dialog → Mode = poster (remote host).

=== Dialog options ===

  Width / Height    Output pixel size. Capped at 32 768 × 32 768
                    (server cap; local cap matches). 64-megapixel
                    soft ceiling.
  Tile size         Per-tile pixel size. Default 1024. Smaller
                    tiles = more parallelism, more seam risk;
                    larger tiles = fewer seams, more memory.
  Format            .png (default) / .tif / .tiff / .bmp.
  Output path       File path or folder. Folder → filename is
                    synthesised from region + theme + timestamp.
  Tile previews     Live thumbnail strip while tiles render.

=== Workflow ===

  1. Pick a region (or use the current view).
  2. Open Poster.
  3. Set Width / Height (3840×2160 = 4K, 7680×4320 = 8K,
     15360×8640 = 16K, …).
  4. Pick tile size — leave default 1024 unless you have a
     reason.
  5. Browse to Output path.
  6. Render. Progress bar shows tile-of-total + ETA.

Cancel at any time — the partial frame buffer is dropped.

=== Remote poster (Client dialog) ===

  1. Pick a saved server connection.
  2. Set Mode = ""image"" (poster is just a large image to the
     server protocol).
  3. Set Width / Height to your poster dimensions.
  4. Pick fractal / region / theme / quality.
  5. Output: a local file path on YOUR machine — the server
     streams the bytes back via TLS.

Larger posters benefit from saved-path return mode (server keeps
the file on its own disk and replies with the path; read it later
over file share).

=== Notes ===

  • Brightness / Contrast / Adaptive sliders apply to every tile.
  • Watermark text scales with the output resolution.
  • Multi-monitor Span mode is unrelated — Span affects the live
    window only; Poster always renders at its own configured size.
  • Posters honour the active quality preset's iter scaling. Use
    Lock Iterations + a manually-set iter count if you want
    consistency between local screen render and the poster.
";

        // ── Architecture / dev reference ─────────────────────────────────
        public const string ArchitectureText =
@"=== Architecture Overview ===

Fracturing Fog is structured as a layered .NET 10 solution:

  FracturingFog.Abstractions (cross-platform, UI-free)
    • Models, ViewState, interfaces (IFractalRenderer / IGpuSurface /
      IFractalCalculator / IColorMap / IFractalRenderHost /
      IFractalInputController / IColorThemeService / IHelpContent-
      Provider / IPaletteExtractionService / IVideoZoomController).
    • HelpTextBundle (this file).
    • POCO DTOs: ColorThemeDef, LightSourceDef, PbrMaterialBandDef,
      FractalViewState, Region, etc.

  FracturingFog.UI.Avalonia (cross-platform Avalonia 12 shell)
    • App / Views (axaml) / ViewModels / Controls / Slideshow / Input.
    • Pure MVVM — no System.Drawing, no Vortice, no Win32.
    • GpuSurfaceControl is a NativeControlHost wrapping an
      IGpuSurface (HWND on Windows; CAMetalLayer / VkSurface
      placeholders for future ports).

  FracturingFog.Rendering (DirectX 11) + Rendering.Skia / Silk
    • Vortice DXGI swap-chain on Windows.
    • Skia is a pure-managed cross-platform fallback (no GPU yet).
    • Silk.NET shells reserved for Vulkan / OpenGL back-ends.

  Rendering.Silk.Smoke + Rendering.Skia (smoke / portable paths)

  Server + ServerHost (the headless --server worker)
    • Mutual-TLS framed protocol.
    • Per-job sandbox: 32 concurrent connections, per-IP rate limit,
      configurable timeout, queue depth, work-dir auto-sweep.
    • Forbidden fractals (UserEquation / Sandbox / UserBulb) blocked
      at the protocol layer to prevent remote code execution.

  Client (the in-shell remote driver)
    • Sealed connection vault (AES-GCM under user master password,
      PBKDF2-SHA256 200k iterations, per-entry salt).
    • Render presets — image OR video — saved separately from
      connections.

  Calculators (per-family compute kernels)
    • IFractalCalculator implementations: Mandelbrot, Julia, Burning
      Ship, Tricorn, Multibrot, Phoenix, Newton, Buddhabrot, IFS,
      L-System, Strange Attractor, Mandelbulb, User Equation
      (equation language), Sandbox (equation language), User Bulb 3D (raymarch),
      Tear Drop, plus the CalculatorGen-emitted Generated family.

  CalculatorGen (compile-time code generator)
    • AST + lexer + parser + symbolic differentiator + simplifier +
      Taylor expander + scalar / AVX2 / perturbation emitters.
    • Emits five execution paths per generated calculator: scalar
      reference, AVX2+FMA, ILGPU GPU, perturbation, BLA.
    • Self-test scaffolding for path agreement.

  ColorGen (algorithmic palette generator)
    • Tiny DSL parser + Roslyn emitter for IColorMap implementations.
    • Live ""Compile & Load"" path keeps maps in memory; ""Generate
      via ColorGen"" writes permanent .cs to the source tree.

  Imaging / Audio / Export / Batch
    • BMP / PNG / TIFF writers + multi-tile poster compositor.
    • NAudio-based capture + spectral-flux beat detector.
    • CLI parser (Batch.CommandLine) for headless renders.

=== Build ===

  dotnet build FracturingFogCLD.sln
  dotnet run --project Server.Tests

The solution targets .NET 10. The Avalonia shell uses ReactiveUI for
property change notification and ReactiveCommand for commands.

=== Entry points ===

  • UI shell:        FracturingFog.exe
  • Headless render: FracturingFog.exe --batch [opts]
  • Render server:   FracturingFog.exe --server [opts]
  • Remote batch:    FracturingFog.exe --batch --remote …

=== Where things live ===

  Abstractions\Models\          shared DTOs + view state
  Abstractions\Help\            this Help bundle
  UI.Avalonia\Views\            .axaml files
  UI.Avalonia\ViewModels\       VM logic
  Hosting\                      host services (IColorThemeService,
                                IHelpContentProvider, palette etc.)
  Calculators\                  every per-family kernel
  Calculators\Generated\        CalcGen output
  Models\ColorSchemes\          built-in IColorMap implementations
  Models\ColorSchemes\Generated\ ColorGen output
  Server\                       --server worker + protocol DTOs
  Client\                       Client dialog + vault
  Resources\                    bundled icons + JSON seeds + ffmpeg

=== See also ===

  PHASE2_AVALONIA_MIGRATION.md       Migration history
  Docs\Avalonia-UserGuide.md         Full UX walkthrough
  Docs\Architecture-Overview.md      Module-by-module deep dive
  Docs\CalculatorGen-Architecture.md Generator internals
  Docs\ServerAdmin-Guide.md          Deployment + cert PKI
";

        public const string MathMagnetOneText =
@"=== Magnet 1 ===

Clifford A. Pickover (1980s).  A rational escape-time map inspired
by the magnetic-susceptibility partition function from statistical
physics:

        zₙ₊₁ = ( (zₙ² + c − 1) / (2zₙ + c − 2) )²

z₀ = 0, c = pixel.  Unlike the polynomial Mandelbrot family the
orbit can converge to TWO finite attractors — infinity (escape)
AND the fixed point z = 1.  Fracturing Fog treats only the escape
basin visually; the converged-to-one basin colours as ""in set"".

=== Pole ===

The denominator vanishes along the curve 2z + c − 2 = 0.  Pixels
whose orbit passes near this curve would blow up to NaN under
naive evaluation.  The kernel floors |den|² ≥ 1e-12 so the
quotient stays finite — the resulting cell may still escape on
the next iteration, but never poisons the buffer.

=== Bailout ===

10² (= 100), not the standard Mandelbrot 2².  The rational map
grows more slowly than z² + c near the unit circle so a small
bailout would trap many true escape paths inside the iteration
budget.

=== Geometry ===

Two main lobes: a heart-shaped main body around c ≈ (1.5, 0) and
a small companion below.  Filament structure resembles the
Mandelbrot dendrites but tilts inward toward the z = 1 attractor.
Default frame: centre (1.5, 0), Zoom 0.6.

=== Parameters ===

None beyond the pixel coordinate.  No tunables in the Params
dialog.

=== As a User Equation ===

  --- Magnet 1 ---
  ((z*z + c - 1) / (2*z + c - 2))^2
";

        public const string MathMagnetTwoText =
@"=== Magnet 2 ===

Pickover's cubic Magnet variant.  A higher-degree rational map
that resolves the Magnet 1 attractor into a richer multi-basin
structure:

        num  = zₙ³ + 3(c−1)zₙ + (c−1)(c−2)
        den  = 3zₙ² + 3(c−2)zₙ + c² − 3c + 3
        zₙ₊₁ = (num / den)²

z₀ = 0, c = pixel.  As with Magnet 1, the kernel uses a denom-
magnitude floor (1e-12) to keep iterations bounded near the
pole curve.

=== Bailout ===

10² for the same growth-rate reason given for Magnet 1.

=== Geometry ===

Three-lobed main body around c ≈ (1.5, 0) with finer filament
detail than Magnet 1.  The convergence-to-z=1 basin is split
into several disconnected components, giving the structure a
""shattered"" feel.

=== Parameters ===

None beyond the pixel coordinate.

=== As a User Equation ===

  --- Magnet 2 ---
  var cm1 = c - 1;
  var cm2 = c - 2;
  var num = z^3 + 3*cm1*z + cm1*cm2;
  var den = 3*z^2 + 3*cm2*z + c*c - 3*c + 3;
  (num / den)^2
";

        public const string MathGlynnText =
@"=== Glynn Fractal ===

Earl Glynn (1990s).  Julia set of the fractional-power map

        zₙ₊₁ = zₙ^1.5 + c           c ≈ −0.2

The canonical view (c = −0.2 + 0i) produces a single connected
dendrite often shown as the namesake ""Glynn"" image — a black
tree-like silhouette against the escape gradient.

=== Fractional Power ===

z^1.5 is multi-valued for complex z; the principal branch is
evaluated through polar form:

        r     = |z|
        θ     = arg(z)
        z^1.5 = r^1.5 · ( cos(1.5θ) + i·sin(1.5θ) )

At the origin (r = 0) the kernel reseeds z to c so the orbit
does not divide by zero or evaluate log(0).  The branch-cut
along the negative real axis is therefore preserved on the
principal sheet — sufficient for this canonical c.

=== Geometry ===

  • Default frame: centre (−0.2, 0), Zoom 0.7.  The dendrite
    fits inside |z| < 1.5.
  • Boundary has fractal dimension > 1; tree-like filaments
    branch off the central trunk at every scale.
  • Off the canonical c the family generalises continuously into
    a smooth zoo of Julia variants — Fracturing Fog currently
    hardcodes c = −0.2.  A user-tunable c slider is planned.

=== Parameters ===

  GlynnC : Complex   Constant c.  Default (−0.2, 0).  Real-part
                     drag tilts the dendrite; small imaginary
                     tweaks deform it asymmetrically.  Clamped
                     to |Re|, |Im| ≤ 2 in the Params dialog.

=== As a User Equation ===

  --- Glynn ---
  z^1.5 + c
";

        public const string MathLogisticText =
@"=== Logistic Bifurcation ===

The one-dimensional iterated map

        xₙ₊₁ = r · xₙ · (1 − xₙ)             r ∈ (0, 4]

studied since May (1976) as the simplest model of period-doubling
route to chaos.  Not an escape-time fractal — every pixel column
in Fracturing Fog corresponds to one value of r and accumulates
the visited x values into a per-column density histogram.

=== Rendering Pipeline ===

  1. Map screen pixel (px, py) to (r, x) using the standard
     view-state mapping (CenterX = r, CenterY = x).
  2. For each r-column:
       • Seed x = LogisticSeed (default 0.5)
       • Burn in LogisticBurnIn steps (default 1000) to settle
         onto the attractor.
       • Plot next (MaxIterations − BurnIn) steps; each visited
         x maps to a y-pixel and increments that pixel's hit
         counter.
  3. Log-normalise the hit map and feed (1 − norm)·MaxIter
     into the active IColorMap; alpha-blend toward InSetColor
     for sparse / empty pixels (matches Buddhabrot tone-map).

Cost is O(W · MaxIter) — Width controls density resolution, not
sample budget.  4000 iterations is a comfortable default at
1920×1080.

=== Geometry ===

  • r < 1            x → 0  (extinction)
  • 1 ≤ r ≤ 3        single stable fixed point
  • 3 < r ≤ 3.449    period-2 cycle
  • 3.449 < r ≤ 3.544 period-4
  • 3.544 < r ≤ 3.564 period-8
  • δ ≈ 4.669…       Feigenbaum constant — ratio of successive
                     bifurcation intervals.  Limit point ≈ 3.5699.
  • r > 3.5699       chaotic regime interspersed with periodic
                     windows (period-3 at r ≈ 3.8284 is famous).

Default frame: CenterX = 3.5, CenterY = 0.5, Zoom = 2.0 frames
r ∈ ~[2.6, 4.4], x ∈ ~[0, 1].

=== Parameters ===

  LogisticBurnIn : int     Iterations to discard before density
                           accumulation.  Default 1000.  Raise
                           for slowly-converging chaotic windows
                           where transient orbits leak speckle
                           into the histogram.
  LogisticSeed   : double  x₀ ∈ (0, 1).  Default 0.5.  All
                           non-fixed-point seeds converge to the
                           same attractor; extreme values
                           (close to 0 or 1) just lengthen the
                           transient.

=== Themes ===

Density-histogram themes work.  Interior-cycle themes are
meaningless (no iter count per pixel) and should be hidden by
the theme picker's family gating.

=== References ===

  May, R. M. (1976) ""Simple mathematical models with very
  complicated dynamics."" Nature 261, 459–467.
  Feigenbaum, M. J. (1978) ""Quantitative universality for a
  class of nonlinear transformations."" J. Stat. Phys. 19, 25–52.
";

        public const string MathHalleyText =
@"=== Halley Basins ===

Edmond Halley (1694).  A root-finding iteration with CUBIC
convergence — one power higher than Newton's quadratic
convergence — at the cost of one extra derivative per step:

        zₙ₊₁ = zₙ − R · 2·f(zₙ)·f'(zₙ) /
                       ( 2·f'(zₙ)² − f(zₙ)·f''(zₙ) )

Fracturing Fog uses the standard f(z) = z^d − 1, the same
polynomial Newton ships with, so all d-th roots of unity are
the attractors and basin colouring is reused without change.

=== Newton vs Halley ===

  • Newton:  z := z − R · f / f'
             ─ quadratic convergence
             ─ basins meet with the Wada-lakes property
  • Halley:  z := z − R · 2 f f' / (2 f'² − f f'')
             ─ cubic convergence
             ─ basins meet with the same topology, but the
               boundary has finer filament detail because
               each iteration step is more accurate.

Halley typically converges in roughly 2/3 the iterations
Newton needs for the same epsilon, so the iteration-shaded
colouring lands in an outer band — the picture often looks
""crisper"" than Newton at the same MaxIterations.

=== Parameters ===

  NewtonExponent   : int      Polynomial degree d.  Default 3.
                              Shared with the Newton dialog.
  NewtonRelaxation : double   Relaxation factor R.  Default 1.0.
                              R = 1 is canonical Halley; R ≠ 1
                              speeds up or slows convergence.

=== As a User Equation ===

  --- Halley (z³ − 1) ---
  // basins: set z₀ seed = c
  var f   = z^3 - 1;
  var fp  = 3*z^2;
  var fpp = 6*z;
  z - 2*f*fp / (2*fp*fp - f*fpp)
";

        public const string MathSecantText =
@"=== Secant Basins ===

The secant method is the derivative-free cousin of Newton's
iteration — instead of f'(z), it approximates the slope by the
chord through the previous two iterates:

        zₙ₊₁ = zₙ − R · f(zₙ) · (zₙ − zₙ₋₁) / (f(zₙ) − f(zₙ₋₁))

Order of convergence ≈ φ ≈ 1.618 (slower than Newton's 2 but
still superlinear).  Fracturing Fog renders the basins of
attraction of f(z) = z^d − 1, mirroring the Newton / Halley
basin maps so the three families render at the same scale and
colour with the same theme.

=== Two-Point State ===

Per-pixel state is (z, prev_z) instead of just z — the kernel
must carry the previous iterate to compute the next chord.
Mathematically equivalent to PhoenixKernel's prev-z slot but
applied to root-finding instead of escape-time.

The recurrence is undefined when prev_z = z (zero chord
denominator).  Pixel initialisation:
    z      = pixel
    prev_z = pixel + SecantInitialOffset    (default 0.5 + 0i)

A non-zero offset is required.  The offset only seeds the first
chord; once iteration starts the chord direction tracks the
local function shape and the asymptotic basins are independent
of small offset changes.  Large offsets can land iterates in
different convergence basins than Newton on the same pixel —
this is a feature, not a bug.

=== Newton vs Halley vs Secant ===

  • Newton:  uses f, f'        (quadratic convergence)
  • Halley:  uses f, f', f''   (cubic convergence)
  • Secant:  uses f only       (superlinear, ≈ 1.618)

Secant needs MORE iterations than Newton for the same epsilon
but each iteration is CHEAPER (no derivative evaluation), so on
high-degree polynomials Secant can beat Newton overall.  For
the z^d − 1 family the difference is small; the visual interest
is the chord-step pattern showing through the basin filaments.

=== Parameters ===

  NewtonExponent      : int      Polynomial degree d.  Default 3.
                                 Shared with Newton / Halley.
  NewtonRelaxation    : double   Relaxation factor R.  Default 1.0.
  SecantInitialOffset : Complex  Initial prev_z displacement.
                                 Default (0.5, 0).  Magnitude
                                 floored at 1e-6 to avoid degenerate
                                 first-step chord.

=== As a User Equation ===

  --- Secant step (z³ − 1) ---
  // the chord through prev and z; set z₀ seed = c.  FractalType =
  // Secant seeds its two starting points differently.
  var f = z^3 - 1;
  var g = prev^3 - 1;
  z - f*(z - prev) / (f - g)
";

        public const string MathMandelboxText =
@"=== The Mandelbox ===

Tom Lowe (2010).  A 3D escape-time fractal built from two
piecewise-linear folds plus a uniform scale.  Per iteration:

        z ← scale · sphereFold(boxFold(z)) + c

with c = ray-sample position (Mandelbrot convention).

=== The two folds ===

  Box fold        — reflection across the planes x = ±1, y = ±1,
                    z = ±1.  Component-wise:
                      if  z_i >  1   z_i ← 2  − z_i
                      if  z_i < −1   z_i ← −2 − z_i

  Sphere fold     — radial scaling driven by two radii
                    R = fixedRadius (≥ minRadius) and m = minRadius:

                      if  |z| <  m    z ← (R/m)² · z      (constant zoom)
                      if  m ≤ |z| < R z ← (R/|z|)² · z    (inversion band)
                      else            z is unchanged

Both folds are conformal away from the fold planes / spheres,
so the linear DE bound stays valid.

=== Distance estimate ===

Track a scalar derivative magnitude dr that mirrors the
running |dz| (folds multiply both z and dr by the same factor):

  dr ← |scale| · dr + 1            after the linear z ← scale·z + c
  dr ← f · dr                      inside each sphere-fold branch

  DE(p) ≈ |z| / |dr|

Surface normal is estimated by central differences of DE.  Lit
with one directional source; ambient = 0.15.

=== Classic scale values ===

  scale =  2.0   The canonical Mandelbox.  Vault-and-corridor
                 structure with the recognisable box footprint.
  scale = −1.5   Inversive ""Juliabox-like"" variant.  Smoother,
                 with central spherical lobes.
  scale =  3.0   Open-pore high-detail variant — fold cycles
                 don't close, exposing inner spiral structure.

=== Parameters ===

  MandelboxScale          : double  Per-iter scale.  Default 2.
  MandelboxFixedRadius    : double  Sphere-fold outer radius.  Default 1.
  MandelboxMinRadius      : double  Sphere-fold inner radius.  Default 0.5.
  MandelboxIterations     : int     DE inner iter count.  Default 12.
  MandelboxBailout        : double  |z|² escape threshold.  Default 1024.
  MandelboxMaxSteps       : int     Raymarch step cap.  Default 128.
  MandelboxEpsilon        : double  DE hit threshold.  Default 0.0015.
  MandelboxCamera*        : double  Dedicated camera + light angles.

=== Implementation notes ===

DE iterations and ray-march step cap are higher than the
Mandelbulb's because Mandelbox folds are cheaper per iter
(no transcendental — only branches + multiplies) so the
budget shifts toward more iters.  Bailout is large (10²·)
because folds bound z slowly compared to z^p escape.

Scale values near critical points (|scale| ≈ 1) can collapse
the DE — iter clamp and bailout exit at |z|² > 10⁶ catch this
before the surface goes degenerate.
";

        public const string MathKifsText =
@"=== Kaleidoscopic IFS (KIFS) ===

Knighty (2010), generalising classic 2D IFS attractors to 3D
distance-estimation raymarching.  Per iteration the point z is
folded by a reflective table, then linearly scaled away from
a pivot offset:

        z ← scale · fold(z) − (scale − 1) · offset

Different fold tables produce different attractor shapes.
Fracturing Fog ships two built-in tables.

=== Menger sponge fold ===

Sort-3 absolute-value fold:

        z ← |z|                         (3 reflections)
        sort components by descending magnitude
        z ← scale · z − (scale − 1) · offset
        smallest component left at scale · z (no offset)

with scale = 3 and offset = (1, 1, 1) reproducing the
Menger sponge — cube with the centre and the six face-centred
sub-cubes removed, recursively.

=== Sierpinski tetrahedron fold ===

Vertex-reflection fold:

        if  x + y < 0   swap and negate  (x, y) ← (−y, −x)
        if  x + z < 0   swap and negate  (x, z) ← (−z, −x)
        if  y + z < 0   swap and negate  (y, z) ← (−z, −y)
        z ← scale · z − (scale − 1) · offset

with scale = 2 and offset = (1, 1, 1) reproducing the
Sierpinski tetrahedron gasket.

=== Distance estimate ===

Each iteration multiplies the running derivative magnitude
by scale.  After N iterations:

        dr = scaleᴺ
        DE(p) ≈ (|z_N| − r₀) / dr

where r₀ is the bounding sphere radius of the iterated shape
(≈ 2 for both built-in tables — generous enough to keep the
estimate a valid lower bound).

=== Parameters ===

  KifsFold                : Menger | Sierpinski
  KifsIterations          : int     DE inner iter count.  Default 14.
  KifsScale               : double  Per-iter scale.  0 = canonical
                                    default (3 Menger, 2 Sierp).
  KifsOffsetX / Y / Z     : double  Pivot offset.  Default (1, 1, 1).
  KifsBailout             : double  |z|² escape threshold.
  KifsMaxSteps            : int     Raymarch step cap.  Default 160.
  KifsEpsilon             : double  DE hit threshold.  Default 0.0012.
  KifsCamera*             : double  Dedicated camera + light angles.

=== Implementation notes ===

KIFS folds are cheaper than Mandelbox sphere-folds (no division,
no square root inside the fold) so the per-step DE budget is
spent on more iterations — default 14, vs Mandelbox's 12.  Step
cap is also bumped to 160 because the recurring offset−scale
combination produces sharper-edged surfaces than the Mandelbox
and the marcher needs finer step granularity near them.
";

        public const string MathQuatJuliaText =
@"=== Quaternion Julia ===

Hart, Sandin & Kauffman (1989) lifted the 2D Julia set into
the quaternions:

        q ∈ ℍ        (Hamilton quaternions, 4D)
        q_{n+1} = q_n² + c       with c ∈ ℍ constant

Quaternion squaring is non-commutative in general, but the
specific product q·q is well-defined and matches the standard
Hamilton form.  Escape criterion is identical to the complex
case — |q|² > bailout (default 16).

=== 3D slice ===

The full attractor lives in 4D and is not directly viewable.
Fracturing Fog raymarches a 3D slice through ℍ — a single
pixel (x, y, z) becomes:

        q = (x, y, z, QJuliaSliceW)

QJuliaSliceW is a UI slider.  Sliding it reveals different 3D
cross-sections of the same 4D set; classic visualisations are
the W = 0 plane (filaments) and small |W| (compact bulbs).

=== Distance estimate ===

Hubbard–Douady estimator generalised to quaternions:

        DE = 0.5 · |q| · ln |q| / |dq|

where dq is the orbital derivative tracked through iteration
with the chain rule:

        dq_{n+1} = 2 · q_n · dq_n        (Hamilton product)
        dq_0     = (1, 0, 0, 0)

The same lower-bound argument as the 2D case applies — the
estimator is a guaranteed under-bound on the distance to the
boundary, which is what sphere-tracing needs.

=== Parameters ===

  QJuliaCX / Y / Z / W   : double  Constant c ∈ ℍ.
                                   Defaults (−0.2, 0.4, −0.4, −0.4)
                                   reproduce the Hart 1989 cover plate.
  QJuliaSliceW           : double  W of the 3D viewing slice.
                                   Slide live to re-render new cross-sections.
  QJuliaIterations       : int     DE inner iter count.  Default 11.
  QJuliaBailout          : double  |q|² escape threshold.  Default 16.
  QJuliaMaxSteps         : int     Raymarch step cap.  Default 160.
  QJuliaEpsilon          : double  DE hit threshold.  Default 0.0012.
  QJuliaCamera*          : double  Dedicated camera + light angles.

=== Implementation notes ===

Hamilton product is computed inline (no Quat allocation) so the
per-iter cost is 16 multiplies + 12 adds for q² and the same for
2·q·dq — comparable to a Mandelbox iteration without the sphere-
fold divide.  Iteration depth saturates around 10–14: past that
the DE shrinks below the per-step epsilon faster than the new
detail it reveals.

The slice-W slider does not invalidate any caches — switching
it just re-runs the DE with a different starting q.W and the
existing camera, lighting, theme and post-FX all transfer.
";

        public const string MathQuatMandelbrotText =
@"=== Quaternion Mandelbrot ===

Norton (1982) and Holroyd (early 1990s) extended the
Mandelbrot membership test from ℂ to ℍ:

        q ∈ ℍ        (Hamilton quaternions, 4D)
        q_{n+1} = q_n² + c       with c varying per pixel

Same squaring map as Quaternion Julia — the only difference
is which variable varies.  For Julia, c is constant and
the orbit's starting q comes from the pixel.  For
Mandelbrot, q starts at the origin (membership test) and c
takes the pixel coordinate.

=== 3D slice ===

The Mandelbrot set lives in c-space, which is 4D.  Fracturing
Fog raymarches a 3D slice through ℍ — a single pixel
(x, y, z) becomes:

        c = (x, y, z, QMandelSliceW)

QMandelSliceW is a UI slider.  Sliding it reveals different
3D cross-sections of the same 4D set.  The W = 0 plane shows
the familiar 2D Mandelbrot silhouette extruded along Z;
non-zero W exposes thin filaments and bulb dustings that
have no analogue in the complex case.

=== Distance estimate ===

Hubbard–Douady estimator with the derivative taken wrt c:

        DE = 0.5 · |q| · ln |q| / |dq|

Chain rule on q_{n+1} = q_n² + c gives:

        dq_{n+1} = 2 · q_n · dq_n + 1
        dq_0     = 0          (q_0 = 0, so dq/dc = 0)

The +1 (identity quaternion) on each step is the partial
derivative of the additive c term.  Same Hubbard–Douady
under-bound applies — the estimator is a guaranteed lower
bound on the distance to the boundary, which is what
sphere-tracing needs.

=== Parameters ===

  QMandelSliceW           : double  W of the 3D viewing slice.
                                   Slide live to re-render new cross-sections.
  QMandelIterations       : int     DE inner iter count.  Default 11.
  QMandelBailout          : double  |q|² escape threshold.  Default 16.
  QMandelMaxSteps         : int     Raymarch step cap.  Default 160.
  QMandelEpsilon          : double  DE hit threshold.  Default 0.0012.
  QMandelCamera*          : double  Dedicated camera + light angles.

=== Implementation notes ===

Per-iter cost is the same as Quaternion Julia — 16 multiplies
+ 12 adds for q² and the same for 2·q·dq.  The +1 on the
derivative update costs nothing measurable.

Unlike the complex Mandelbrot, the quaternion variant has no
known closed-form perturbation series, so the renderer does
not run the CalcGen 5-path pipeline — every pixel pays full
DE cost.  Detail saturates around iter 10–14 with the
default bailout 16, same as Julia.

The slice-W slider does not invalidate any caches — switching
it just re-runs the DE with a different c.W and the existing
camera, lighting, theme and post-FX all transfer.
";

        public const string MathPlasmaText =
@"=== Plasma (Diamond-Square) ===

Not strictly a fractal — Fournier, Fussell & Carpenter's 1982
diamond-square algorithm builds a 2D height field whose
power spectrum is approximately 1/f^β, the same self-similar
statistics produced by fractional Brownian motion.  Visually
it's the classic ""cloud"" / terrain noise that ships in
every fractal generator going back to Apophysis.

=== Construction ===

Choose the smallest power-of-two grid (size 2ⁿ + 1) covering
the longer image edge.  Seed the four corners with random
values in [0, 1].  Then for each subdivision level:

  1. Square step.  For every cell centre, average the four
     surrounding corner heights and add a random jitter in
     (−amp, +amp).
  2. Diamond step.  For every edge midpoint, average the
     (up to four) cardinal neighbours and add the same kind
     of jitter.
  3. Halve the step size; multiply amp by 2^(−roughness).

The displacement schedule controls the fractal dimension of
the resulting surface.  Continuous-everywhere, nowhere-
differentiable surfaces correspond to roughness ≈ 0.5; lower
roughness produces visibly smooth gradients, higher
roughness produces jagged mountain terrain.

After the field is built it's normalised to [0, 1] and
sampled bilinearly into the output buffer, mapped through
the active gradient palette.

=== Parameters ===

  PlasmaRoughness : double  0..1.  0 = smooth gradient,
                            1 = full amplitude (no decay).
                            Default 0.55 — Apophysis-style cloud.
  PlasmaSeed      : int     PRNG seed.  Same seed + roughness
                            + image size = identical field.

=== Implementation notes ===

The field is generated once per render; pan and zoom are
disabled because the procedural domain has no natural
notion of coordinates outside the seed grid.  Re-rendering
with the same seed at a different output size reuses the
same conceptual surface but resamples the (2ⁿ + 1) grid
that fits the new dimensions, so the pattern morphs
slightly with viewport size — not a bug, an artefact of
the grid quantisation.

Themes that work on density / smooth-iter palettes carry
over directly: the height value is fed to IColorMap.Map
in the same [0, 256) range escape-time fractals use.
";

        public const string MathFlameText =
@"=== Flame Fractals (Apophysis-style) ===

Draves & Reckase, ""The Fractal Flame Algorithm"" (2003).  A flame
is an iterated function system (IFS) where each map is the
composition of an affine pre-transform and a single non-linear
""variation"".  The result is tone-mapped through a log-density
histogram with gamma correction and per-map colour blending — the
visual signature of every Apophysis / Chaotica / Electric Sheep
flame ever rendered.

=== Construction ===

For each chaos-game step:
  1. Pick map i with probability w_i / Σ w_j.
  2. Apply the affine pre-transform
        p' = A · p + t
     (six coefficients per map: A is 2×2, t is 2-vector).
  3. Apply the non-linear variation v_i
        q = V_{v_i}(p', amount_i).
  4. Update the running per-point colour
        c ← (c + colorIndex_i) · 0.5
     (geometric blend of every map traversed so far).
  5. Splat the (q, c) sample into the per-pixel hit + colour
     histograms.

After 8 M (default) samples the histograms drive the tone-map:
  α     = log(hit + 1) / log(maxHit + 1)            density
  α_γ   = α ^ (1 / gamma)                            gamma-boost
  bright = vibrancy · α_γ + (1 − vibrancy) · α       blend
  rgb   = palette(avgColor) · bright                 modulate

=== Variations (Slice 2) ===

Eight stock variations are wired:

  Linear      identity
  Sinusoidal  (sin x, sin y)
  Spherical   (x, y) / (x² + y²)
  Swirl       rotate by r² about the origin
  Polar       (θ/π, r − 1)
  Heart       r · (sin θr, −cos θr)
  Disc        (θ/π) · (sin πr, cos πr)
  Julia       √r · (cos φ, sin φ), φ = θ/2 + nπ random

Apophysis ships ~49 variations; the eight here are the ones
that appear in essentially every gallery flame.  Adding more is
mechanical — extend FlameVariation + ApplyVariation in
FlameRenderer.cs.

=== Parameters ===

  FlamePresetName : string  Built-in map table name.
  FlameMaps       : list    Optional explicit map list (overrides
                            the preset when non-null).
  FlameIterations : int     Chaos-game sample count.  Default 8M;
                            density-quality scales with √N.
  FlameGamma      : double  Tone-map gamma exponent.  Default 2.2
                            (Apophysis default).
  FlameVibrancy   : double  Highlight saturation blend, 0..1.
                            0 = linear density (preserves filament
                            tint), 1 = full gamma-on-colour.

=== Built-in presets ===

  Sierpinski Linear     — Sierpinski IFS, Linear variation only.
                          Identical shape to the IFS preset but
                          routed through gamma + per-map colour.
  Sierpinski Variation  — Sinusoidal-warped Sierpinski.
  Spherical Pair        — 2-map spherical inversion.
  Swirl Gasket          — 3-leg gasket with r²-swirl rotation.
  Heart Sierpinski      — Concave Heart-warped Sierpinski.
  Polar Julia           — Polar + Julia + Disc three-map blend.

=== Implementation notes ===

Spherical clamps to the origin inside a 1e-20 safety bubble to
avoid NaN at the inversion pole.  Julia's two-branch random
rotation uses a thread-local Random so the main chaos-game RNG
stays deterministic across slices.  The attractor-bbox pass
discards samples whose coordinates have already gone NaN /
infinity, so a sampler that lands inside a singular pole during
its 30 k-sample fit doesn't squash the rest of the set into a
single pixel.

Themes that work on density / smooth-iter palettes feed directly
through this renderer — average colour index sits in [0, 1] and
is fed to IColorMap.Map in the same [0, 256) range escape-time
fractals use.
";

        public const string MathApollonianText =
@"=== Apollonian Gasket ===

A circle packing built recursively from a seed quadruple of
mutually tangent circles using the Descartes Circle Theorem.
The gasket is the limit set under repeated tangency
construction — between any three mutually tangent circles
there are exactly two further circles tangent to all three,
and the packing recurses indefinitely.  The resulting fractal
has Hausdorff dimension ≈ 1.3057.

=== Descartes Circle Theorem ===

For four mutually tangent circles with signed curvatures
k_i = ±1/r_i (positive for internally tangent / disjoint,
negative for the unique enclosing circle):

  (k₁ + k₂ + k₃ + k₄)² = 2 · (k₁² + k₂² + k₃² + k₄²)

Given any three of the four, solving for k₄ yields a quadratic
with the two solutions corresponding to the two circles that
complete the tangency.  Vieta's formulas reduce this to a
single linear ""jump"" between the two solutions:

  k₄' = 2(k₁ + k₂ + k₃) − k₄

The complex form, with each circle's centre z_i carried as
k_i·z_i, gives the position of the new circle by the same
linear jump on (k·z):

  k₄'·z₄' = 2(k₁z₁ + k₂z₂ + k₃z₃) − k₄·z₄

=== Seed quadruple ===

This implementation ships the integral (−1, 2, 2, 3) gasket:

  outer disk     radius 1     k = −1   (enclosing)
  two ""kissing""  radius 1/2   k = +2
  two cusp caps  radius 1/3   k = +3   (above + below diameter)

It is one of the rare quadruples whose entire packing has
integer curvatures at every depth.

=== Recursion ===

From a quadruple {a, b, c, d}, generate three children by
Vieta-jumping each of a, b, c through the other three
(the just-inserted d is skipped to avoid revisiting).  Each
child quadruple replaces one circle with its Vieta partner
and recurses with the new circle as 'd'.  Termination:

  • Stop when the next generated circle would draw smaller
    than ApollonianMinPixelRadius device pixels of radius
    (the visual cutoff — almost always fires first).
  • Stop when recursion depth exceeds ApollonianDepth.
  • Skip Vieta jumps that yield non-positive curvature
    (would replace an enclosed circle with an enclosing one,
    bursting the gasket).

The pixel-radius cutoff naturally lets pan / zoom reveal new
detail — at higher Zoom the world-radius cutoff shrinks, so
deeper levels of the tree get drawn.

=== Parameters ===

  ApollonianDepth           : int    Max recursion depth.
                                     Default 12.
  ApollonianMinPixelRadius  : double Sub-pixel cutoff in device
                                     pixels.  Default 0.75.
  ApollonianColorByDepth    : bool   On = palette indexed by
                                     recursion depth (banded
                                     rings).  Off = log-radius
                                     gradient (smooth).

=== Implementation notes ===

Circles are accumulated into a flat list during recursion,
then sorted by radius descending and painted disk-fill in
order so that smaller children naturally overwrite the
interior of their enclosing parent — the nested-disks look
emerges from overdraw without any explicit annulus
construction.  The kissing-point cusps where three circles
meet are rendered as a stack of progressively smaller filled
disks; the pixel-radius cutoff is what keeps the renderer
from infinitely chasing those cusps.
";

        public const string MathKleinianText =
@"=== Kleinian Limit Set ===

A 3D fractal generated by the limit set of a Schottky-style
Kleinian group — the set of accumulation points reached by
composing inversions in a family of reflection spheres.  Every
point in the ambient ℝ³ is iteratively reflected through the
deepest-containing sphere; orbits that stay trapped between the
spheres pile up on the limit set, which fills the curvilinear
region bounded by the inversion mirrors.

=== Sphere inversion ===

Inversion through a sphere with centre c and radius r maps a
point p to

  p' = c + (p − c) · r² / |p − c|²

The transformation has scalar derivative magnitude r² / |p − c|²
multiplied by an orthogonal Householder factor of unit length —
so for distance-estimation purposes only the scalar prefactor
needs tracking through the iteration chain.

=== Distance estimator ===

  scale := 1
  for i in 0 .. iter:
    pick the sphere whose interior most contains p
    if every signed distance ≥ 0:  break
    invert p through that sphere
    scale *= r² / |p − c|²  (chain-rule scalar derivative)
  DE := nearest-sphere distance / scale

The renderer sphere-traces with this DE, then finite-differences
it for the surface normal and applies a single-light Phong shade.

=== Tetrahedral seed (first cut) ===

The shipping preset uses four mutually tangent spheres of radius
√2·s centred at the four even-parity (±s, ±s, ±s) corners — the
tetrahedral vertex arrangement.  At s = 1 each pair of spheres
is tangent at the midpoint of the connecting edge and their
interiors overlap heavily around the origin, so any sample point
near the origin is inside all four spheres at once.  Iterated
inversion drives the sample toward the limit set on the
intersection boundary.

The KleinianSphereScale parameter loosens the packing:  s < 1
shrinks the configuration so the spheres separate and the
fundamental domain opens up, splitting the limit set into
discrete shells around each sphere.

=== Parameters ===

  KleinianIterations  : int    Inversion-iteration cap.  Default
                               16; deep cusps benefit from 24+.
  KleinianSphereScale : double Tetrahedral arrangement scale.
                               Default 1.0 (mutually tangent).
  KleinianCamera*               Standard 3D orbit camera fields
                               (theta / phi / distance), shared
                               with the rest of the 3D family.
  KleinianLight*                Phong light direction (theta /
                               phi spherical).

=== Implementation notes ===

The signed-distance picker uses (|p − c| − r), so the deepest
inversion is the one with the most-negative signed distance.
The fallback for divergent inversions where |p − c| collapses
to numerical zero is an early break — keeps the DE finite even
at the kissing points where the chain rule technically blows up.
The accumulated scale is read at the end of the loop so the DE
remains meaningful for both deep-iter and early-break paths.

User-editable sphere lists, alternate Schottky configurations
(necklace, Klein-bottle, Apollonian-extrusion), and proper
Möbius-group composition are deferred to a later slice — this
first cut ships only the tetrahedral preset.
";

        public const string MathBicomplexText =
@"=== Bicomplex (Tessarine) Mandelbrot ===

A 3D-rendered slice of the 4D Mandelbrot set built over the
bicomplex (tessarine) algebra ℂ² — the commutative 4-dimensional
algebra spanned by (1, i, j, k) under the relations

  i² = j² = −1,   k² = +1,   ij = ji = k

Unlike the Hamilton quaternions, tessarine multiplication is
commutative.  Unlike split-complex, the algebra has zero divisors:
anything of the form a + a·k with a ∈ ℂ multiplied by 1 − k gives
zero, so the multiplicative norm is degenerate on a 2D subspace.

=== Iteration ===

  tₙ₊₁ = tₙ² + c    t, c ∈ ℂ²
  t₀  = 0           (Mandelbrot membership convention)

Squaring map for t = (t₁ + t₂·i + t₃·j + t₄·k):

  t²₁ = t₁² − t₂² − t₃² + t₄²
  t²ᵢ = 2·(t₁·t₂ − t₃·t₄)
  t²ⱼ = 2·(t₁·t₃ − t₂·t₄)
  t²ₖ = 2·(t₁·t₄ + t₂·t₃)

=== 3D slice convention ===

Pixel coordinate (x, y, z) maps to

  c = (x, y, z, BicomplexSliceW)

so the renderer walks the 1, i, j axes of c through the 3D viewport
with the k axis pinned to the slider value.  At sliceW = 0 the
3D slab collapses onto the standard 2D Mandelbrot extruded along
the j axis — the k coordinate decouples and the iteration projects
back to the complex squaring map.  Sliding sliceW away from 0
exposes the zero-divisor seam slabs unique to the tessarine
algebra; these read as flat slabs rather than the curved DE
surfaces a Hamilton-algebra rendering would produce.

=== Distance estimator ===

Hubbard–Douady, same as the quaternion variant:

  dtₙ₊₁ = 2 · tₙ · dtₙ + 1      (bicomplex product; 1 is the
                                  algebra identity)
  dt₀  = 0
  DE   = 0.5 · |t| · ln|t| / |dt|

The bicomplex product in the 2·t·dt step uses the commutative
multiplication table above, not the Hamilton ordering — for a
tessarine, t·dt equals dt·t, so the two writeable orderings yield
identical components.

=== Parameters ===

  BicomplexSliceW         : double  k-axis slice constant for c.
                                    Default 0.
  BicomplexIterations     : int     DE inner iteration count.
                                    Default 11.
  BicomplexBailout        : double  |t|² escape threshold.
                                    Default 16.
  BicomplexCamera*                  Standard orbit-camera fields
                                    shared across the 3D family.
  BicomplexLight*                   Phong light direction (theta /
                                    phi spherical).

=== Implementation notes ===

The 4-component DE returns 0 when |t| < 1 to give the surface a
flat hit inside the closed set, identical to the Hart/Hubbard
convention used for quaternion Mandelbrot.  Zero-divisor cusps —
where the inversion derivative collapses because |dt|² < 1e−30 —
are guarded with an early-return DE = 0; the sphere tracer then
treats the cusp point as a surface hit instead of stepping past it
on a numerically-blown distance estimate.

Visually this set overlaps the quaternion Mandelbrot on the
(i, j = 0, k = 0) plane (both restrict to the standard 2D
Mandelbrot) but diverges sharply on off-axis slices.  Lower visual
differentiation against the quaternion family is why this slot
sits late in the roadmap — it is here mostly for completeness of
the 4D algebra coverage rather than as a primary user-facing
fractal.
";

        public const string MathDlaText =
@"=== Diffusion-Limited Aggregation (DLA) ===

A stochastic growth model introduced by Witten and Sander (1981).
A single seed cell sits at the centre of a 2D grid; particles
spawn on a launch circle just outside the current aggregate
bounding radius, perform a Brownian random walk, and stick the
first time they land in a cell adjacent to the aggregate.  Each
new stuck cell extends the aggregate; the resulting tree has
empirical fractal dimension D ≈ 1.71 and recognisably dendritic,
branched structure with screened-off interior regions.

=== Algorithm ===

  init   grid[centre] = 1                (the seed)
         maxR² = 1                       (bounding radius²)
  for n = 1 .. DlaParticles:
    launchR = sqrt(maxR²) + margin
    killR   = launchR · 3 + 8
    spawn particle on the launch circle
    walk:
      if any 4-neighbour is aggregated:
        grid[here] = n + 1               (arrival index)
        update maxR²
        break
      step in one of {−x, +x, −y, +y} with equal probability
      if outside killR or at canvas edge: restart particle

The launch-circle + kill-radius pair is the standard Witten-
Sander optimisation: spawning at the aggregate boundary collapses
the long unbiased outbound walks that drive the naive O(N · area)
implementation down to roughly O(N² · log N).  Particles that
walk past the kill circle restart from a fresh launch point so
the joining distribution stays unbiased.

=== Determinism ===

A single System.Random seeded by DlaSeed drives both launch-angle
and walk-step draws, so (Width, Height, DlaSeed, DlaParticles)
uniquely determines the produced aggregate.  Reproducing a tree
just needs the same four values.

=== Colour by arrival ===

Each stuck cell records its arrival index n + 1; the colour pass
maps arrival/totalArrivals through the active IColorMap.  Early-
stuck cells therefore get one end of the palette and the outer
tips get the other — the temporal-growth structure of the
dendrite becomes the visual.  Vacuum cells stay at the
ColorBuffer's pre-cleared 0 (transparent black).

=== Parameters ===

  DlaParticles : int   Random-walk particle count.  Aggregate
                       cell count = DlaParticles + 1.  Default
                       8000 produces a recognisable dendrite at
                       512² in well under a second.
  DlaSeed      : int   PRNG seed for the walk.  Default 12345.

=== Limitations ===

Pan/zoom is unsupported — the simulation IS the image, and
zooming in would invalidate the per-cell arrival map.  The
roadmap calls out cached-blit pan/zoom as a future improvement;
this slice ships only the fixed-grid render.  Very high particle
counts (>200 k) on small canvases will see the kill-radius
restart cap kick in as the aggregate fills the available area —
that is expected and not a defect; bump canvas size in that case.
";

        public const string MathSpiderText =
@"=== Spider Fractal ===

A two-state escape-time recurrence where the constant c is
NOT constant — it drifts each iteration in the direction of z:

        zₙ₊₁ = zₙ² + cₙ
        cₙ₊₁ = decay · cₙ + zₙ₊₁         (default decay = 0.5)

Pixel coordinate seeds c₀; z₀ = 0 (Mandelbrot convention).  The
mutating c is what distinguishes Spider from every other
quadratic-family Mandelbrot-flavoured set — adjacent pixels'
c values drift apart, producing the namesake spider-leg
filaments instead of the smooth lobes of Mandelbrot.

=== Decay Spectrum ===

Decay is the only tunable.  Three regimes:

  decay = 1.0   c never mutates → degenerates to Mandelbrot
                (sanity check: render at decay = 1 and you get
                the canonical cardioid).
  decay = 0.5   Canonical Spider.  c bleeds half its previous
                value plus the new z; orbits flush quickly so
                in-set behaviour is dominated by the local z
                dynamics.
  decay = 0.0   c reseeds to z each step → heavy chaos, the
                ""set"" becomes a thin Cantor-like dust.

Intermediate values trace a continuous deformation between
these regimes.  The boundary fractal dimension shifts smoothly
with decay; this is one of the few standard escape-time
families with a non-degenerate one-parameter deformation
space.

=== Implementation ===

c mutates per iteration — that is NOT part of the standard
IFractalKernel.Step contract (Step takes c by value).
SpiderKernel exposes a dedicated StepMutatingC(ref zr, ref zi,
ref cx, ref cy) overload and EscapeTimeCalculator routes
Spider through its own loop (CalculateSpider) the same way
Phoenix routes through CalculatePhoenix for its prev-z carry.

No closed-form dz/dc — distance + normal themes fall back to
the flat-exterior branch in FillAuxAndColor.

=== Parameters ===

  SpiderCDecay : double  c-mutation coefficient.  Default 0.5.
                         Range [0, 1]; values outside the
                         range are clamped at the kernel level.

=== As a User Equation ===

The Spider changes c every step as well as z; a User Equation's c
is fixed per pixel, so it can't be expressed exactly.  Use
FractalType = Spider.
";
    }
}
