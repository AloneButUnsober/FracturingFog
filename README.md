# Fracturing Fog

![Fractal families](https://img.shields.io/badge/fractal%20families-~38-blue)
![Color themes](https://img.shields.io/badge/built--in%20themes-290%2B-purple)
![Platforms](https://img.shields.io/badge/platforms-Win%20%7C%20Linux%20%7C%20macOS-green)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

Real-time high-precision Mandelbrot explorer with audio-reactive
slideshow, palette tools, and cross-platform support on Windows, Linux,
and macOS.

* Full feature tour: [FEATURES.md](FEATURES.md)
* Documentation landing page: [Docs/_Index.md](Docs/_Index.md)
* Per-OS install + caveats: [Docs/User/CrossPlatform-UserGuide.md](Docs/User/CrossPlatform-UserGuide.md)
* Avalonia shell tour: [Docs/User/Avalonia-UserGuide.md](Docs/User/Avalonia-UserGuide.md)
* Keyboard shortcuts: [Docs/User/Keyboard-Shortcuts.md](Docs/User/Keyboard-Shortcuts.md)

## Install

| OS                     | Download                                | After download                                                      |
|------------------------|-----------------------------------------|---------------------------------------------------------------------|
| Windows 10/11 (x64)    | `FracturingFog-win-x64.zip`             | Unzip + double-click `FracturingFog.App.exe`. Video export needs ffmpeg (in-app auto-download, or on PATH). |
| Linux (x64)            | `FracturingFog-linux-x64.AppImage`      | `chmod +x` + run. `sudo apt install ffmpeg` enables video export.   |
| Linux (arm64)          | `FracturingFog-linux-arm64.AppImage`    | Same as linux-x64.                                                  |
| macOS (Apple Silicon)  | `FracturingFog-osx-arm64.tar.gz`        | `tar xf` + drag `FracturingFog.app` into `/Applications/`. `brew install ffmpeg` enables video. |
| macOS (Intel)          | `FracturingFog-osx-x64.tar.gz`          | Same as osx-arm64.                                                  |

Archives ship on every tagged release; grab the latest from the
[Releases page](https://github.com/AloneButUnsober/FracturingFog/releases).

Self-contained — no .NET runtime install is needed. macOS bundles are
not yet code-signed; right-click → Open the first launch so Gatekeeper
accepts them.

## Build from source

```
dotnet build FracturingFogCLD.csproj -c Release   # Windows build (D3D/Win backends)
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

The Avalonia shell (`UI.Avalonia/`) is the only UI; the legacy WinForms
shell was removed. See [CLAUDE.md](CLAUDE.md) for the rule of thumb — all
new UI work lands in `UI.Avalonia/`.

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

The cross-platform initiative (phases above) has shipped and is merged to
`main`. Plan:
[Docs/Technical/CrossPlatform-ImplementationPlan.md](Docs/Technical/CrossPlatform-ImplementationPlan.md).

## License

Fracturing Fog is licensed under the **GNU Affero General Public License v3.0
or later** (AGPL-3.0-or-later). See [`LICENSE`](LICENSE) for the full text.

In short: you are free to use, study, modify, and share this software, but any
derivative work — including a modified version made available to users **over a
network** (AGPL §13) — must be released under the same license with complete
corresponding source. If you distribute or host a modified build, you must make
your source available to its users.

Copyright © 2026 Bradley Brown.

### Affiliation

Fracturing Fog is an independent personal project by Bradley Brown (a.k.a.
DanarDalin). It is **not affiliated with, endorsed by, sponsored by, or a work
product of DPI Information Services, Inc. (dpiserve.com)**. The `@dpiserve.com`
address appearing in historical commit metadata reflects only the email
configured in the author's Git client at commit time and does not indicate any
corporate involvement, ownership, or endorsement. See [`DISCLAIMER.md`](DISCLAIMER.md)
for the full statement.

### Contributing

Contributions are welcome via pull request. All contributions are accepted under
the project's [Contributor License Agreement](CLA.md), which you accept by adding
a `Signed-off-by:` trailer to your commits (`git commit -s`). The CLA lets the
maintainer offer alternative (e.g. commercial) licenses in addition to the AGPL.

### Third-party components

Full attributions and dependency licenses are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Summary:

* **ffmpeg** — used as an external tool for video export, **not bundled**.
  Resolved at runtime from `PATH` / the app directory, or fetched on demand;
  Linux/macOS install via apt / brew. ffmpeg is GPL/LGPL; invoking it as a
  separate program is mere aggregation, not a derivative work.
* **QuestPDF** — Community licence (free for OSS / sub-USD-1M revenue).
* **Avalonia, Silk.NET, SkiaSharp, NAudio, ILGPU, Vortice.\***, Roslyn,
  MathNet.Numerics, Markdig — MIT / BSD / NCSA (permissive).
