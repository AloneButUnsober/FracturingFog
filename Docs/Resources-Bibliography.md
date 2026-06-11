# Resources & Bibliography

Every piece of mathematics, every algorithm, every UI affordance in Fracturing Fog grew out of work
done by someone else. This page collects the references that informed the project — books, papers,
canonical websites, GitHub repositories, library docs, and useful explainers. Where the application
ships a particular algorithm or formula, you should be able to trace it from the relevant doc page
back to a citation here.

If you spot a missing reference, file an issue or open a pull request. Citations age — broken links
are flagged for fixing in [Documentation Plan → Citation maintenance](Documentation-Plan.md#citation-maintenance).

---

## Foundational mathematics

### Fractal geometry and complex dynamics

- **Benoit B. Mandelbrot.** *The Fractal Geometry of Nature.* W. H. Freeman, 1982. The book that
  coined the word "fractal" and gave the Mandelbrot set its first wide audience.
- **Adrien Douady, John H. Hubbard.** *Étude dynamique des polynômes complexes.* Publications
  mathématiques d'Orsay, 1984/1985. Proved the Mandelbrot set is connected; introduced the
  parameter-ray theory used to label hyperbolic components.
- **Heinz-Otto Peitgen, Peter H. Richter.** *The Beauty of Fractals: Images of Complex Dynamical
  Systems.* Springer-Verlag, 1986. The earliest accessible visual catalogue.
- **John W. Milnor.** *Dynamics in One Complex Variable.* Princeton University Press, 3rd ed., 2006.
  The modern standard reference for iterating rational maps on the Riemann sphere.
- **Wolf Jung.** *Mandel — Software for Complex Dynamics.* <https://mndynamics.com/indexp.html>.
  Reference tool for orbit-portrait and external-ray verification.

### Specific fractal families

- **Pierre Fatou, Gaston Julia.** *Sur les équations fonctionnelles.* Bull. Soc. Math. France, 1917-1919.
  The origins of complex iteration theory; the Julia sets are named here.
- **Michael F. Barnsley.** *Fractals Everywhere.* Academic Press, 2nd ed., 1993. Defines the chaos
  game and the iterated function systems used by the IFS renderer.
- **John Hutchinson.** *Fractals and self similarity.* Indiana University Mathematics Journal, 1981.
  Original IFS attractor theorem.
- **Shigehiro Ushiki.** *Phoenix.* IEEE Transactions on Circuits and Systems, 1988. The two-step
  memory recurrence used by the Phoenix family.
- **Michael Michelitsch, Otto E. Rössler.** *A New Feature in the Mandelbrot Set.* Computers & Graphics,
  1992. Defines the Burning Ship.
- **Daniel White.** *Triplex algebra and the Mandelbulb.*
  <https://www.skytopia.com/project/fractal/2mandelbulb.html>, 2007-2009. The Mandelbulb's
  triplex-power formula.
- **Paul Nylander.** *Hypercomplex fractals* gallery and notes.
  <https://www.bugman123.com/Hypercomplex/>. Companion to White's Mandelbulb explorations.
- **Melinda Green.** *Buddhabrot rendering technique.* <http://superliminal.com/fractals/bbrot/>, 1993.
- **Clifford A. Pickover.** *Computers, Pattern, Chaos, and Beauty.* St. Martin's Press, 1990. Source
  of the Clifford / De Jong attractor formulae used by the Strange Attractor family.

### Deep zoom acceleration

- **Kevin I. Martin.** *Superfractalthing: Mandelbrot Set Calculation in High Precision.*
  Bulletin of the Mandel-machine project, 2014. Original published perturbation + series
  approximation algorithm now widely re-implemented (including in this project).
- **Claude Heiland-Allen.** *MandelbrotPerturbator* implementation notes.
  <https://mathr.co.uk/mandelbrot/book-draft/>. Practical writeup of perturbation theory + bilinear
  approximation, glitch detection, and rebase reference selection.
- **Botond Kósa.** *KallesFraktaler2 — source and engineering blog.*
  <https://github.com/edyoung/kalles-fraktaler-2>. The de-facto reference open-source perturbation
  Mandelbrot renderer.
- **Yuhao Zhu.** *Higher-precision arithmetic via double-double and quad-double.* In particular
  *QD library* by Y. Hida, X. S. Li, D. H. Bailey
  (<https://www.davidhbailey.com/dhbsoftware/>). Foundation for the DD / QD pipeline.

### Distance estimation, normals, shading

- **John Hart.** *Sphere tracing: A geometric method for the antialiased ray tracing of implicit
  surfaces.* The Visual Computer, 1996. The original distance-estimation raymarcher.
- **Inigo Quilez.** *Distance estimation, ambient occlusion, soft shadows for raymarched fractals.*
  <https://iquilezles.org/articles/>. The canonical modern primer.
- **Robert L. Cook, Kenneth E. Torrance.** *A reflectance model for computer graphics.* SIGGRAPH 1982.
  Source for the PBR3D theme kind's microfacet shading.

---

## Colour theory and palettes

- **Garry T. Krollman.** *CIELAB and CIECAM02 conversions* — referenced by the from-image palette
  k-means sampler. Practical formulae from
  <https://www.brucelindbloom.com/index.html?Eqn_RGB_to_XYZ.html>.
- **Cynthia A. Brewer.** *ColorBrewer 2.* <https://colorbrewer2.org>. Reference for accessible
  sequential / diverging palettes, used for several built-in themes.
- **Peter Karpov.** *Improved orbit-trap colouring for Mandelbrot zoom videos.*
  <https://inversed.ru/Blog_2.htm>. Direct inspiration for the orbit-trap theme kind.
- **NASA Ames perceptual papers** on chromostereopsis (red/blue depth cue). Source for the
  Chromostereopsis theme kind.

---

## Audio analysis (audio-reactive slideshow)

- **Sebastian Böck, Markus Schedl.** *Maximum filter vibrato suppression for onset detection.*
  Proceedings of DAFx-13, 2013. Spectral-flux beat detection underpins the audio-reactive engine.
- **Brian C. J. Moore.** *An Introduction to the Psychology of Hearing.* Brill, 6th ed., 2012.
  Source for the band-weighted detector EQ (bass / lo-mid / mid / hi-mid / high) bands.

---

## Software, libraries, and tooling

### Direct dependencies (referenced from `*.csproj`)

| Library                | Purpose                                                                          | Project home                                                       |
|------------------------|----------------------------------------------------------------------------------|--------------------------------------------------------------------|
| Avalonia 12            | Cross-platform UI framework that hosts the active shell                          | <https://avaloniaui.net>                                           |
| Avalonia.Controls.ColorPicker | Colour swatch / colour wheel in the theme editor                          | <https://github.com/AvaloniaUI/Avalonia>                           |
| ReactiveUI.Avalonia    | MVVM with observable bindings; the project's primary VM idiom                    | <https://www.reactiveui.net>                                       |
| Vortice.Windows        | Native-friendly D3D11/12 + DXGI bindings                                         | <https://github.com/amerkoleci/Vortice.Windows>                    |
| Silk.NET               | Cross-platform OpenGL / Vulkan / Metal bindings (cross-platform render path)     | <https://github.com/dotnet/Silk.NET>                               |
| SkiaSharp              | Software / OpenGL Skia renderer                                                  | <https://github.com/mono/SkiaSharp>                                |
| ILGPU                  | C# → SPIR-V / PTX / OpenCL JIT for the GPU calculator path                       | <https://www.ilgpu.net>                                            |
| Roslyn (`Microsoft.CodeAnalysis.CSharp`) | C# script compilation for User Equation + theme C# export      | <https://github.com/dotnet/roslyn>                                 |
| Markdig                | Markdown → HTML for the static documentation site                                | <https://github.com/xoofx/markdig>                                 |
| NAudio                 | WASAPI loopback + microphone capture for the audio-reactive engine               | <https://github.com/naudio/NAudio>                                 |
| FFmpeg                 | Lossless and visually-lossless video encoding presets                            | <https://ffmpeg.org>                                               |
| KaTeX (CDN, web only)  | LaTeX rendering inside the static doc site                                       | <https://katex.org>                                                |
| Prism.js (CDN, web only) | Syntax highlighting inside the static doc site                                 | <https://prismjs.com>                                              |

### Algorithm references implemented in code

- **k-means in CIELAB** — Stuart P. Lloyd. *Least squares quantization in PCM.* IEEE Transactions on
  Information Theory, 1982. Underlies `PaletteBuilder.Lib`'s palette extractor and the theme editor's
  *From Image…* button.
- **Gaussian elimination with partial pivoting** — Numerical Recipes (Press et al., 3rd ed.). Used in
  the Newton calculator's polynomial-coefficient mode (roadmap).
- **Smoothstep / quintic Hermite interpolation** — Ken Perlin. *Improving noise.* SIGGRAPH 2002.
  Used for the slideshow cross-fade and the video-zoom easing curve.

---

## Sites, communities, and other long-form references

- **fractalforums.com** — long-running fractal-rendering community. Many of the perturbation and
  glitch-detection refinements implemented here were first vetted there.
- **mathr.co.uk** — Claude Heiland-Allen's writing on Mandelbrot rendering. Frequent first-stop
  reference.
- **iquilezles.org** — Inigo Quilez's articles on procedural graphics, raymarching, signed
  distance functions, noise.
- **shadertoy.com** — countless cross-reference shaders for the 3-D / distance-estimation pipeline.
- **scratchapixel.com** — Wikipedia-style explainer site for ray tracing, sampling, transforms.
- **redblobgames.com** — Amit Patel's interactive write-ups (hex grids, A*, easing). Indirect
  influence on UI animation design.

---

## How to cite

When introducing a piece of mathematics or an algorithm in a doc page, link inline like this:

```markdown
The smoothed escape count follows [(Linas Vepstas 1997)](../Resources-Bibliography.md#linas-vepstas-smooth-iter).
```

…and ensure the anchor exists here. If it does not yet, add it as a one-line stub under the right
section and let the next pass flesh it out. The cost of a stub is zero; the cost of an unverifiable
claim is the trust readers put in the doc.
