# FracturingFog Client/Server User Guide

Render fractals on one machine, drive the render from another. The same
`FracturingFog.exe` runs in three modes:

| Mode | What it does | How it's invoked |
|---|---|---|
| UI (default) | Interactive Avalonia explorer | `FracturingFog.exe` |
| Server | Headless renderer accepting remote jobs | `FracturingFog.exe --server [opts]` |
| Batch / remote batch | Headless single render against a remote server | `FracturingFog.exe --batch --remote --connection NAME --render NAME --out PATH` |

All traffic between client and server is **mutual TLS** (both ends present
X.509 certificates). Saved cert passwords on the client are encrypted with
AES-GCM derived from a master password the user enters once per session.

---

## 1. First-time server setup

### 1.1 Start the server

```
FracturingFog.exe --server
```

On first run the server creates a self-signed certificate bundle in:

```
%APPDATA%\FracturingFog\server-certs\
    ca.pfx          # the trust root
    server.pfx      # server identity cert (presented to clients)
    client.pfx      # client identity cert (give one copy to each client)
```

The server prints, e.g.:

```
listening on 127.0.0.1:47823 (loopback only — use --bind 0.0.0.0 to expose)
```

By default the server binds **loopback only** — only the same machine can
connect. To accept connections from other machines, restart with:

```
FracturingFog.exe --server --bind 0.0.0.0
```

### 1.2 Server CLI options

| Flag | Default | Meaning |
|---|---|---|
| `--bind ADDR` | `127.0.0.1` | Network interface to bind. `0.0.0.0` = all interfaces (LAN). |
| `--port N` | `47823` | TCP port. |
| `--max-minutes N` | `240` | Hard ceiling on per-job render time. |
| `--allow-override` | off | Allow client to request a longer per-job timeout (still capped server-side). |
| `--queue-depth N` | `1` | How many render jobs may be queued. Excess connections receive `busy`. |
| `--cert PATH` | auto-gen | Override server identity PFX. |
| `--client-ca PATH` | auto-gen | Override CA used to validate client certs. |
| `--log-dir PATH` | `%APPDATA%\FracturingFog\server-logs\` | Per-session log files. |
| `--work-dir PATH` | `%APPDATA%\FracturingFog\server-work\` | Where render jobs write before streaming back. |

Server config is persisted in `%APPDATA%\FracturingFog\server-config.json` —
the **Server…** admin dialog in the UI edits the same file.

### 1.3 Server status from the UI

When `FracturingFog.exe` starts in UI mode and detects the server already
running on the configured port, the status bar shows a green `● Server`
indicator (grey = down, red = error — hover for the last error string).
Floating Menu → **Server…** opens the admin dialog:

* uptime / in-flight count / completed count / last error
* edit max-minutes, allow-override, queue depth (Apply rewrites config)
* edit rate-limit-per-minute + burst (per-IP throttle, 0 disables)
* edit max concurrent TLS sessions (default 32)
* TLS hardening: require TLS 1.3 only · revocation policy (none / online /
  offline) · allowed client cert thumbprints (cert pinning)
* edit cert paths (server cert PFX, client CA PFX, certs dir override)
* edit log + work directory paths
* stale work sweep age (default 1 h — leftover job-* dirs older than this
  delete on startup)
* Start / Restart / Kill — spawn or terminate a local `--server` child process

The Server admin dialog only manages a *local* server; remote server lifecycle
is the responsibility of whoever runs that host.

For deployment + PKI walkthroughs see [ServerAdmin-Guide.md](ServerAdmin-Guide.md).

### 1.4 Status-bar indicator

The Avalonia MainWindow's status bar shows a coloured ● Server pill on the
right edge:

| Color | Meaning |
|---|---|
| Green | Local server is up + listening on the configured port |
| Grey | Local server is down |
| Red | Local server reported an error or management socket unreachable |

Click the pill to open the Server Admin dialog.

---

## 2. Certificate setup

### 2.1 Same machine (loopback)

1. Run `FracturingFog.exe --server` once to generate the bundle.
2. Open the Client dialog. Browse **Client cert** to
   `%APPDATA%\FracturingFog\server-certs\client.pfx`.
3. Browse **Server CA** to the same folder's `ca.pfx`.
4. **Cert password** is blank — the auto-generated dev certs have no password.

### 2.2 Different machine

1. On the **server host**, run `FracturingFog.exe --server --bind 0.0.0.0` to
   generate the bundle and start listening.
2. Copy `client.pfx` and `ca.pfx` to the client machine over a trusted channel
   (Bitlocker USB, SSH, etc. — do **not** email them).
3. On the **client machine**, place both files somewhere the user can read
   them — `%APPDATA%\FracturingFog\server-certs\` is fine but any path works.
4. In the Client dialog, browse **Client cert** to the copy of `client.pfx`,
   **Server CA** to the copy of `ca.pfx`, leave **Cert password** blank.

### 2.3 Production / shared deployment

For a multi-user deployment you should generate per-user client certs signed
by the same CA. The dev helper only produces a single client cert. Use
`dotnet` / `openssl` / a corporate PKI to mint additional client certs whose
CA chain ends in the same `ca.pfx`. Each user gets their own client cert; all
trust the same server.

If you store the client `.pfx` with a password (recommended on shared
machines), enter that password in **Cert password** before saving the
connection — it will be sealed under your master password.

---

## 3. Client dialog walkthrough

Open: Floating Menu → **Client…**.

### 3.1 Master password

The first time you save a connection that has a sealed cert password, the
master password you entered becomes the vault key. Every subsequent session
must enter the **same** master password to use saved connections.

* Empty vault: any password is accepted. The one used at first save sticks.
* Vault with sealed entries: `Unlock` verifies via decrypt. Wrong password →
  `Wrong password` message. There is no recovery — if forgotten, delete
  `%APPDATA%\FracturingFog\client-connections.json` and start over.

The master password is held in process memory only; closing the UI clears it.

### 3.2 Saving a server connection

1. Enter **Name** (e.g. `local` or `render-box-2`).
2. **Host** + **Port** of the server.
3. **Client cert (.pfx)** — browse to the file.
4. **Cert password** — only if the .pfx itself is password-protected.
5. **Server CA (.pfx)** — the trust root the server presents.
6. **Remark** — free-form note (e.g. `RTX 4090 box in basement`).
7. Click **Save**. The vault is rewritten.

### 3.3 Render presets

A preset captures every field of a render request (mode, region, theme,
quality, size, video parameters, return mode, optional output path) under a
name. To save: fill the preset form → **Save**. To reuse: pick from the combo
→ fields populate.

* **Mode** = `image` or `video`. The banner under the preset combo turns
  orange (`▶ VIDEO MODE`) when video is selected so you can see at a glance
  what will be produced.
* **Region** + **Theme** are editable comboboxes — pick from the local
  library or type a name the server will resolve. Leave Region blank and
  fill the **Manual coords** rows to specify centerX/Y, zoom, iter directly.
* **Quality** drives the iteration cap and AA strategy on the server.
* **Size** is bounded server-side: 16×16 to 32768×32768, with a default
  64-megapixel ceiling (32K × 32K = 1 G px, configurable).
* **Lossless** picks the encoder: `none` (h264 mp4), `h264`, `ffv1` (mkv),
  `h264hq` (visually lossless h264).

Disallowed fractal types (UserEquation / Sandbox / UserBulb) are not listed
in the Fractal combo and are rejected server-side as `forbidden-fractal`
even if a saved region tags one of them.

### 3.4 Output

* **Output path** is the file the response writes to. Leave blank to pop a
  Save dialog on response.
* **Return mode**:
  * `inline` — server sends bytes back over TLS. The client writes them to
    your chosen path. Use for renders you want to keep on the client.
  * `saved-path` — server keeps the artifact in its work directory and
    returns the absolute path. Use when client + server share a filesystem
    or you'll fetch later.

### 3.5 Rendering

Click **Render Image** or **Render Video** — the button label tracks the
Mode combo. The button is disabled until the response arrives.

Status line shows `Connecting…` → `Rendering…` → `Done (NNN ms, WxH)`.
Errors are surfaced in red below.

### 3.6 Rendering a video from the dialog

The same dialog produces both stills and videos. The Mode combo decides
which path runs:

1. Unlock the master password.
2. Pick a saved connection.
3. In the Render Preset group, set **Mode** = `video`. The banner above
   the form turns orange (`▶ VIDEO MODE — output will be an MP4/MKV`)
   and the bottom button relabels to **Render Video**.
4. Pick the **target** of the zoom — either set a Region (recommended,
   gets centerX/Y + end zoom from the saved region) or fill the Manual
   coords rows (centerX, centerY, end-zoom, iter).
5. Fill the **Video options** sub-form near the bottom:
   - **seconds** — duration (0.5–600).
   - **fps** — frame rate (1–240; typical 30 or 60).
   - **start zoom** — how zoomed-out the first frame is. With a Region
     selected, the animation runs from this zoom to the region's stored
     zoom (e.g. `0.5` → full set; the clip zooms in to the region).
   - **reverse** — tick to animate out instead of in.
   - **lossless**:
     - `none` — browser-friendly MP4 via built-in Mp4Writer (default).
     - `h264` — lossless MP4 via ffmpeg.
     - `ffv1` — lossless MKV via ffmpeg.
     - `h264hq` — visually lossless high-bitrate H.264 via ffmpeg.
6. Set **Output path** to a local file ending `.mp4` (or `.mkv` for
   `ffv1`), or leave blank to be prompted on completion.
7. Click **Render Video**.

Notes:
* The `h264` / `ffv1` / `h264hq` presets require `ffmpeg.exe` on the
  **server's** PATH. The `none` preset uses the in-process Mp4Writer.
* Inline return streams the file back in 1 MB chunks over the TLS
  socket. Long videos can be hundreds of MB — for multi-GB outputs
  pick **Return mode = saved-path** and fetch the file later (e.g.
  over a share).
* The end zoom comes from the region. To bypass the region pick, blank
  the Region field and use the Manual coords `zoom` field.

---

## 4. Batch / remote batch

Headless single render against a saved connection + saved preset. The
preset's stored Mode (`image` or `video`) decides which protocol method
runs — there is no separate `--mode` flag in the remote path.

Image:
```
FracturingFog.exe --batch --remote ^
    --connection render-box-2 ^
    --render seahorse_4k ^
    --out C:\renders\seahorse.png
```

Video (preset saved with `Mode = video`):
```
FracturingFog.exe --batch --remote ^
    --connection render-box-2 ^
    --render seahorse_video_30s ^
    --out C:\renders\seahorse.mp4
```

* `--remote` switches batch from local to remote path.
* `--connection NAME` resolves a saved entry from `client-connections.json`.
* `--render NAME` resolves a saved entry from `client-render-presets.json`.
* `--out PATH` is where returned bytes land. Match the extension to the
  preset's mode — `.png` for image presets, `.mp4` / `.mkv` for video.
  If you point `.png` at a video preset the file holds video bytes
  regardless.

On startup the CLI prints:
```
batch remote → 127.0.0.1:47823
  preset : seahorse_video_30s
  mode   : video
  size   : 3840x2160
  out    : C:\renders\seahorse.mp4
```
so you can confirm the preset's Mode before the render begins.

The batch path prompts for the master password on stdin (no echo). After
unlocking it runs the same `FFClientConnection` the UI uses and writes the
returned bytes to `--out`. Exit code 0 on success, non-zero on any failure
(connection refused, cert mismatch, server error, timeout).

Local batch (`--batch` without `--remote`) is unchanged.

---

## 5. Error messages

| Error | Cause | Fix |
|---|---|---|
| `forbidden-fractal` | Preset asked for UserEquation / Sandbox / UserBulb, or a saved region tags one. | Pick a different fractal or region. |
| `unknown-region` | Region name not in server's library. | Save the region on the server side first, or use manual coords. |
| `unknown-theme` | Theme name not in server's library. | Pick from the dropdown (lists local themes) or save the theme server-side. |
| `bad-request` | Invalid dimensions, video seconds out of range, etc. | Check the request limits (16-32768 px, 0.5-600 s, 1-240 fps). |
| `timeout` | Render exceeded `--max-minutes`. | Shrink size / lower iterations / raise server `--max-minutes`. |
| `busy` | Server queue full. | Retry; or raise `--queue-depth` server-side. |
| `ArgumentException ... 'path'` | Client connection has no cert path. | Open the connection, browse to the client .pfx, Save. |
| `Wrong password` (Unlock) | Master pw differs from what sealed entries were encrypted with. | The master pw is set the first time you Save with a non-empty cert password. If forgotten, delete `client-connections.json`. |
| Hangs on Connect | Wrong host/port, firewall blocking TLS, server not bound to a routable interface (loopback-only by default). | Confirm `--bind 0.0.0.0` server-side and the port is reachable. |

---

## 6. Security notes

* Server defaults to **loopback only** (`127.0.0.1`). Setting `--bind 0.0.0.0`
  is a deliberate, conscious step before exposing on a network.
* mTLS is enforced — the server rejects any client that does not present a
  cert chaining to the configured CA.
* User-code fractal types (UserEquation, Sandbox, UserBulb) are blocked at
  the protocol layer to prevent arbitrary code execution from the network.
* Per-job timeouts (`--max-minutes`) prevent runaway renders from monopolising
  the server.
* Connection cap (`--queue-depth` + a hard 32 concurrent TLS sessions) limits
  fan-out abuse.
* Image dimension cap (32768 × 32768) and a 64-megapixel default ceiling
  prevent OOM-by-design.
* Saved client cert passwords are encrypted with AES-GCM, key derived from
  the master password via PBKDF2-SHA256 (200k iterations) with a per-entry
  salt.
* Self-signed dev certs are convenient for `localhost` testing. For any
  network deployment, generate proper certs through your own PKI and pass
  them via `--cert` / `--client-ca`.
* `client-connections.json` is protected by **filesystem ACL** for everything
  except the sealed cert password. Treat the file as sensitive — anyone who
  can read it learns your hostnames and cert paths.

---

## 7. File locations

| File | Purpose |
|---|---|
| `%APPDATA%\FracturingFog\server-config.json` | Server config (port, max-minutes, etc.) |
| `%APPDATA%\FracturingFog\server-certs\*.pfx` | Self-signed dev certs |
| `%APPDATA%\FracturingFog\server-logs\*.log` | One file per accepted session |
| `%APPDATA%\FracturingFog\server-work\` | Temp render output before streaming |
| `%APPDATA%\FracturingFog\client-connections.json` | Saved server connections (AES-GCM sealed) |
| `%APPDATA%\FracturingFog\client-render-presets.json` | Saved render presets (plain JSON) |

---

## 8. See Also

- [ServerAdmin-Guide.md](ServerAdmin-Guide.md) — server deployment, PKI, TLS hardening, rate limiting, protocol-layer validators
- [Avalonia-UserGuide.md](Avalonia-UserGuide.md) — UX walkthrough of the Client + Server admin dialogs
- [Capture-Guide.md](Capture-Guide.md) — remote poster workflow + ffmpeg flag reference
- [Architecture-Overview.md](Architecture-Overview.md) — module-by-module map (Server / ServerHost / Client / Guard layers)
