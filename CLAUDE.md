# Fracturing Fog — Project Notes for Claude

## Dev tracking: use GitHub issues

**Prefer the GitHub issues list for dev tracking whenever possible.** New work,
bugs, spikes, and multi-slice plans get filed as issues (`gh issue create`)
rather than tracked only in scratch notes or ad-hoc TODOs.

- Multi-phase work: one tracking issue per slice, with dependencies stated in
  the body (repo has no labels/milestones/auto-blocking configured).
- A companion design/dev doc under `Docs/Technical/` may back a plan, but the
  issues are the canonical task list — link doc ↔ issues both ways.
- Repo: `AloneButUnsober/MandelbrotExplorer`. `gh` is authenticated with admin.

## UI status: Avalonia is the only shell. WinForms was removed (#116).

**All UI work goes into `UI.Avalonia/`.** The legacy WinForms shell —
`MainForm.cs` + partials (`VideoZoom.cs`, `Slideshow.cs`, `SlideshowConfig.cs`,
`AudioReactive.cs`, `ImageCapture.cs`), `MainForm.resx`, the in-root `Views/**`
tree, and the `--winforms` launch flag — was deleted in #116. The root
`FracturingFogCLD.csproj` no longer sets `UseWindowsForms`; there is no
`System.Windows.Forms` reference anywhere in the codebase.

### What is the Avalonia shell? (the only path)

- Entry: `Program.cs` → `FracturingFog.UI.Avalonia.AvaloniaShell.Run(...)`
  (after the headless/self-test CLI flags). There is no UI fallback.
- Code: everything under `UI.Avalonia/` (Views, ViewModels, Services).
- Cross-platform hosting glue: `Hosting/AvaloniaShellBootstrap.cs`,
  `Hosting/AvaloniaDialogs.cs`.

### About the TFM (why the WinExe is still net10.0-windows)

Removing WinForms did **not** move `FracturingFogCLD.csproj` off
`net10.0-windows`. This exe is the **Windows build**: it ProjectReferences the
Windows-only backends `Rendering.D3D` / `FracturingFog.Win` / `Audio.Win` (all
`net10.0-windows`), which a plain `net10.0` assembly cannot reference.
Cross-platform hosting is `FracturingFog.App`'s job (`net10.0` leg, Silk/Skia).
So `net*-windows` on the WinExe is expected — it is the Windows target, not a
WinForms artifact.

### Rules of thumb

1. **New feature?** Add it to `UI.Avalonia/` only. If it needs host services,
   wire them through `AvaloniaShellBootstrap` / `IPlatformHost`.
2. **Never reintroduce `System.Windows.Forms`** or `UseWindowsForms` on the
   WinExe / Avalonia path. Host dialogs go through `AvaloniaDialogs`; screen /
   Win32 needs live in `FracturingFog.Win` (net10.0-windows, no WinForms).
3. **New project reference?** Prefer adding the dependency to
   `UI.Avalonia.csproj`. Windows-only backends belong behind the
   `FracturingFog.Win` / `Rendering.D3D` boundary, loaded via bootstrap.

### Non-UI projects (no deprecation, work normally)

- `Abstractions/` — UI-free contracts.
- `Rendering.Silk/`, `Rendering.Silk.Smoke/`, `Rendering.Skia/` — backends.
- `Server/`, `Server.Tests/`, `Client/` — headless server + RPC client.
- `CalculatorGen/`, `ColorGen/` — codegen tools.
- `PaletteBuilder/` — standalone Avalonia palette tool + extraction lib.

### Build / run quick reference

- Solution: `FracturingFogCLD.sln` (root).
- Default run: `dotnet run --project FracturingFogCLD.csproj` → Avalonia shell.
- Headless: `--server`, `--batch`, plus self-test flags in `Program.cs`.
