# Fracturing Fog — Project Notes for Claude

## Git commit identity — ALWAYS ABUDev, NEVER dpiserve

**Every commit MUST be authored and committed as
`ABUDev <56877581+AloneButUnsober@users.noreply.github.com>`.**

NEVER commit (or author) as `Bradley Brown <bradley.brown@dpiserve.com>` (the
"bbrowndpi" account) — that DPI-associated identity is deliberately kept out of
this project (see `DISCLAIMER.md`). The machine's *global* git config defaults to
the dpiserve email, so a repo-**local** override pins the clean identity. Before
committing, verify `git config user.email` shows the noreply address; if the
local override is missing, re-add it — never commit with the dpiserve identity.

### NO `Co-Authored-By: Claude` trailer (CLA gate)

**Do NOT add a `Co-Authored-By: Claude ...` trailer to commit messages in this
repo.** This OVERRIDES the default Claude Code convention. The `cla-assistant`
CI check treats every commit co-author as a contributor and hard-fails the PR
when `noreply@anthropic.com` (never a CLA signatory) appears, blocking merge.
Commits stay single-author `ABUDev`. (A `🤖 Generated with Claude Code` line in
the PR *body* is fine — the CLA bot only parses commit authors/co-authors, not
the PR description.) If a trailer slips in, strip it before pushing:
`FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --msg-filter 'grep -v "Co-Authored-By: Claude"' main..HEAD`.

## Dev tracking: use GitHub issues

**Prefer the GitHub issues list for dev tracking whenever possible.** New work,
bugs, spikes, and multi-slice plans get filed as issues (`gh issue create`)
rather than tracked only in scratch notes or ad-hoc TODOs.

- Multi-phase work: one tracking issue per slice, with dependencies stated in
  the body (repo has no labels/milestones/auto-blocking configured).
- A companion design/dev doc under `Docs/Technical/` may back a plan, but the
  issues are the canonical task list — link doc ↔ issues both ways.
- Repo: `AloneButUnsober/FracturingFog`. `gh` is authenticated with admin.

## Keep batch processing in step with every feature

**Whenever a feature is introduced or changed, update the batch-processing
(headless `--batch` CLI) library as required, in the same change.** Anything
that alters what gets rendered — a fractal type or parameter, an effect, a
lighting / relief / post-FX option, an animation or export capability — must
stay reproducible headless and through the Control Center **Command** builder
(#64). A live-only feature that the batch silently drops is a bug.

For each such change, check and update as needed:

1. **Render path** — the headless renderer actually honours it
   (`Batch/BatchRenderer.cs` → `BuildFractalParameters`, `Engine/Imaging/PosterRenderer.cs`,
   video / slideshow / scene loops). Verify, don't assume.
2. **Expressible on the command line**, via the cheapest fitting route:
   - a new per-family parameter → captured by the region snapshot
     (`RegionFractalParams` in `Engine/Models/FractalRegion.cs`), which makes it
     `--param Key=Value` automatically (and keeps #961 region coverage);
   - a new Lighting & FX field → `LightingFxData` / `LightingFxPresetData`, carried by
     `--lighting-preset`; if it deserves its own flag, add it to the covered set in
     `Abstractions/Cli/LightingFidelity.cs`;
   - otherwise a dedicated flag: `BatchFlags` const → `BatchOptions` parse +
     validate → `BatchRenderer` apply → `BatchFlagCatalog` entry (kind, range,
     modes, implies / requires / conflicts). The catalog guard tests fail until all
     of these agree, and `--batch --help` is generated from the catalog.
3. **Command builder** — the live-view seed emits it (`BatchCommandBuilder` +
   `ControlCenterViewModel.BuildLiveSeed`), with a parse round-trip test.
4. **If it truly can't be expressed yet** — report it as a fidelity gap in
   `BatchCommandBuilder.DetectGaps` (yellow banner) and file a GitHub issue; never
   drop it silently.

## UI status: Avalonia is the only shell. WinForms was removed (#116).

**All UI work goes into `UI.Avalonia/`.** The legacy WinForms shell —
`MainForm.cs` + partials (`VideoZoom.cs`, `Slideshow.cs`, `SlideshowConfig.cs`,
`AudioReactive.cs`, `ImageCapture.cs`), `MainForm.resx`, the in-root `Views/**`
tree, and the `--winforms` launch flag — was deleted in #116. The root
`FracturingFogCLD.csproj` no longer sets `UseWindowsForms`; there is no
`System.Windows.Forms` reference anywhere in the codebase.

### Main menu = Control Center. FloatingMenu view is DEPRECATED.

**The Control Center (`UI.Avalonia/Views/ControlCenterView.axaml` +
`ViewModels/ControlCenterViewModel.cs`) is the main menu form going forward.**
The old `FloatingMenuView` window is retired (rc3-ui-prep): the toolbar **Menu**
button, the **M** hotkey, and the render-window right-click all open the Control
Center now. No UI path opens `FloatingMenuView` anymore.

- **Do NOT make changes to `FloatingMenuView.axaml` / `.axaml.cs`** or re-wire it
  back into the UI. Add new menu surfaces as Control Center sections
  (`UI.Avalonia/Views/ControlCenterSections/*`) instead.
- **`FloatingMenuViewModel` lives on** — it is the shared command/state hub the
  Control Center binds through (`ControlCenterViewModel.Menu = shell.FloatingMenu`)
  and the shell wires many events on it. That VM is fine to touch; the *view* is not.
- `ShowFloatingMenuCommand` + `ShellViewModel.IsFloatingMenuVisible` +
  `MainWindow.SyncMenu` are dormant/deprecated; leave them, don't re-bind.

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
