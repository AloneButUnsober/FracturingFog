# Distributed Rendering User Guide

Spread expensive Mandelbrot renders across a small cluster of machines on
your LAN. One **master** plans + merges; N **workers** chew through tiles
in parallel; the **admin UI** and the **client** both speak to the master
over mutual TLS. The same `FracturingFog.exe` plays every role — only the
CLI flag differs.

> Companion pages:
> [User Index](_Index.md) · [Client / Server Guide](ClientServer-UserGuide.md) ·
> [Server Admin Guide](ServerAdmin-Guide.md)

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture at a Glance](#2-architecture-at-a-glance)
3. [First-Time Master Launch](#3-first-time-master-launch)
4. [The Cluster Cert Bundle](#4-the-cluster-cert-bundle)
5. [Sharing Keys Between Hosts](#5-sharing-keys-between-hosts)
6. [Production PKI — Per-Role Certificates](#6-production-pki--per-role-certificates)
7. [Launching Workers](#7-launching-workers)
8. [Admin UI Tour](#8-admin-ui-tour)
9. [Master Config Dialog](#9-master-config-dialog)
10. [Submitting Jobs as a Client](#10-submitting-jobs-as-a-client)
11. [Rate Limits + Admin Audit Log](#11-rate-limits--admin-audit-log)
12. [Crash Recovery](#12-crash-recovery)
13. [Logs, Metrics, and Troubleshooting](#13-logs-metrics-and-troubleshooting)
14. [CLI Reference](#14-cli-reference)
15. [Config File Reference](#15-config-file-reference)
16. [File Locations](#16-file-locations)
17. [See Also](#17-see-also)

---

## 1. Overview

The cluster mode layered onto FracturingFog turns a stack of LAN machines
into a single virtual render farm. The user-visible win is wall-clock
time: a Bird-of-Paradise 8K poster that takes 8 minutes on one box
finishes in under 3 minutes across four workers. The mechanism is
deliberately small:

- One **master** process accepts client jobs, slices each image into
  tiles, dispatches them to whichever workers are free, merges the
  returned pixels into a final artifact, and serves the artifact back to
  the client.
- N **worker** processes dial *out* to the master, register, and then
  long-poll for tile work. Workers never accept inbound connections, so
  only the master needs an open port.
- An **admin UI** opens a tab inside the regular Avalonia shell. From it
  you watch workers heartbeat, drill into a job's tile map, quiesce a
  worker for maintenance, or live-edit cluster knobs.
- A **client** is unchanged — the FFClient dialog uses the same
  `FracturingFog.exe` binary and the same mTLS handshake as the
  single-server path, but the master replies with a `JobId` and the
  client polls until the artifact is ready.

Everything below assumes a trusted LAN. Cluster mode is **not** designed
for WAN or Internet operation: there is no NAT traversal, no rate-
limited public ingress, and the dev cert bundle uses empty-password
PFX files.

> [!IMPORTANT]
> The cluster shares almost every safety mechanism with the single-
> server path — forbidden fractal types, payload validators, image-
> dimension caps, mTLS. The only new attack surface is the worker
> registration channel, which is protected by a separate set of
> role-tagged certificates (§4).

---

## 2. Architecture at a Glance

```
                         ┌────────────────────────┐
                         │  Avalonia Admin UI     │
                         │  (cluster.* RPC)       │
                         └──────────┬─────────────┘
                                    │ mTLS
                                    ▼
┌────────────┐    mTLS    ┌────────────────────────┐    mTLS    ┌────────────┐
│  Client    │──submit───▶│  Master Server         │◀──register─│  Worker N  │
│  (FFClient)│◀──poll─────│  job queue · planner   │            │  (dial out)│
│            │            │  dispatcher · merger   │──tile job─▶│            │
└────────────┘            └────────────────────────┘◀──chunks───└────────────┘
```

The three roles map to three cert OUs (`role-client`, `role-worker`,
`role-admin`). The master examines the OU on the presented client cert
and routes RPC calls accordingly — a worker cert cannot call
`job.submit`; a client cert cannot call `worker.kill`. Role parsing is
in [`CertRole.cs`](../../Server/Tls/CertRole.cs); the role check is
applied by `FFServer.DispatchClusterAsync` before any method handler
runs.

### A friendly tour

Think of the master as a **kitchen pass**. Clients drop tickets on it
("render this Bird-of-Paradise"); workers stand at stations and grab the
next ticket whenever they're free; the master plates the dish (merges
the tiles into the final PNG or video) and hands it back to the client
who ordered it. The admin UI is the **chef's view** — it watches every
ticket and every station at once.

### Worked example — "First-ever cluster"

Imagine three desktops on the same LAN:

| Host          | IP            | Role            |
|---------------|---------------|-----------------|
| `tower-1`     | `192.168.1.50`| Master + Admin  |
| `tower-2`     | `192.168.1.51`| Worker          |
| `laptop`      | `192.168.1.52`| Client          |

You will:

1. Start the master on `tower-1` (it auto-generates a cluster cert
   bundle on first run).
2. Copy `ca.pfx` + `worker.pfx` to `tower-2`. Start a worker that dials
   `tower-1:47823`.
3. Copy `ca.pfx` + `cluster-client.pfx` to `laptop`. Open FFClient,
   point it at `tower-1:47823`, submit a render.
4. On `tower-1`, open the Cluster Dashboard from the Floating Menu and
   watch the tiles drain.

The rest of this guide is detail on each of those steps.

---

## 3. First-Time Master Launch

```
FracturingFog.exe --master
```

Defaults match the dev plan §6: bind `127.0.0.1`, port `47823`. On
first run the master:

1. Creates `%APPDATA%\FracturingFog\cluster-certs\` if missing.
2. Generates **five** PFX files (the cluster bundle — see §4).
3. Creates `%APPDATA%\FracturingFog\master\jobs\` for on-disk job state.
4. Creates `%APPDATA%\FracturingFog\master-logs\` for the cluster
   NDJSON log.
5. Calls `RecoverFromDisk()` to replay any in-flight jobs left over from
   a previous master process (§12).
6. Prints the bundle paths so you can hand them out:

```
cluster dev bundle:
  certs dir : C:\Users\me\AppData\Roaming\FracturingFog\cluster-certs
  ca        : ...\ca.pfx
  master pfx: ...\master.pfx
  worker pfx: ...\worker.pfx
  client pfx: ...\cluster-client.pfx
  admin  pfx: ...\admin.pfx
master listening on 127.0.0.1:47823
```

The first line of `master listening on …` is your confirmation that the
JSON-RPC listener is up. `Ctrl-C` stops the master cleanly — any
in-flight job is left on disk and resumes on next startup.

> [!NOTE]
> The cluster cert bundle lives in a **separate** directory from the
> single-server bundle (`server-certs/` vs `cluster-certs/`). This is
> deliberate: a cluster CA must never be confused with the single-server
> CA, even though both start out as self-signed dev material.

To bind the master to a routable address so other LAN hosts can reach
it:

```
FracturingFog.exe --master --bind 0.0.0.0
```

For a non-default cert directory (e.g. you minted certs in your own
PKI):

```
FracturingFog.exe --master --bind 0.0.0.0 --cluster-certs-dir D:\pki\fog-cluster
```

The full CLI is in §14.

---

## 4. The Cluster Cert Bundle

The auto-generated bundle has **five** files:

| File                  | Subject                                            | Used by             |
|-----------------------|----------------------------------------------------|---------------------|
| `ca.pfx`              | `CN=fracturingfog-cluster-ca`                      | Trust root (everyone) |
| `master.pfx`          | `CN=fracturingfog-server`                          | Master only         |
| `worker.pfx`          | `CN=fracturingfog-worker, OU=role-worker`          | Each worker         |
| `cluster-client.pfx`  | `CN=fracturingfog-client, OU=role-client`          | Each client         |
| `admin.pfx`           | `CN=fracturingfog-admin, OU=role-admin`            | The admin operator  |

The `OU=role-*` markers are the **only** thing distinguishing one cert
from another at the protocol layer. The master reads
[`CertRoleParser.FromCertificate`](../../Server/Tls/CertRole.cs) for
every presented client cert and refuses out-of-band RPC calls:

- `role=worker` may call `worker.register`, `worker.heartbeat`,
  `tile.next`, `tile.deliver`, `tile.error`. It **cannot** call
  `job.submit` or `cluster.*`.
- `role=client` may call `job.submit`, `job.status`, `job.fetch`,
  `job.cancel`. It **cannot** call worker RPCs or any `cluster.*` admin
  call.
- `role=admin` may call everything the client can, plus `cluster.*`,
  `worker.kill`, `worker.quiesce`, `master.config.*`, `job.list`, and
  cross-user `job.cancel`.

A cert with **no** role OU resolves to `Client` (backwards-compatible
with the single-server dev bundle).

All five PFX files have **empty passwords**. On Windows the cluster-
certs directory is locked to the launching user's SID; on POSIX it is
chmod 0700. See the security trade-off note at the top of
[`CertSelfSignedHelper.cs`](../../Server/Tls/CertSelfSignedHelper.cs)
for what these ACLs do and do not protect against.

> [!WARNING]
> Empty-password PFX files are convenient for first-time LAN setup but
> are **not** suitable for any shared machine. For multi-user
> deployments mint your own bundle (§6) with PFX passwords and keep
> them in the OS credential store.

### Cert validity

The auto-generated bundle is valid for **10 years** from the day the
master first runs. The CA's `notBefore` is one day in the past to absorb
small clock skew. If you regenerate the bundle (by deleting any one of
the five files and restarting the master) every existing worker /
client / admin distribution becomes invalid; redistribute.

---

## 5. Sharing Keys Between Hosts

### 5.1 What each role needs

| Role on remote host | Files to copy from the master host           |
|---------------------|----------------------------------------------|
| Worker              | `ca.pfx` + `worker.pfx`                      |
| Client              | `ca.pfx` + `cluster-client.pfx`              |
| Admin (remote)      | `ca.pfx` + `admin.pfx`                       |

The remote host **never** needs `master.pfx` (that is the master's
identity) or any role PFX it does not play.

### 5.2 Copying the files safely

The dev bundle has no password, so the channel you use to move it is
the **only** thing keeping the keys out of an attacker's hands.
Acceptable channels:

- **Encrypted USB stick** (BitLocker To Go, FileVault, LUKS).
- **SCP / SSH** over the LAN (`scp ca.pfx worker.pfx me@tower-2:/path/`).
- **A shared folder protected by SMB-over-Kerberos**.

Channels you should **not** use:

- Plain email. Once a message is on a mail server it is forever.
- Public chat (Slack DM, Teams) — same problem.
- An unencrypted file share. Cleartext on the wire.

> [!IMPORTANT]
> Treat `ca.pfx` as sensitive. Anyone who has it can mint additional
> role certs and join the cluster. If the CA is exposed, delete the
> bundle and restart the master — five new files appear and every
> distribution must be repeated.

### 5.3 Step-by-step: adding a new worker host

On the master host:

```powershell
$src = "$env:APPDATA\FracturingFog\cluster-certs"
$dst = "\\tower-2\share\fog-keys"   # or use scp / sneakernet
Copy-Item $src\ca.pfx     $dst
Copy-Item $src\worker.pfx $dst
```

On the worker host (`tower-2`):

```powershell
$dst = "$env:APPDATA\FracturingFog\cluster-certs"
New-Item -ItemType Directory -Force $dst | Out-Null
Move-Item "\\share\fog-keys\ca.pfx"     $dst
Move-Item "\\share\fog-keys\worker.pfx" $dst
icacls $dst /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F"
```

The `icacls` line restricts the cert directory to the launching user so
a co-resident process under a different local account cannot lift the
empty-password PFX files.

Then launch the worker — see §7.

### 5.4 Step-by-step: handing the admin role to another operator

The `admin` role is the most powerful in the cluster — it can quiesce
workers, cancel arbitrary jobs, and live-edit cluster config. Hand it
out deliberately.

1. Copy `ca.pfx` + `admin.pfx` to the operator over a trusted channel.
2. Have them place both files in their own
   `%APPDATA%\FracturingFog\cluster-certs\`.
3. From the Avalonia shell on their host, Floating Menu → **Cluster
   Dashboard…**. The dialog points at `127.0.0.1:47823` by default;
   change the **Host** field to the master's address.

The admin operator's actions are recorded — see §11.

---

## 6. Production PKI — Per-Role Certificates

For any deployment beyond a single-operator LAN, replace the dev bundle
with certs minted by your own CA. The master only requires that:

- The presented cert chains to the CA in `ca.pfx`.
- The cert's Subject DN includes one of
  `OU=role-worker`, `OU=role-client`, or `OU=role-admin`.

### 6.1 Mint the CA

```bash
openssl req -new -x509 -newkey rsa:4096 -nodes -days 3650 \
    -keyout ca.key -out ca.crt \
    -subj "/CN=fracturingfog-cluster-ca"

openssl pkcs12 -export \
    -inkey ca.key -in ca.crt -name "Fracturing Fog Cluster CA" \
    -out ca.pfx -passout pass:""
```

The `ca.pfx` produced this way is distributed to **every** host (as the
trust root). The `ca.key` stays on a single trusted machine — issuing
certs is the only operation that needs it.

### 6.2 Mint the master cert

```bash
openssl req -new -newkey rsa:4096 -nodes \
    -keyout master.key -out master.csr \
    -subj "/CN=fog-master.example.com"

openssl x509 -req -in master.csr \
    -CA ca.crt -CAkey ca.key -CAcreateserial \
    -out master.crt -days 365 -sha256 \
    -extfile <(printf "subjectAltName=DNS:fog-master.example.com,IP:10.0.0.42\nextendedKeyUsage=serverAuth")

openssl pkcs12 -export \
    -inkey master.key -in master.crt -certfile ca.crt \
    -name "Fracturing Fog Master" \
    -out master.pfx -passout pass:"STRONG-PASSWORD"
```

> [!NOTE]
> The master cert needs `extendedKeyUsage=serverAuth` so .NET's TLS
> stack accepts it as a server identity. The dev bundle generator sets
> this automatically; the openssl invocation above does it via the
> `-extfile` heredoc.

### 6.3 Mint a worker cert

```bash
openssl req -new -newkey rsa:4096 -nodes \
    -keyout worker-tower2.key -out worker-tower2.csr \
    -subj "/CN=tower-2/OU=role-worker"

openssl x509 -req -in worker-tower2.csr \
    -CA ca.crt -CAkey ca.key -CAcreateserial \
    -out worker-tower2.crt -days 365 -sha256 \
    -extfile <(printf "extendedKeyUsage=clientAuth")

openssl pkcs12 -export \
    -inkey worker-tower2.key -in worker-tower2.crt -certfile ca.crt \
    -name "Fracturing Fog Worker (tower-2)" \
    -out worker-tower2.pfx -passout pass:"STRONG-PASSWORD"
```

Repeat for every worker host. The **CN can be anything you find useful
for log-grepping** — typically the hostname; the OU is what gates the
RPC surface.

### 6.4 Mint a client cert

```bash
openssl req -new -newkey rsa:4096 -nodes \
    -keyout alice.key -out alice.csr \
    -subj "/CN=alice@example.com/OU=role-client"

openssl x509 -req -in alice.csr \
    -CA ca.crt -CAkey ca.key -CAcreateserial \
    -out alice.crt -days 365 -sha256 \
    -extfile <(printf "extendedKeyUsage=clientAuth")

openssl pkcs12 -export \
    -inkey alice.key -in alice.crt -certfile ca.crt \
    -name "Fracturing Fog Client (alice)" \
    -out alice.pfx -passout pass:"STRONG-PASSWORD"
```

### 6.5 Mint an admin cert

```bash
openssl req -new -newkey rsa:4096 -nodes \
    -keyout admin-alice.key -out admin-alice.csr \
    -subj "/CN=alice-admin@example.com/OU=role-admin"

openssl x509 -req -in admin-alice.csr \
    -CA ca.crt -CAkey ca.key -CAcreateserial \
    -out admin-alice.crt -days 365 -sha256 \
    -extfile <(printf "extendedKeyUsage=clientAuth")

openssl pkcs12 -export \
    -inkey admin-alice.key -in admin-alice.crt -certfile ca.crt \
    -name "Fracturing Fog Admin (alice)" \
    -out admin-alice.pfx -passout pass:"STRONG-PASSWORD"
```

### 6.6 Lay out the PFX files

The cluster code expects the cert directory to contain these five files
under their **exact** names so the bundle loader (`EnsureClusterBundle`)
recognises them:

```
<cert dir>\
    ca.pfx
    master.pfx
    worker.pfx                 # used only by --worker hosts
    cluster-client.pfx         # used only by --batch --remote callers
    admin.pfx                  # used only by the admin operator's host
```

A given host only needs the files for the roles it plays.

### 6.7 Pin worker thumbprints (optional, recommended)

`server-config.json` has an `allowedClientThumbprints` field. When
non-empty, the master refuses any presented client cert whose
SHA-1 thumbprint is not on the list, even if the chain validates. This
lets you mint many certs from one CA but only authorise a known subset
to join the cluster. The field is documented in
[Server Admin Guide §7](ServerAdmin-Guide.md#7-tls-hardening).

> [!TIP]
> If you minted a worker cert and the worker says
> `chain validation error`, double-check the OU — `OU=role-worker`
> exactly, case-insensitive, no trailing spaces. Anything else hits the
> `unrecognised role` throw in `CertRoleParser`.

---

## 7. Launching Workers

```
FracturingFog.exe --worker --master-host tower-1 --master-port 47823
```

Required:

- `--master-host` — DNS name or IP of the master.

Common options:

- `--master-port N` (default `47823`).
- `--worker-name NAME` — shows up in the Cluster Dashboard. Defaults to
  the machine's hostname.
- `--max-concurrent-tiles N` — how many tiles the worker chews on at
  once. Default 1; raise for CPU-rich hosts to let SIMD parallelism
  overlap with network I/O. Above ~`Environment.ProcessorCount / 2` you
  start trading throughput for latency.
- `--preferred-tile-pixels N` — the side length the master prefers to
  cut tiles to for this worker. Default 512. Smaller = more tiles =
  finer load balancing but more overhead; larger = fewer tiles =
  better SIMD utilisation per tile.
- `--cluster-certs-dir PATH` — override the default cert location.
- `--work-dir PATH` — per-tile scratch dir (default
  `%APPDATA%\FracturingFog\worker-work`).

A successful launch prints:

```
worker 'tower-2' → tower-1:47823
```

…and then waits for the master to assign tiles. The worker stays alive
across master restarts: it reconnects + re-registers automatically.
`Ctrl-C` stops it cleanly — any tile in flight is rolled back to the
dispatcher and re-assigned to another worker.

### Workers running as a service

The worker is a long-lived process with no UI. For unattended boxes,
wrap it in:

- Windows: `nssm` or a scheduled task with "Run whether user is logged
  on or not".
- Linux: a `systemd` unit (the binary runs on .NET 10 on Linux via
  `dotnet FracturingFog.dll --worker …`).
- macOS: `launchd`.

The worker's logs go to stdout (capture them with the service manager).
A future patch will add a `--log-dir` flag mirroring the master's.

### What capabilities does a worker advertise?

On `worker.register` the worker tells the master:

| Field                   | Value                                          |
|-------------------------|------------------------------------------------|
| `WorkerName`            | `--worker-name` or hostname                    |
| `OsPlatform`            | `win` / `linux` / `macos`                      |
| `LogicalCores`          | `Environment.ProcessorCount`                   |
| `TotalRamBytes`         | `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` |
| `Gpus`                  | empty list in v1 (CPU-only tile path)          |
| `SupportedFractalTypes` | hard-coded subset: Mandelbrot, BurningShip, Tricorn, Multibrot, Julia, Phoenix, Newton, Nova, BuddhaBrot |
| `MaxConcurrentTiles`    | from `--max-concurrent-tiles`                  |
| `PreferredTilePixels`   | from `--preferred-tile-pixels`                 |
| `EngineBuildSha`        | `InformationalVersion` of the engine assembly  |
| `ProtocolVersion`       | `"1"`                                          |

The `EngineBuildSha` check is the load-bearing one for fidelity: the
master refuses any worker whose engine build differs from its own. This
defends against subtle pixel drift from a worker running an older or
patched binary (risk #7 in the dev plan).

---

## 8. Admin UI Tour

The Avalonia shell's Floating Menu (press `M`) gains four cluster
entries when an admin cert is configured:

- **Cluster Dashboard…** — workers grid + recent-jobs grid + connection
  status.
- **Job List…** — full job list with cancel buttons.
- **Worker Detail…** — opens from the dashboard via the *Open* button
  on a worker row.
- **Job Detail…** — opens from the dashboard via *Open* on a job row.

### 8.1 Cluster Dashboard

The dashboard's top half shows the live worker grid:

| Column      | Meaning |
|-------------|---------|
| Worker      | Name + (hover) WorkerId |
| State       | `live` / `quiesced` / `lost` |
| OS          | `win` / `linux` / `macos` |
| Cores       | Logical processor count |
| RAM         | Total available memory |
| In flight   | Tiles currently dispatched to this worker |
| CPU         | Heartbeat-reported CPU % |
| ms/kpx      | EMA of tile completion time per kilopixel |
| HB age (s)  | Seconds since last heartbeat |
| Detail      | Opens the per-worker dialog |

The bottom half is the recent-jobs grid: JobId, mode, state, tile
progress, percent complete, creation time, and per-row Open / Detail
buttons.

Status indicators use **yellow** (`#FFCC00`) — not red — for problem
states. (The project author is red/green colour-blind; yellow is the
unambiguous alert hue across the rest of the UI.)

### 8.2 Worker Detail

Opens a per-worker dialog with:

- The capability snapshot from registration (cores, RAM, GPU list,
  supported fractal types).
- The live telemetry stream (heartbeat, CPU %, ms-per-kilopixel EMA).
- Three buttons:
  - **Quiesce** — stop sending new tiles to this worker. Existing
    in-flight tiles finish.
  - **Resume** — re-enable dispatch.
  - **Kill** — force-close the worker's socket. The worker process
    keeps running; it reconnects on its own loop, but any in-flight
    tiles roll back to the dispatcher.

Quiesce + resume is the **safe** way to take a worker out for
maintenance. Kill is for misbehaving workers — use it sparingly.

### 8.3 Job Detail

Per-job view:

- **Tile map** — visual grid showing each tile's status (pending /
  in-flight / done / failed) coloured by the worker that produced it.
- **Events log** — live tail of the `events.ndjson` for this job, so
  you can watch tile arrivals + worker swaps in real time.
- **Per-worker contribution bar** — bar chart of how many tiles each
  worker delivered.
- **Cancel** — admin-level cancel of any job, any submitter.

### 8.4 Job List

A flat scrollable list of every job in the master's on-disk store
(subject to the retention sweep — see §9). Cancel and Refresh buttons
along the top.

---

## 9. Master Config Dialog

Open from Floating Menu → **Master Config…**.

This dialog is the live-tuning surface for the three cluster knobs that
need to change without a master restart:

| Field                          | Default | Meaning |
|--------------------------------|--------:|---------|
| Max concurrent jobs            | 0       | Cap on concurrent non-terminal jobs. 0 = unlimited. Submits beyond this return `queue-full`. |
| Artifact retention (minutes)   | 60      | Terminal jobs (`ready` / `failed` / `cancelled`) older than this are evicted from the on-disk store. 0 = never evict. |
| Tile target (pixels)           | 0       | Default per-tile side length when the client gives no hint and no worker EMA is available. 0 = use the built-in 512. Positive values are clamped to `[64, 8192]` by the server. |

Click **Load** to fetch the current values from the running master.
Click **Apply** to push edits — the master clamps and reports the post-
apply values, and rewrites `server-config.json` so the new values
survive a restart.

A new **? Help** button on the dialog opens this guide jumped directly
to this section.

> [!TIP]
> "Max concurrent jobs" is the operator's first line of defence against
> a flood of poster requests OOM-ing the master's merge buffer. A 32K
> poster's merge buffer is roughly 4 GB — set this conservatively for
> RAM-constrained masters.

The four rate-limit knobs from §11 are not in this dialog (they are
applied at startup from `server-config.json`); edit them with a text
editor and restart the master to apply.

---

## 10. Submitting Jobs as a Client

A client points the FFClient dialog at the master's
`host:port` and uses the **cluster-client.pfx** cert. From the user's
perspective the dialog behaves identically to the single-server path
([Client / Server Guide](ClientServer-UserGuide.md) §3).

The protocol difference is hidden inside `FFClientConnection`:

1. The client sends `job.submit` with the same `RenderRequestDto` it
   would have sent to a single server. The master returns a `JobId`
   immediately (no bytes).
2. The client polls `job.status(jobId)` until `JobState == "ready"` or
   `"failed"`.
3. On success, the client calls `job.fetch(jobId)` and the master
   streams the merged artifact back over the same TLS socket.
4. On cancel, the client (or an admin) calls `job.cancel(jobId)`.

The poll cadence is 1 Hz — well below the per-call rate limit (§11) so
a normal render session never gets throttled.

### Batch mode against a cluster

```
FracturingFog.exe --batch --remote ^
    --connection fog-cluster ^
    --render seahorse_8k ^
    --out C:\renders\seahorse.png
```

is identical to the single-server batch path. The saved connection
named `fog-cluster` points at the master's address and uses
`cluster-client.pfx`.

---

## 11. Rate Limits + Admin Audit Log

D-6c layered a per-role token-bucket limiter on top of the existing
per-IP TCP-accept limiter. The defaults are tight enough to catch a
runaway loop but invisible under normal use.

### 11.1 Client-role limiter

Keyed by the caller's IP. Caps dispatched calls per minute inside an
authenticated session. Defaults:

| Field                  | Default | Meaning |
|------------------------|--------:|---------|
| `clientCallPerMinute`  | 600     | Sustained 10 calls/sec per IP. 0 disables. |
| `clientCallBurst`      | 30      | Standing token allowance. |

Refusals reply with the `rate-limited` error code (distinct from
`busy`, which means "queue full"). A refused client should back off ~5
seconds before retrying.

`server.status` bypasses the limiter — uptime probes will not get
throttled.

### 11.2 Worker-role limiter

Keyed by the worker's cert thumbprint. Caps only `tile.next` long-poll
calls; other worker methods (`heartbeat`, `deliver`, `error`,
`register`) bypass — their volume is already bounded by cadence or by
the dispatcher's tile supply.

| Field                       | Default | Meaning |
|-----------------------------|--------:|---------|
| `workerTileNextPerMinute`   | 600     | Sustained 10 calls/sec per worker. 0 disables. |
| `workerTileNextBurst`       | 30      | Standing token allowance. |

A worker that gets `rate-limited` on `tile.next` should treat it
identically to a `wait-again` reply — back off and retry.

### 11.3 Admin role

Never rate-limited. Every `cluster.*` method called by an admin cert is
recorded in the cluster log as a `kind:"admin-call"` event so the
"unlimited" surface stays accountable:

```
{"ts":"2026-06-28T18:42:11Z","kind":"admin-call",
 "method":"cluster.config.set","thumb":"3a7b1c…"}
```

The log is in `%APPDATA%\FracturingFog\master-logs\cluster.ndjson`.

### 11.4 Editing the knobs

The rate-limit fields are persisted in `server-config.json` and read at
master startup. To change them:

1. Stop the master (`Ctrl-C` or kill).
2. Edit `%APPDATA%\FracturingFog\server-config.json`. The four cluster
   rate-limit fields are at the top of the file alongside the
   single-server `rateLimitPerMinute` / `rateLimitBurst`.
3. Restart the master.

A future patch will surface them in the Master Config dialog so they
can be edited live.

---

## 12. Crash Recovery

D-6a added **image-job replay** to the master. On `--master` startup,
`ClusterCoordinator.RecoverFromDisk()` walks
`%APPDATA%\FracturingFog\master\jobs\` and:

- Replays every on-disk tile of every non-terminal image job back into a
  freshly-instantiated `ArtifactMerger`.
- Re-enqueues every missing tile under its original tile id, so the
  dispatcher hands them out to whatever workers connect first.
- Finalises any job whose tiles were all on disk already (e.g. the
  master crashed during merge).
- Flips video + slideshow jobs to `failed` with reason
  `master-restart` (their streaming pipelines aren't replayable in v1).

The recovery line prints on startup:

```
recovery: considered=4 resumedImage=3 failedUnsupported=1 failed=0
```

If the count is non-zero you'll also see the resumed jobs in the
Cluster Dashboard's recent-jobs grid with their original `JobId`.

### What is *not* recovered

- **In-flight video renders** — the streaming ffmpeg ingest path is
  rebuilt at submit time, not on disk. A video job that was rendering
  when the master died goes straight to `failed`.
- **In-flight slideshow renders** — same reason. The per-slide PNGs
  are on disk, but the slide manifest's finaliser is one step past the
  per-slide store.
- **Worker tile output that was in the middle of being written** — the
  PNG/RGBA sniff in `ClusterCoordinator.TryResumeImageJob` is
  corruption-tolerant; a half-written `.bin` is dropped from the done
  set and the tile is re-enqueued for re-rendering.

The fail-on-restart fallback also fires if the master encounters any
unhandled exception during the resume walk — better to fail the job
explicitly than to silently leave it in a stuck state.

---

## 13. Logs, Metrics, and Troubleshooting

### 13.1 Where to look

| File / Dir                                                    | What lives there |
|---------------------------------------------------------------|------------------|
| `%APPDATA%\FracturingFog\master-logs\cluster.ndjson`          | Cluster events: job lifecycle, worker register/heartbeat/lost, admin-call audit, tile delivery + retry, ref-orbit attach/fail. |
| `%APPDATA%\FracturingFog\server-logs\*.log`                   | One file per accepted session (single-server lineage; still emitted by the master's underlying `FFServer`). |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\request.json`    | Original `RenderRequestDto` as submitted. |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\plan.json`       | Tile list + per-tile dispatch history. |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\status.json`     | Current persisted `JobStatusDto`. |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\events.ndjson`   | Per-job event log: state transitions, tile arrivals, retries, worker swaps. |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\tiles\*.bin`     | Raw tile output as delivered by workers (PNG or BGRA). |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\artifact.<ext>`  | Final merged output. |
| `%APPDATA%\FracturingFog\worker-work\`                        | Per-tile scratch on the worker side. |

The `events.ndjson` per-job file is the single most useful artifact for
"why is this job stuck?" — it has timestamps for every tile dispatch,
delivery, retry, and the final merge.

### 13.2 Common errors

| Error             | Where you see it                | Likely cause |
|-------------------|---------------------------------|--------------|
| `forbidden-fractal` | client | Preset asks for UserEquation / Sandbox / UserBulb. Pick a built-in family. |
| `queue-full`      | client | `clusterMaxJobs` cap hit. Wait for in-flight jobs to drain or raise the cap. |
| `rate-limited`    | client / worker | Per-role bucket exhausted. Back off; retune §11 if legitimate. |
| `chain validation error` | worker dial-out / client dial-out | Cert does not chain to the configured CA, or the CN/SAN mismatches. Check the OU + the DN. |
| `unrecognised role 'X'`  | master log on TLS handshake | A presented client cert has `OU=role-X` for an unknown `X`. Fix the cert. |
| `master-restart`  | job `failReason` | The master died during a video/slideshow render. Resubmit; in v1 only image jobs auto-resume. |
| `worker engine SHA mismatch` | master log on `worker.register` | The worker is a different build of the engine than the master. Update the worker binary. |
| `tile-deadline`   | tile retry | The worker held a tile too long. Reassigned to another worker; happens once and you see no failure. Repeated occurrences mean the worker is overcommitted — lower `--max-concurrent-tiles`. |
| `worker-disappeared` | tile retry | Heartbeat timeout. Master assumes the worker is dead and reassigns its in-flight tiles. |

### 13.3 Diagnostics walkthrough — "my 4-worker cluster is rendering at 1-worker speed"

1. Open the Cluster Dashboard. Count *live* workers — if three are
   `lost`, you have a connectivity problem (firewall, master rebound to
   loopback after restart, etc.). Fix that first.
2. If all four are live, check the **CPU** column. A worker at 0–5 %
   while a job is in flight is starved — either its `tile.next` is
   getting `rate-limited`, or its `engine SHA mismatch` rejected it
   without an obvious banner.
3. Open Job Detail on the running job. The tile map should be a roughly
   even spread of colours across the four workers; if one colour
   dominates, the planner ran before the others registered. Cancel and
   resubmit.
4. The **events.ndjson** has timestamps. Compute the median
   `tile-delivered` interval — if it's >1 s the tiles are too big and
   you have low effective parallelism. Lower `clusterTileTargetPixels`
   in the Master Config dialog to 256 or 384 and resubmit.

### 13.4 Self-tests built into the binary

```
FracturingFog.exe --cluster-parity
FracturingFog.exe --cluster-scale --mode image --workers 4
FracturingFog.exe --cluster-video-parity
```

The first asserts byte-for-byte pixel parity between a single-worker
and an N-worker render of the same input — useful for confirming a new
worker host produces identical output.

The second is the scale harness behind the D-3 exit criteria — it
reports walltime and speedup vs. a single-worker baseline.

The third checks per-frame PNG SHA-256 + ffprobe stream parity for
video renders (D-4d's load-bearing fidelity guard).

---

## 14. CLI Reference

### Master

```
FracturingFog.exe --master [options]
```

| Flag                    | Default                                              | Meaning |
|-------------------------|------------------------------------------------------|---------|
| `--bind ADDR`           | `127.0.0.1`                                          | Listen interface. `0.0.0.0` exposes to LAN. |
| `--port N`              | `47823`                                              | TCP port. |
| `--cluster-certs-dir P` | `%APPDATA%\FracturingFog\cluster-certs`              | Override cert bundle dir. |
| `--log-dir P`           | `%APPDATA%\FracturingFog\server-logs`                | Per-session log dir (single-server lineage). |
| `--work-dir P`          | `%APPDATA%\FracturingFog\server-work`                | Master scratch dir. |
| `--jobs-dir P`          | `%APPDATA%\FracturingFog\master\jobs`                | On-disk job state root. |

### Worker

```
FracturingFog.exe --worker --master-host HOST [options]
```

| Flag                          | Default                                  | Meaning |
|-------------------------------|------------------------------------------|---------|
| `--master-host HOST`          | (required)                               | Master DNS name or IP. |
| `--master-port N`             | `47823`                                  | Master port. |
| `--cluster-certs-dir P`       | `%APPDATA%\FracturingFog\cluster-certs`  | Override cert bundle dir. |
| `--work-dir P`                | `%APPDATA%\FracturingFog\worker-work`    | Per-tile scratch dir. |
| `--worker-name NAME`          | `MachineName`                            | Display name in dashboard. |
| `--max-concurrent-tiles N`    | `1`                                      | Tiles in flight per worker. |
| `--preferred-tile-pixels N`   | `512`                                    | Preferred tile side length. |

### Self-tests

| Flag                       | Purpose |
|----------------------------|---------|
| `--cluster-parity`         | Single-worker vs. N-worker pixel parity (image). |
| `--cluster-video-parity`   | Per-frame PNG SHA-256 + ffprobe + framemd5 parity (video). |
| `--cluster-scale --mode image|video --workers N` | Walltime + speedup vs. baseline. |

---

## 15. Config File Reference

The `server-config.json` keys that govern cluster behaviour:

```json
{
  "port": 47823,
  "bindAddress": "0.0.0.0",
  "rateLimitPerMinute": 30,
  "rateLimitBurst": 10,

  "clientCallPerMinute": 600,
  "clientCallBurst": 30,
  "workerTileNextPerMinute": 600,
  "workerTileNextBurst": 30,

  "clusterMaxJobs": 4,
  "clusterArtifactRetentionMinutes": 60,
  "clusterTileTargetPixels": 0,

  "requireTls13": true,
  "revocationCheckMode": "none",
  "allowedClientThumbprints": [
    "3A:7B:1C:..."
  ]
}
```

The single-server fields (`maxMinutes`, `allowOverride`, `queueDepth`,
`maxConcurrentConnections`, etc.) also apply when the master is running
— it is, after all, an `FFServer` underneath.

`clusterMaxJobs`, `clusterArtifactRetentionMinutes`, and
`clusterTileTargetPixels` are live-tunable via the Master Config
dialog (§9); changes survive a master restart because the dialog's
**Apply** path calls back into `cfg.Save()`.

The four rate-limit knobs (`clientCallPerMinute` + `clientCallBurst` +
`workerTileNextPerMinute` + `workerTileNextBurst`) are read at master
startup only; edit and restart to apply.

---

## 16. File Locations

| File / Dir                                                  | Purpose |
|-------------------------------------------------------------|---------|
| `%APPDATA%\FracturingFog\cluster-certs\`                    | Cluster cert bundle (ca / master / worker / cluster-client / admin). |
| `%APPDATA%\FracturingFog\server-config.json`                | Single source of truth for master + single-server config. |
| `%APPDATA%\FracturingFog\master\jobs\<jobid>\`              | Per-job state (request / plan / status / events / tiles / artifact). |
| `%APPDATA%\FracturingFog\master-logs\cluster.ndjson`        | Cluster event log (admin audit, registrations, tile arrivals). |
| `%APPDATA%\FracturingFog\server-logs\*.log`                 | Per-session log files (one per accepted TLS session). |
| `%APPDATA%\FracturingFog\worker-work\`                      | Per-tile scratch on each worker host. |

---

## 17. See Also

- [Client / Server Guide](ClientServer-UserGuide.md) — single-server
  mode, FFClient dialog, render presets, batch mode.
- [Server Admin Guide](ServerAdmin-Guide.md) — TLS hardening,
  per-IP rate limiting, payload validators, stale-work sweep, status-
  bar indicator. The cluster master inherits every knob there.
- [Avalonia User Guide](Avalonia-UserGuide.md) — UX walkthrough of the
  Floating Menu, Client + Server admin dialogs.
- [Distributed Rendering Development Plan](../Technical/DistributedRendering-DevelopmentPlan.md)
  — design rationale, wire protocol, sharding strategy, risk register.
- [D-6 Session Notes](../Technical/D-6-Session-Notes.md) — the
  hardening + polish phase that produced this guide.

---

*Distributed Rendering User Guide · Fracturing Fog · © 2026*
