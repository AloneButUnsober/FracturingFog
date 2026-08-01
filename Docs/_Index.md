# Fracturing Fog — Documentation

Welcome. This is the top-level landing page for **all** Fracturing Fog
documentation. The docs are split into two audiences — pick the one
that matches what you came for.

> Just want to install it? See the [project README](../README.md) for
> per-OS downloads and quick install steps, or the
> [Cross-Platform User Guide](User/CrossPlatform-UserGuide.md) for the
> per-OS caveats and gotchas.

---

## I want to *use* Fracturing Fog

You are an end user: you want to explore fractals, save views, paint
them, record videos, run slideshows, type your own equations into the
editor. Start here:

→ **[User Documentation Index](User/_Index.md)**

Highlights (full menu on the index page):

| Page                                                                                 | What it covers                                                       |
|--------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| [Avalonia User Guide](User/Avalonia-UserGuide.md)                                    | Every menu, button, and dialog in the main shell.                    |
| [Regions Guide](User/Regions-Guide.md)                                               | Save, recall, and organise favourite views.                          |
| [Capture Guide](User/Capture-Guide.md)                                               | Screenshots, posters, PNG sequences, MP4 video export.               |
| [Slideshow + Audio-Reactive Guide](User/Slideshow-AudioReactive-Guide.md)            | Cycle regions + themes on the beat with live audio.                  |
| [Scene Engine Guide](User/SceneEngine-UserGuide.md)                                  | Direct cinematic Scenes: timeline, flying camera, transitions, video.|
| [Colour Theme Editor Guide](User/ColorThemeEditor-Guide.md)                          | Live palette editing with the floating editor.                       |
| [ColorGen DSL Guide](User/ColorGen-UserGuide.md)                                     | One-line palettes in a tiny domain language.                         |
| [CalcGen / User Equation Guide](User/CalcGen-UserGuide.md)                           | Author your own fractal formula in C# or DSL pseudo-code.            |
| [User Bulb 3D Guide](User/UserBulb-Guide.md)                                         | Mandelbulb-style 3-D fractals with raymarched DEs.                   |
| [Volumetric Lighting Guide](User/Volumetric-Lighting-Guide.md) + [Cookbook](User/Volumetric-Lighting-Cookbook.md) | God rays, cinematic fog, volumetric clouds — controls + recipes.     |
| [Client / Server Guide](User/ClientServer-UserGuide.md)                              | Drive a heavy render from a thin client over mTLS.                   |
| [Server Admin Guide](User/ServerAdmin-Guide.md)                                      | Configure the local render server.                                   |
| [Cross-Platform User Guide](User/CrossPlatform-UserGuide.md)                         | Per-OS install + capability matrix (Windows / Linux / macOS).        |
| [Keyboard Shortcuts](User/Keyboard-Shortcuts.md)                                     | The whole cheat-sheet.                                               |

---

## I want to *build, port, or extend* Fracturing Fog

You are a developer or contributor: you want to read the source, ship
a new fractal family, hook into the perturbation pipeline, port to a
new GPU backend, or add a new shell. Start here:

→ **[Technical Documentation Index](Technical/_Index.md)**

Highlights (full menu on the index page):

| Page                                                                                                  | What it covers                                                                  |
|-------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------|
| [Architecture Overview](Technical/Architecture-Overview.md)                                           | One-page tour of the whole solution. Read first.                                |
| [Fractal Equation Design Guide](Technical/FractalEquation-DesignGuide.md)                             | How to add a new fractal family end-to-end.                                     |
| [CalculatorGen Architecture](Technical/CalculatorGen-Architecture.md) + [Authoring](Technical/CalculatorGen-Authoring.md) | Roslyn source-gen of perturbation calculators from DSL equations.   |
| [Performance Development Plan](Technical/Performance-DevelopmentPlan.md)                              | SIMD + DD/QD/OD precision + BLA + GPU JIT roadmap.                              |
| [Cross-Platform Roadmap](Technical/CrossPlatform-Roadmap.md) + [Implementation Plan](Technical/CrossPlatform-ImplementationPlan.md) | Linux / macOS port plan + per-RID smoke tests.                |
| [User Bulb 3D Development Plan](Technical/UserBulb3D-DevelopmentPlan.md) + [Sandbox DevPlan](Technical/UserBulbSandbox-DevPlan.md) | 3-D Mandelbulb engine + user-equation sandbox.            |
| [PHASE2 Avalonia Migration](Technical/PHASE2_AVALONIA_MIGRATION.md)                                   | WinForms → Avalonia migration record.                                           |
| [Cross-Platform Smoke Tests](Technical/CrossPlatform-SmokeTests.md)                                   | Per-phase manual verification matrix.                                           |

---

## Project-wide roadmaps + plan

These docs span both audiences — they describe what work is open, what
shipped, and where the project is going.

| Page                                                                       | Scope                                                                   |
|----------------------------------------------------------------------------|-------------------------------------------------------------------------|
| [Open Work Plan](Open-Work-Plan.md)                                        | Master execution plan rolling up every open item across every roadmap.  |
| [Performance Roadmap](Performance-Roadmap.md)                              | Tier 1 / 2 / 3 perf wins across the render pipeline.                    |
| [Lighting + FX Roadmap](Lighting-FX-Roadmap.md)                            | HDR DoF, bloom, GGX importance sampling, HDRI environments.             |
| [Fractal Expansion Roadmap](Fractal-Expansion-Roadmap.md)                  | New families (KIFS, L-systems, Apollonian, Flame, Bicomplex, …).        |
| [Animation Roadmap](Animation-Roadmap.md)                                  | Animated `FractalParameters`, Animation asset, Animation Slideshow.     |
| [Scene Engine Roadmap](Scene-Engine-Roadmap.md)                            | Cinematic Scenes: timeline + camera paths, resource governor, HW tiers. |
| [CalculatorGen Roadmap](Technical/CalculatorGen-Roadmap.md)                | Perturbation + SA + DD/QD/OD + cluster-rebase pipeline.                 |
| [Documentation Plan](Documentation-Plan.md)                                | What is still being written and how to contribute.                      |
| [Resources & Bibliography](Resources-Bibliography.md)                      | Citations for every formula, algorithm, and paper referenced in code.   |

---

## A note on the two-audience split

Every page links back to either `User/_Index.md` or `Technical/_Index.md`
through a "Companion pages" line at the top. If you wandered into a
page that turns out to be the wrong altitude for what you wanted to
read, follow that companion line back to the matching index and try
again from there.

If a page reads as if it should exist but doesn't, please open an issue —
the [Documentation Plan](Documentation-Plan.md) tracks what is queued.
