# Contributing to Fracturing Fog

Thanks for your interest in contributing. This is a solo-maintained project;
contributions are welcome, and a little up-front alignment keeps things smooth.

## License & the CLA

Fracturing Fog is licensed under **AGPL-3.0-or-later** (see [`LICENSE`](LICENSE)).

All contributions are accepted under the project's
[Contributor License Agreement](CLA.md). You accept it by signing off your
commits — add a `Signed-off-by:` trailer with:

```bash
git commit -s
```

The sign-off certifies you wrote the change (or have the right to submit it) and
agree to the CLA. The CLA lets the maintainer offer alternative (e.g. commercial)
licenses alongside the AGPL. Pull requests without a sign-off can't be merged.

## Before you start

- **Open or find an issue first.** Dev work is tracked in
  [GitHub issues](https://github.com/AloneButUnsober/FracturingFog/issues). For
  anything beyond a trivial fix, file an issue (or comment on an existing one) so
  we can agree on the approach before you invest time. Multi-phase work gets a
  tracking issue per slice.
- **Read [`CLAUDE.md`](CLAUDE.md).** It captures the load-bearing project rules —
  most importantly the UI boundary below.

## Project rules of thumb

- **All UI work lands in `UI.Avalonia/`.** The Avalonia shell is the only UI;
  the legacy WinForms shell was removed. Do not reintroduce
  `System.Windows.Forms`. Host services wire through
  `AvaloniaShellBootstrap` / `IPlatformHost`.
- **Prefer the DSL over raw code.** User-authored fractal formulas and color
  logic go through the constrained DSL, not raw C# execution. Keep it that way —
  don't add raw-code-execution paths.
- **Expose tunables as parameters.** New hardcoded numeric constants should
  surface as `FractalParameters` fields with matching UI controls where it makes
  sense.
- **ASCII-only** for region/theme/scene names (back-compat aliases handle
  renames).

## Build & test

```bash
# Windows build (D3D/Win backends)
dotnet build FracturingFogCLD.csproj -c Release

# Cross-platform Avalonia shell
dotnet build FracturingFog.App

# Run the test suite
dotnet test
```

Please make sure the build is green and relevant tests pass before opening a PR.
Add tests for new behavior where practical.

## Pull requests

- Branch off `main`; keep PRs focused on one logical change.
- Reference the issue the PR addresses. If it should close an issue, add an
  explicit `Closes #N` line per issue (ranges and mentions do not auto-close).
- Write a clear description of **what** changed and **why**.
- CI runs a secret-scanning gate (gitleaks), a cross-platform build, and the CLA
  check. Green CI + sign-off + review are required to merge.

## Security issues

Do **not** open a public issue for vulnerabilities — see
[`SECURITY.md`](SECURITY.md) for private reporting.

## Code of conduct

Be respectful and constructive. Harassment or hostile behavior isn't welcome.
Report conduct concerns privately via the maintainer's GitHub profile
[@AloneButUnsober](https://github.com/AloneButUnsober).
