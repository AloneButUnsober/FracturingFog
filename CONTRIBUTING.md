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

## Source citation contract

Fracturing Fog lives on publicly available work by the mathematics and
fractal-rendering community: papers, books, forum threads, blog posts, open
source renderers. Crediting that work is a standing contract with the
community. It is not optional and it does not expire. A change that breaks it
is not merged.

1. **Credit every source in the same change.** If code, a doc, a help text, a
   preset or a commit leans on someone else's formula, algorithm, constant,
   fit, technique, data, file format or write-up, credit it in
   [`Docs/Resources-Bibliography.md`](Docs/Resources-Bibliography.md) in the
   same pull request. That holds for clean-room work and for "inspired by".
   Name the author (or forum handle), the title, where it was published and
   the year, plus a URL where one exists. Say which part of Fracturing Fog
   uses it.
2. **Cite at the point of use too.** Name the source in the code comment or
   doc where the work is used (`// after Keinert et al. 2014`), so a reader
   can trace it to the bibliography.
3. **Never remove a credit.** A credit can be corrected, expanded or moved.
   It is never deleted, even when the feature that used it is removed. When
   details are uncertain, the entry goes under *Citations awaiting
   verification* and stays until it is traced. A credit is never dropped for
   being incomplete. When a confirmed correction changes an entry (a wrong
   author, URL or anchor), record the old form under *Corrected
   attributions* in the bibliography, with the date, PR and reason. Renamed
   anchors keep their old `<a id>`.
4. **Don't overstate originality.** Describe Fracturing Fog's own
   contribution only after checking prior art, and say what it builds on (see
   the novelty research in `Docs/Technical/DualOrbit-Coloring-RnD.md` §8).
5. **Code licences stay separate.** Third-party *code* or assets also need
   their licence and attribution in
   [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The bibliography credits
   *ideas*; the notices file covers *copyright*. Many sources need both.

**The guard.** `Tools/CitationGuard/citation_guard.py` runs on every pull
request (`.github/workflows/citation-guard.yml`) and fails when:

- a scholarly-style citation in the code or docs names someone the
  bibliography doesn't mention;
- an external URL points at a host the bibliography doesn't mention;
- a `Resources-Bibliography.md#anchor` link is broken;
- a credit present on the base branch has been removed.

Run it locally before pushing:

```bash
python Tools/CitationGuard/citation_guard.py --base origin/main
```

The guard only catches what it can see. A source used without naming it
anywhere is still a breach of this contract. Words the guard flags that are
not citations (a fixture URL, an XML namespace) go in
`Tools/CitationGuard/allowlist.txt` with a reason. A real source never goes
there.

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
