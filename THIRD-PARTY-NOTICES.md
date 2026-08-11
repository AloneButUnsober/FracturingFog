# Third-Party Notices

Fracturing Fog is licensed under the GNU Affero General Public License v3.0
(see [`LICENSE`](LICENSE)). It incorporates, references, or depends on the
third-party components and prior-art algorithms listed below. Each remains
under its own license; this file is provided for attribution and compliance.

This document is informational. In case of conflict, the individual component's
own license text (as distributed with that component) governs.

---

## 1. Runtime & build dependencies (NuGet)

All packages below are referenced as external NuGet dependencies (not vendored
into this repository). All are permissive (MIT / Apache-2.0 / BSD / NCSA) and
impose attribution-in-distribution only — none impose copyleft on Fracturing
Fog.

| Package | License | Project(s) |
|---------|---------|------------|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Controls.ColorPicker | MIT | UI.Avalonia, PaletteBuilder |
| ReactiveUI.Avalonia | MIT | UI.Avalonia, PaletteBuilder |
| SkiaSharp | MIT | Engine, Rendering.Skia, FracturingFog.Win, PaletteBuilder.Lib |
| Silk.NET.* (Core, OpenGL, Windowing, Input, GLFW extensions) | MIT | Rendering.Silk |
| Vortice.* (Direct3D11/12, DirectX, DXGI, D3DCompiler, Mathematics) | MIT | Rendering.D3D, FracturingFogCLD |
| NAudio | MIT | Audio, Audio.Win, FracturingFogCLD |
| MathNet.Numerics | MIT | Audio, PaletteBuilder.Lib |
| Microsoft.CodeAnalysis.CSharp / .Scripting (Roslyn) | MIT | Engine, CalculatorGen.*, ColorGen.Lib |
| BenchmarkDotNet | MIT | FracturingFogCLD, Benchmarks |
| Markdig | BSD-2-Clause | Tools/DocSiteGen |
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 / MIT | Server.Tests |
| Microsoft.NET.Test.Sdk | MIT | Server.Tests |
| System.Drawing.Common | MIT | FracturingFog.Win |
| ILGPU | NCSA (University of Illinois/NCSA Open Source License) | Engine, Compute.Smoke, FracturingFogCLD |
| **QuestPDF** | **QuestPDF Community License** (see note below) | PaletteBuilder.Lib |

### QuestPDF — dual-license note

QuestPDF is distributed under the **QuestPDF Community License**: free (MIT-like)
for individuals and for companies/organizations with annual gross revenue below
USD $1,000,000; a paid Professional/Enterprise license is required above that
threshold. This obligation is independent of Fracturing Fog's own AGPL-3.0
license and applies to the *user/distributor* of QuestPDF.

QuestPDF is used only in `PaletteBuilder.Lib` for PDF export of palette sheets.
The Fracturing Fog maintainer's use currently falls under the free Community
tier. Downstream distributors or forks that exceed the revenue threshold are
responsible for obtaining a commercial QuestPDF license, isolating, or removing
the dependency.

See <https://www.questpdf.com/license/> for current terms.

---

## 2. Prior-art algorithms & techniques

The following are **algorithms, mathematical methods, and rendering techniques**
described in academic papers, technical reports, or public articles. Algorithms
and mathematical methods are not themselves copyrightable; the implementations
in this repository are original code written for Fracturing Fog. These entries
are provided as scholarly attribution, not because any code was copied.

- **Double-Double / Quad-Double arithmetic**
  Y. Hida, X. S. Li, D. H. Bailey — *"Algorithms for Quad-Double Precision
  Floating Point Arithmetic"*, Lawrence Berkeley National Laboratory Technical
  Report (2000/2007), and the accompanying `qd` reference library.
  Implemented independently in `Abstractions/Math/DoubleDouble.cs` and
  `Abstractions/Math/QuadDouble.cs`.

- **Perturbation-theory Mandelbrot rendering (arbitrary-precision deep zoom)**
  K. I. Martin — *SuperFractalThing* method, as described at
  <https://www.fractalforums.com/announcements-and-news/superfractalthing-arbitrary-precision-mandelbrot-set-rendering-in-java/>.
  Provides the reference-orbit + per-pixel delta approach used in the deep-zoom
  calculators.

- **Bilinear Approximation (BLA) for perturbation acceleration**
  Method popularized on fractalforums.com and in the Imagina / Kalles Fraktaler
  lineage. Implemented independently in `Engine/Math/Bla.cs`.

- **Analytic surface normals & exterior distance estimation, soft shadows**
  Techniques after Inigo Quilez (`z · conj(dz/dc)` normals; soft-shadow
  sharpness), from his public articles on distance-estimated fractals and
  lighting. Used in the CalculatorGen shading templates and lighting pipeline.

- **Milnor / Hubbard derivative recurrences** for exterior distance, referenced
  in the CalculatorGen differentiator.

- **Acid Warp palette-cycling pattern field**
  Inspired by Noah Spurrier's 1992 DOS *Acid Warp* demo (modern SDL/Emscripten
  port by Boris Gjenero / dreamlayers). The upstream acidwarp is **GPL-licensed**
  (a GPL-2.0-only upstream would *not* be license-compatible with AGPL-3.0). The
  Fracturing Fog implementation is a **clean-room** reimplementation of the
  *mathematics only* — the closed-form pattern maps (plasma sums, radial/angular
  sine interference, XOR fields) reimplemented fresh in C#. **No acidwarp source
  code, precomputed lookup tables (`lut_sin` / `lut_dist` / `lut_angle`), or
  palette data were copied.** Pattern equations and math are not copyrightable,
  so the upstream GPL does not attach to this original code. Implemented in
  `Engine/Calculators/AcidWarpCalculator.cs` (see also
  `Engine/Models/ColorSchemes/AcidWarpSpectrum.cs` and
  `Docs/Technical/AcidWarp-Mode-Design.md` §0 licensing gate). Credit to Noah
  Spurrier (original concept) and Boris Gjenero (modern port) is given as a
  courtesy.

---

## 3. External binaries (not compiled in)

- **FFmpeg** — invoked as an external executable for video/frame encoding, and
  optionally bundled with Windows releases. FFmpeg is distributed under the
  **GPL** (or LGPL depending on build). AGPL-3.0 is GPLv3-compatible, so shipping
  an unmodified FFmpeg binary alongside Fracturing Fog is a compatible aggregate
  ("mere aggregation"), not a derivative work. On Linux/macOS the user's
  apt/brew install is governed by their distribution's packaging. Bundlers must
  ship FFmpeg's own license and, for GPL builds, its corresponding source offer.
  See <https://ffmpeg.org/legal.html>.

## 4. Fonts

- **Inter** (bundled via `Avalonia.Fonts.Inter`) — SIL Open Font License 1.1.

---

*Maintainers: when adding or removing a dependency, update this file. When
adapting an algorithm from a published source, add a scholarly attribution
entry to Section 2.*
