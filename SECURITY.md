# Security Policy

## Supported versions

Fracturing Fog is pre-1.0. Security fixes land on `main` and ship in the next
tagged release. Only the latest release and `main` are supported — please
reproduce any issue against one of those before reporting.

| Version            | Supported          |
|--------------------|--------------------|
| latest release     | :white_check_mark: |
| `main` (unreleased)| :white_check_mark: |
| older releases     | :x:                |

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report privately through GitHub's **[Private vulnerability reporting](https://github.com/AloneButUnsober/FracturingFog/security/advisories/new)**
(Security tab → "Report a vulnerability"). This keeps the details confidential
until a fix is available.

If private reporting is unavailable to you, contact the maintainer through the
GitHub profile [@AloneButUnsober](https://github.com/AloneButUnsober) and ask for
a private channel before sharing any details.

Please include:

- affected version / commit and platform (Windows, Linux, macOS)
- a description of the issue and its impact
- reproduction steps or a proof of concept, if you have one

### What to expect

- **Acknowledgement:** within about 7 days.
- **Assessment & fix:** as a solo-maintained project, timelines are best-effort;
  you'll get an honest estimate once the report is triaged.
- **Disclosure:** coordinated. The fix is released first; credit is given to the
  reporter unless you prefer to remain anonymous.

## Scope notes

Some context that helps triage:

- **User-supplied expressions / DSL.** Fracturing Fog evaluates user-authored
  fractal formulas and color code through a constrained DSL rather than raw
  code execution. Sandbox-escape or arbitrary-code-execution findings in that
  surface are in scope and of high interest.
- **Headless server mode** (`--server`) accepts network connections. Auth,
  transport, and input-handling issues there are in scope.
- **Third-party dependencies.** Vulnerabilities in bundled or referenced
  third-party components (see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md))
  are best reported upstream, but let us know so we can bump or mitigate.

Thank you for helping keep the project and its users safe.
