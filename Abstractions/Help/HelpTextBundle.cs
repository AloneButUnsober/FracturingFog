// Abstractions/Help/HelpTextBundle.cs
//
// Shared static help-text repository. The legacy WinForms FloatingHelp.cs
// historically held ~2,500 lines of verbatim help text as private inline
// @"..." string literals; the Avalonia HostHelpContentProvider used to
// surface short placeholders because the text could not be reached from
// the cross-platform abstractions assembly.
//
// This bundle is the new single source of truth for *cross-shell* help
// text. The Avalonia HostHelpContentProvider reads directly from these
// properties so the Avalonia FloatingHelp window renders the same long-
// form text the WinForms FloatingHelp has shipped for years.
//
// SCOPE: this initial cut covers everything IHelpContentProvider exposes
// today — AboutText / FeaturesText / AudioText / EditorText / BioText
// + the 4 MathSubTabs the Avalonia FloatingHelp surfaces (Mandelbrot,
// Julia, Newton, Mandelbulb). The legacy FloatingHelp.cs continues to
// hold its own copies of the Burning Ship / Tricorn / Multibrot /
// Phoenix / Nova / Buddhabrot / IFS / L-System / Attractor / User
// Equation / User Bulb / Sandbox math sub-tab strings; folding those
// in is the next step once IHelpContentProvider grows the surface area
// to expose them. See PHASE2_AVALONIA_MIGRATION.md F.3 follow-up notes.

namespace FracturingFog.Help
{
    public static class HelpTextBundle
    {
        public const string AboutText =
@"Fracturing Fog — real-time high-precision Mandelbrot & friends explorer.

Built for deep zoom: double → double-double (DD) → quad-double (QD)
auto-promotion with perturbation, series approximation, and bilinear
approximation past zoom 1e25. Twelve fractal families share the same
view-state and color pipeline.

• Renderer:  DirectX 11 / 12 via Vortice on Windows. The Avalonia shell
             hosts the swap chain inside a NativeControlHost so the
             same DXGI code path runs unchanged; Skia / Vulkan / Metal
             back-ends slot in for macOS and Linux as Phase 2 lands.
• UI:        Cross-platform Avalonia 12 — formerly WinForms-only.
• Audio:     Beat-synced slideshow + closed-loop fractal-synth source.
• Themes:    JSON-import-able gradient / cycling / Phong3D / PBR3D maps,
             editable in a modeless live-preview editor.
";

        public const string FeaturesText =
@"=== Navigation ===

  Mouse wheel        Zoom in / out at cursor
  Left-click drag    Pan the view
  Double-click       Center on point + zoom in
  Right-click        Context menu (toolbar, mini-map, etc.)
  Reset (R)          Restore default view (-0.5, 0, zoom 0.3)

=== Toolbar / Floating Menu ===

  Span        Span the window across all monitors
  Image       Save a high-resolution PNG screenshot
  Poster      Generate a multi-tile poster-size render
  Slideshow   Auto-cycle regions (30 s) and color themes (10 s)
  Video       Smooth animated zoom from current view to a target
  Menu        Toggle the floating coordinate/control window

  Quality     Standard / DeepHP / Extreme arithmetic presets
              (auto-promotes from double → DD → QD as zoom deepens)
  Theme       Color map selector — many built-in palettes
              plus user-imported JSON themes
  Region      Named bookmarks — built-in tour + your saved views

=== Coordinate / Region Panel ===

  CX, CY      Real / imaginary coordinates of the view center
              (accepts pipe-separated DD/QD limb format for paste-back)
  Zoom        Scalar zoom factor — paste large values like 1e48
  Iterations  Max escape iterations (min 64, no upper cap)
  Lock        Pin iterations during pan/zoom (no auto-recalc)
  Go          Apply the typed coordinates / zoom / iter values
  Flip Y      Mirror the view vertically (negate CY)

  Brightness  −100 … +100 post-process offset
  Contrast    −100 … +100 post-process multiplier
  Adaptive    0 … 100 histogram equalization (reveals flat detail)

  Save / Delete / Exp… / Imp…
              Persist custom regions to JSON, share, reload

=== Color Themes ===

  Categories  Escape-time, distance estimation, orbit traps,
              binary / argument decomposition, domain coloring,
              field lines, histograms, stripe averages, potentials,
              lemniscate, lighting / Phong / PBR (3-D),
              chromostereopsis, post-process, json-imported, etc.
  Exp / Imp   Export / import individual themes as JSON
  Delete      Remove a user-imported theme (built-ins protected)
  Reload      Re-scan disk for edited theme JSON files

=== Overlays / Mini Windows ===

  Grid        Cartesian complex-plane overlay
  Mini Map    Inset showing whole-set position of current view
  Mini Depth  Per-pixel iteration depth heat-map indicator
  Mini Mode   Shrink window to minimum size + on-top, borderless
  On Top      Keep main window above all others

=== Capture ===

  Screenshot  Single-frame PNG at panel resolution or oversampled
  Poster      Multi-tile composite render at print resolution
  Video       Animated keyframe zoom rendered via ffmpeg
  Video Slideshow
              Continuous zoom-out → next-region → zoom-in loop

=== Precision ===

  Double  (SP)    ~15 digits  — zoom ≤ ~1e13
  Double-Double   ~31 digits  — zoom ≤ ~1e25
  Quad-Double     ~62 digits  — zoom ≤ ~1e50+
  Auto-promotion crosses thresholds based on the active view.
  Perturbation theory (Series Approx. + BLA) accelerates deep zooms.
";

        public const string AudioText =
@"=== Audio-Reactive Slideshow ===

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

The Audio-Reactive checkbox in the Floating Menu (above the
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

A floating window that lets you create new color themes from scratch
or edit existing ones, with live preview into the main render window.

Open from the Floating Menu: Color Themes → ""Edit Theme…"" button.
The editor uses the currently-selected theme as its starting point.

=== Layout ===

  Left column   Target, Identity, Kind, Color Stops, Cycle,
                In-Set, Post-FX Defaults, action buttons
  Right column  3D Lighting (Phong/PBR), Phong3D Extras,
                Pbr3D Extras  (visible only when relevant Kind chosen)

=== Kind (theme type) ===

  Gradient   Linear gradient stretched once across the iteration range.
  Cycling    Gradient that wraps multiple times based on CycleSpeed.
  Phong3D    Cycling gradient with Blinn-Phong directional lighting.
  Pbr3D      Cycling gradient with Cook-Torrance PBR lighting.

=== Color Stops ===

  Position   Normalized [0, 1]. 0 = start (low iterations), 1 = end.
  Swatch     Click to open a color picker. RGB entry below also works.
  Minimum 2 stops required. Linear interpolation between consecutive.

=== Cycle (Cycling / Phong3D / Pbr3D) ===

Speed = repetition rate of the gradient across the iteration range.
Default 0.02 = roughly one full cycle every 50 smooth-units.

=== 3D Lighting (Phong3D + Pbr3D) ===

Steepness = Z-scale on the surface normal (relief depth).
Ambient   = base illumination before lighting.

  Key Light + Fill Light: each carries Dir(X,Y,Z), Diffuse RGB,
  Specular RGB, Shininess. Key = bright/white, Fill = dim/cool.
  Optional Rim Light for back-lighting silhouettes.

=== Pbr3D Extras ===

  Lighting mode:  PBRRealistic | PBRBright
  Glow exp / scl: additive emission near escape t=1.
  Material bands: piecewise (Metal, Roughness) over t.

=== In-Set (Interior) ===

Override the in-set color (default opaque black). Useful for themes
where black hides too much against a dark gradient.

=== Post-FX Defaults ===

Per-theme defaults for Brightness / Contrast / Adaptive sliders.
Locked sliders ignore these defaults on theme switch.

=== Live Preview & Actions ===

  Live preview   When ticked, edits push to the main render via a
                 150 ms debounce. Drag freely; calculator re-runs once.
  Apply          Force a push regardless of live-preview state.
  New Blank      Discard edits, start from a fresh Gradient (black→white).
  Revert         Reload from the last source theme name.
  Save to Library
                 Validates Name / ≥ 2 stops, then adds or replaces a
                 user theme in %APPDATA%\FracturingFog\colorthemes.json.
  Export JSON…   Writes a single-theme JSON array to disk.
  Save C#…       Writes a compilable C# class via the shared
                 ColorThemeCsExporter so the theme can ship built-in.

=== File Format ===

  Library file:  %APPDATA%\FracturingFog\colorthemes.json
  Source seed:   <install>\Resources\ColorThemes\colorthemes.json

  Each entry is a single ColorThemeData object. Fields are emitted
  with WhenWritingNull semantics — anything left null in the editor
  is omitted from the JSON entirely.
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
    }
}
