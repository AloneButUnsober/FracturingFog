# Fracturing Fog

![Fractal families](https://img.shields.io/badge/fractal%20families-~38-blue)
![Color themes](https://img.shields.io/badge/color%20themes-200%2B-purple)
![Platforms](https://img.shields.io/badge/platforms-Win%20%7C%20Linux%20%7C%20macOS-green)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

Real-time high-precision Mandelbrot explorer with audio-reactive
slideshow, palette tools, and cross-platform support on Windows, Linux,
and macOS.

* Full feature tour: [FEATURES.md](FEATURES.md)
* Per-OS install + caveats: [Docs/User/CrossPlatform-UserGuide.md](Docs/User/CrossPlatform-UserGuide.md)
* Avalonia shell tour: [Docs/User/Avalonia-UserGuide.md](Docs/User/Avalonia-UserGuide.md)
* Keyboard shortcuts: [Docs/User/Keyboard-Shortcuts.md](Docs/User/Keyboard-Shortcuts.md)

## Install

| OS                     | Download                                | After download                                                      |
|------------------------|-----------------------------------------|---------------------------------------------------------------------|
| Windows 10/11 (x64)    | `FracturingFog-win-x64.zip`             | Unzip + double-click `FracturingFog.App.exe`. Bundled ffmpeg.       |
| Linux (x64)            | `FracturingFog-linux-x64.AppImage`      | `chmod +x` + run. `sudo apt install ffmpeg` enables video export.   |
| Linux (arm64)          | `FracturingFog-linux-arm64.AppImage`    | Same as linux-x64.                                                  |
| macOS (Apple Silicon)  | `FracturingFog-osx-arm64.tar.gz`        | `tar xf` + drag `FracturingFog.app` into `/Applications/`. `brew install ffmpeg` enables video. |
| macOS (Intel)          | `FracturingFog-osx-x64.tar.gz`          | Same as osx-arm64.                                                  |

Archives ship on every tagged release; grab the latest from the
[Releases page](https://github.com/dpiserve/FracturingFog/releases).

Self-contained — no .NET runtime install is needed. macOS bundles are
not yet code-signed; right-click → Open the first launch so Gatekeeper
accepts them.

## Build from source

```
dotnet build FracturingFogCLD.csproj -c Release   # legacy WinForms shell (Windows)
dotnet build FracturingFog.App                    # cross-platform Avalonia shell
```

* .NET 10 SDK
* Avalonia 12 (Win + Linux + macOS)
* Vortice.Direct3D11 / 12 (Windows-only renderers)
* Silk.NET OpenGL (Linux + macOS, opt-in on Windows via `--renderer silk`)
* SkiaSharp 3 (Avalonia, exporters, CPU renderer)
* NAudio 2 (Windows audio capture)
* ILGPU 1.5 (GPU compute, CPU fallback everywhere)
* QuestPDF (palette PDF export)
* MathNet.Numerics (FFT beat analyzer)

See [CLAUDE.md](CLAUDE.md) for the WinForms-deprecation rule of thumb —
new UI work lands in `UI.Avalonia/`, not `MainForm.cs`.

## Status

| Phase    | What                                       | State    |
|----------|--------------------------------------------|----------|
| X.0      | Project geometry split                     | Shipped  |
| X.A      | Image I/O SkiaSharp swap                   | Shipped  |
| X.B      | Audio capture abstraction                  | Shipped  |
| X.3      | P/Invoke + IsOSPlatform sweep              | Shipped  |
| X.1      | Palette engine (QuestPDF + SkiaSharp)      | Shipped  |
| X.2      | Video export portability (ffmpeg)          | Shipped  |
| X.4      | Renderer selection + Wayland CI            | Shipped  |
| X.5      | Compute fallbacks (ILGPU device probe)     | Shipped  |
| X.6      | Packaging (AppImage + .app + workflow)     | Shipped  |
| X.7      | Documentation + UX                         | Shipped  |

Tracking branch: `feature/cross-platform-full`. Plan:
[Docs/Technical/CrossPlatform-ImplementationPlan.md](Docs/Technical/CrossPlatform-ImplementationPlan.md).

## License

See the repository's license file. Third-party dependencies:

* ffmpeg — GPL (Windows bundle); apt / brew installs follow the user's
  distro licence.
* QuestPDF — Community licence (free for OSS / sub-USD-1M revenue).
* Avalonia, Silk.NET, SkiaSharp, NAudio, ILGPU, Vortice.* — MIT.
