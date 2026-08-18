# Fracturing Fog — Technical Documentation

This is the contributor / developer entry point. For end-user docs, see the
[User Index](../User/_Index.md). For a top-level router that bridges both audiences plus
the project-wide roadmaps, see [Docs Index](../_Index.md).

Fracturing Fog is a Windows-first, cross-platform-foundation Avalonia application that wraps a
DirectX 11/12 render surface, a SIMD + double-double + quad-double maths pipeline, an ILGPU GPU
JIT path, a Roslyn-backed equation compiler, a mutual-TLS render server, and a 200-palette colour
system. The codebase is a single .NET 10 solution.

---

## Solution layout (at a glance)

| Project                          | Role                                                                                  |
|----------------------------------|---------------------------------------------------------------------------------------|
| `FracturingFogCLD.csproj`        | Top-level WinExe. Bootstraps WinForms shell *(deprecated)* or Avalonia shell.         |
| `UI.Avalonia/`                   | **Active UI.** Avalonia 12 + ReactiveUI MVVM shell. All new UI work lands here.       |
| `Abstractions/`                  | UI-free contracts shared by every project.                                            |
| `Rendering.Silk/`                | Cross-platform GPU rendering scaffolding (Silk.NET).                                  |
| `Rendering.Silk.Smoke/`          | Smoke-test runner for the cross-platform backend.                                     |
| `Rendering.Skia/`                | Skia software / OpenGL rendering path.                                                |
| `Server/`                        | mTLS JSON-RPC render server (headless).                                               |
| `Server.Tests/`                  | xUnit coverage for the server.                                                        |
| `Client/`                        | mTLS JSON-RPC client + AES-GCM credential vault.                                      |
| `CalculatorGen/`                 | Library + tool that compiles user-supplied fractal equations into native code.        |
| `ColorGen/`                      | Library + tool that compiles ColorGen DSL palette source into runtime themes.         |
| `PaletteBuilder/`                | Standalone Avalonia palette-authoring tool + extraction library.                      |
| `Tools/DocSiteGen/`              | Markdown → static HTML site generator for these docs.                                 |

---

## Where to start reading

| If you are…                                                | Read first                                                  |
|------------------------------------------------------------|-------------------------------------------------------------|
| New to the codebase                                        | [Architecture Overview](Architecture-Overview.md)           |
| Adding a new fractal family                                | [Fractal Equation Design Guide](FractalEquation-DesignGuide.md) |
| Touching the calculator generator                          | [CalculatorGen Architecture](CalculatorGen-Architecture.md) and [Authoring](CalculatorGen-Authoring.md) |
| Tracking the GPU JIT / perturbation roadmap                | [Performance Development Plan](Performance-DevelopmentPlan.md) |
| Porting away from Direct3D                                 | [Cross-Platform Roadmap](CrossPlatform-Roadmap.md) and [Implementation Plan](CrossPlatform-ImplementationPlan.md) |
| Extending the 3-D Mandelbulb / User Bulb engine            | [User Bulb 3D Development Plan](UserBulb3D-DevelopmentPlan.md) and [Sandbox](UserBulbSandbox-DevPlan.md) |
| Adding non-escaping / user-supplied DE (pseudo-Kleinian, Amoser sine) | [Non-Escaping DE Dev Plan](NonEscaping-DE-DevPlan.md) |
| Building the Region Editor (Animation Roadmap Sub-goal B)   | [Region Editor Dev Plan](RegionEditor-DevPlan.md) |
| Building the cross-asset Asset Manager (Sub-goal A, deferred) | [Asset Manager Dev Plan](AssetManager-DevPlan.md) |
| Working on cinematic Scenes — camera track, timeline, offline render | [Scene Engine Architecture](SceneEngine-Architecture.md) |
| Planning FF's move deeper into 3D — AOV passes, linear/tonemap, camera, denoise | [3D Rendering Roadmap](3D-Rendering-Roadmap.md) (parent issue #389) |
| Growing PaletteBuilder into a perceptual, colorblind-first color assistant | [PaletteBuilder Design](PaletteBuilder-Design.md) (roadmap S10, issue #392) |
| Adding the Acid Warp palette-cycling mode + color-motion ideas | [Acid Warp Mode Design](AcidWarp-Mode-Design.md) |
| Building the distributed master/worker rendering cluster   | [Distributed Rendering Development Plan](DistributedRendering-DevelopmentPlan.md) — phase notes: [D-1](D-1-Session-Notes.md), [D-2](D-2-Session-Notes.md) |
| Maintaining the docs themselves                            | [Documentation Plan](../Documentation-Plan.md)              |
| Citing the maths behind a piece of code                    | [Resources & Bibliography](../Resources-Bibliography.md)    |

---

## Build / run cheatsheet

```powershell
# Full solution
dotnet build FracturingFogCLD.sln -c Release

# Avalonia shell (default)
dotnet run --project FracturingFogCLD.csproj

# Legacy WinForms shell (deprecated but still wired)
dotnet run --project FracturingFogCLD.csproj -- --winforms

# Headless render server
dotnet run --project FracturingFogCLD.csproj -- --server

# Headless batch render
dotnet run --project FracturingFogCLD.csproj -- --batch --help

# Regenerate this site
dotnet run --project Tools/DocSiteGen
```

---

## Conventions for technical docs

1. **Code blocks must compile or be clearly marked as pseudo-code.** Reach for fenced ` ```csharp `,
   ` ```hlsl `, ` ```text `, etc., so the static-site syntax highlighter and the in-app viewer can
   colour them correctly.
2. **LaTeX is allowed.** Inline `$\\alpha + \\beta$`, blocks delimited by `$$ ... $$`. The web export
   runs KaTeX over both; the in-app viewer keeps the raw source so users can copy it.
3. **Cite sources** when introducing a formula. Add a permanent link to a paper, textbook, or canonical
   resource. The bibliography file is the right place for the actual citation; cross-link from the
   prose using `[Author Year](../Resources-Bibliography.md#anchor)`.
4. **Show diagrams.** SVG preferred (smaller, editable). The `Docs/Images/diagrams/` folder is the home.
5. **Be verbose.** Technical docs are reference material, not tweets. Pour on the worked examples.
