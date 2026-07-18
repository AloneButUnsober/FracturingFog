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

## UI status: Avalonia is canonical. WinForms is deprecated.

**All new UI work goes into `UI.Avalonia/`.** Do not add features, fix
non-critical bugs, or refactor inside `MainForm.cs`, `Views/` (the WinForms
`Views/`), or other Windows Forms code paths. WinForms is kept buildable
only as a fallback and for historical parity during the migration tail.

### What is the WinForms shell?

- Entry point: `Program.cs` → `--winforms` CLI flag → `Application.Run(new MainForm())`.
- Form code: `MainForm.cs`, `MainForm.resx`, `Views/**` (the in-root `Views`
  folder; the Avalonia view tree lives under `UI.Avalonia/Views/`).
- Hosting glue with `System.Windows.Forms` references: parts of `Hosting/`,
  `Imaging/` (`ImageCapture.cs`), some dialogs.

### What is the Avalonia shell? (active path)

- Default entry: `Program.cs` falls through to
  `FracturingFog.UI.Avalonia.AvaloniaShell.Run(...)` when no CLI flag matches.
- Code: everything under `UI.Avalonia/` (Views, ViewModels, Services).
- Cross-platform hosting glue: `Hosting/AvaloniaShellBootstrap.cs`,
  `Hosting/AvaloniaDialogs.cs`.

### Rules of thumb

1. **New feature?** Add it to `UI.Avalonia/` only. If it needs host services,
   wire them through `AvaloniaShellBootstrap` / `IPlatformHost`.
2. **Bug in both shells?** Fix the Avalonia side. WinForms gets the fix only
   if the bug is a crash/data-loss class issue.
3. **Touching `MainForm.cs` / WinForms `Views/`?** Stop and ask. Default
   answer is "don't."
4. **New project reference?** Should not require `UseWindowsForms` or
   `net*-windows`. Prefer adding the dependency to `UI.Avalonia.csproj`,
   not `FracturingFogCLD.csproj`.
5. **Removal of WinForms code is out of scope** for the deprecate branch —
   the goal is to *prevent new work* landing there. A future branch will
   rip the WinForms shell out once the Avalonia shell has full parity.

### Non-UI projects (no deprecation, work normally)

- `Abstractions/` — UI-free contracts.
- `Rendering.Silk/`, `Rendering.Silk.Smoke/`, `Rendering.Skia/` — backends.
- `Server/`, `Server.Tests/`, `Client/` — headless server + RPC client.
- `CalculatorGen/`, `ColorGen/` — codegen tools.
- `PaletteBuilder/` — standalone Avalonia palette tool + extraction lib.

### Build / run quick reference

- Solution: `FracturingFogCLD.sln` (root).
- Default run: `dotnet run --project FracturingFogCLD.csproj` → Avalonia shell.
- Legacy run: `dotnet run --project FracturingFogCLD.csproj -- --winforms`.
- Headless: `--server`, `--batch`, plus self-test flags in `Program.cs`.
