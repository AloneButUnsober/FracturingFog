# S-X10 — Color Theme Editor Eyedropper Fixes (2026-06-27)

## Done

### S-X10a — diagnostic logging + grab-retry (`Hosting/X11ColorSampleBridge.cs`, `Hosting/AvaloniaShellBootstrap.cs`)

Bridge was silent on every failure path, so the Linux Sample button
no-op'd with no hint why. Added:

- Stderr log lines (with `Flush`) at every branch: handler entry, bridge
  null/active, pump start, `XOpenDisplay`, `XCreateFontCursor`,
  `XGrabPointer` rc + retry exhaust, `ButtonPress` receipt,
  `TrySampleRoot` result, 30 s deadline w/ no events, pump exceptions.
- `XGrabPointer` retry loop (20 × 25 ms). The eyedropper Button's
  `PointerPressed` leaves Avalonia's X client in an implicit pointer
  grab until `PointerReleased` dispatches; a separate-connection
  `XGrabPointer` on root races against that and the server returns
  `AlreadyGrabbed` (1) until Avalonia releases.

### S-X10b — leaf-window walk for composited X11 (`Hosting/X11ColorSampleBridge.cs`)

`XGetImage` on root returned null on the user's KDE/composited desktop
(coords like `(2767, 489)` on a multi-monitor span). Composited window
managers redirect every top-level into off-screen pixmaps; the root's
own pixmap is then blank or unreadable.

Fix: walk `XTranslateCoordinates` from root down to the leaf descendant
under the click and `XGetImage` that window in its local coord system.
Top-level windows still have live pixmaps the compositor reads from, so
software- or X11-GL-rendered windows yield real pixels. Root XGetImage
stays as the last-resort fallback for uncomposited WMs.

User confirmed pick now works for any window the app itself is on.

---

## Deferred — desktop-wide pixel sampling parity with the legacy WinForms eyedropper

User reports: legacy WinForms `DesktopEyedropper` (`Views/Editors/DesktopEyedropper.cs`)
could sample any pixel on the desktop — other apps, taskbar, wallpaper.
The new Avalonia-era bridges (`WindowsColorSampleBridge`,
`X11ColorSampleBridge`) only return correct pixels for windows the running
app draws. Confirmed on both Windows and Linux.

### Roots

- **Windows** — `WindowsColorSampleBridge.SamplePixel` uses
  `GetDC(NULL) + GdiGetPixel`. Under DWM compositing on Win 8+,
  `GetPixel` on the screen DC returns app-window-only / stale pixels for
  many surfaces. Legacy `DesktopEyedropper.SamplePixel` used
  `Graphics.CopyFromScreen` (`BitBlt(SRCCOPY|CAPTUREBLT)`), which the DWM
  forwards correctly.

  **Fix path:** swap `GetPixel` for a `BitBlt` to a 1×1 compatible bitmap
  + `GetDIBits`, OR pull `System.Drawing.Graphics.CopyFromScreen` since
  `FracturingFog.Win` already references `System.Drawing.Common`. Mirrors
  the legacy code 1:1.

- **Linux** — `X11ColorSampleBridge.TrySamplePoint` reads the leaf X11
  window's pixmap. Works for software-drawn windows and X11-GL windows
  whose compositor backing is a server-side pixmap (incl. the
  Avalonia/Silk surface). Fails for:
    * Direct-rendered GPU windows (Chrome, Firefox, Steam, most games)
      whose pixel content lives in GPU memory the X server never sees.
    * XWayland proxy windows for Wayland-native clients.

  **Fix paths (ranked):**
    1. `xdg-desktop-portal` `org.freedesktop.portal.Screenshot.PickColor`
       D-Bus call. Modern standard, works on Wayland + composited X11.
       Zero new deps if we shell out via `gdbus call --session …`.
    2. `XCompositeNameWindowPixmap` per top-level window. Partial
       coverage (server-side pixmaps only) — still fails for GPU-direct
       clients.
    3. Shell out to a screenshot tool (`grim` Wayland, `import` X11) for
       a full screen, sample the pixel. Heavyweight, depends on tool
       presence.

### Disposition

Deferred. Current behavior covers the common case (sample inside the
running app — the band/stop swatches, the rendered fractal). Cross-app
sampling can land in a follow-up slice once the portal D-Bus surface is
wired (the Windows BitBlt swap is a 20-line change but lands at the same
time so both shells stay at parity).

Tracking constant: **S-X11** when the work picks back up.
