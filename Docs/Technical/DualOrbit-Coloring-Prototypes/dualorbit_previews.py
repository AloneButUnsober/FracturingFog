# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
# Prototype for Docs/Technical/DualOrbit-Coloring-RnD.md (epic #1114) - not shipped code.
"""Prototype previews for the dual-orbit colouring R&D (not FF code).
Colour-blind-safe: blue <-> amber/yellow axes only, no red/green coding."""
import numpy as np
from PIL import Image

W = H = 480
C0 = 0.5 + 0.0j                       # FF default c-seed
sx = np.linspace(-2.1, 0.7, W); sy = np.linspace(-1.4, 1.4, H)
S = sx[None, :] + 1j * sy[:, None]

def save(rgb, name):
    Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).save(name); print("wrote", name)

def ramp(t, a, b):  # linear blend of two RGB colours
    t = np.clip(t, 0, 1)[..., None]; return (1 - t) * np.array(a) + t * np.array(b)

BLUE, AMBER, DARK, WHITE = (0.15, 0.35, 0.95), (1.0, 0.75, 0.1), (0.03, 0.03, 0.06), (1, 1, 1)

# ---------------------------------------------------------- secant Lyapunov
N = 600; R2 = 1e6
z = np.zeros_like(S); u = np.full_like(S, C0)
acc = np.zeros(S.shape); cnt = np.zeros(S.shape); live = np.ones(S.shape, bool)
for n in range(N):
    sig = np.abs(z + u)
    acc[live] += np.log(np.maximum(sig[live], 1e-300)); cnt[live] += 1
    z = np.where(live, z * z + S, z); u = np.where(live, u * u + S, u)
    live &= (np.abs(z) ** 2 < R2) & (np.abs(u) ** 2 < R2)
lam = acc / np.maximum(cnt, 1)
# diverging: lambda<0 (contracting pair, interior) -> blue; lambda>0 (escaping) -> amber; 0 -> dark
neg = np.clip(-lam / 1.0, 0, 1); pos = np.clip(lam / 3.0, 0, 1) ** 0.6
rgb = ramp(neg, DARK, BLUE) * (lam < 0)[..., None] + ramp(pos, DARK, AMBER) * (lam >= 0)[..., None]
save(rgb, "prev_secant_lyapunov.png")

# --------------------------------------------- interior phase lag (s-plane)
N = 1500
z = np.zeros_like(S); u = np.full_like(S, C0)
with np.errstate(over="ignore", invalid="ignore"):
    for _ in range(N):
        z = z * z + S; u = u * u + S
        z = np.where(np.abs(z) > 4, 4, z); u = np.where(np.abs(u) > 4, 4, u)
    bounded = (np.abs(z) < 2) & (np.abs(u) < 2)
    P = 16; zs = [z]
    for _ in range(P): zs.append(zs[-1] ** 2 + S)
    period = np.zeros(S.shape, int)
    for p in range(1, P + 1):
        hit = (period == 0) & (np.abs(zs[p] - zs[0]) < 1e-7) & bounded
        period[hit] = p
    lag = np.full(S.shape, -1)
    best = np.full(S.shape, np.inf)
    for k in range(P):
        d = np.abs(u - zs[k]); m = (k < period) & (d < best); best[m] = d[m]; lag[m] = k
frac = np.where(period > 0, lag / np.maximum(period, 1), 0)
# lag 0 -> white; others spread along blue->amber; z-bounded-but-c-escaped -> grey; exterior -> dark
rgb = np.zeros(S.shape + (3,)) + np.array(DARK)
inMc = (period > 0) & (lag >= 0)
rgb[inMc] = ramp(frac[inMc], BLUE, AMBER)
rgb[inMc & (lag == 0)] = WHITE
rgb[(np.abs(z) < 2) & ~(np.abs(u) < 2)] = (0.25, 0.25, 0.3)
save(rgb, "prev_phase_lag.png")

# ----------------------------------------------- Boettcher-ratio domain colour
Wb = Hb = 360; NB = 220; Rb = 1e4
Sb = np.linspace(-2.6, 1.2, Wb)[None, :] + 1j * np.linspace(-1.9, 1.9, Hb)[:, None]
def lift(u0):
    args = np.zeros((NB,) + Sb.shape, np.float32)
    u = np.full(Sb.shape, u0, complex); esc = np.full(Sb.shape, -1)
    logu = np.zeros(Sb.shape)
    for n in range(NB):
        args[n] = np.angle(u) / (2 * np.pi)
        e = (esc < 0) & (np.abs(u) > Rb); esc[e] = n; logu[e] = np.log(np.abs(u[e]))
        u = np.where(esc < 0, u * u + Sb, u)
    t = np.full(Sb.shape, np.nan); G = np.full(Sb.shape, np.nan)
    ok = esc >= 1
    t[ok] = np.mod(args[esc[ok], np.nonzero(ok)[0], np.nonzero(ok)[1]], 1)
    G[ok] = logu[ok] / 2.0 ** (esc[ok] - 1)
    for k in range(NB - 2, 0, -1):
        m = ok & (esc > k)
        a = np.mod(args[k][m], 1); t0 = t[m] / 2; t1 = t0 + .5
        cd = lambda x, y: np.minimum(np.mod(np.abs(x - y), 1), 1 - np.mod(np.abs(x - y), 1))
        t[m] = np.where(cd(t0, a) <= cd(t1, a), t0, t1)
    return t, G
tz, Gz = lift(0j); tc, Gc = lift(C0)
dth = np.mod(tc - tz, 1); dG = Gc - Gz
live = ~np.isnan(dth) & ~np.isnan(dG)
# cyclic blue<->amber hue via the angle, contour stripes on log|ratio| = dG
cyc = 0.5 + 0.5 * np.cos(2 * np.pi * dth)
base = ramp(cyc, BLUE, AMBER)
stripes = 0.65 + 0.35 * np.cos(2 * np.pi * 6 * dG) ** 2
rgb = base * stripes[..., None]
rgb[~live] = DARK
save(rgb, "prev_boettcher_ratio.png")

# -------------------------------------------------------------- dual Buddhabrot
Wd = Hd = 480; MAXI = 300; SAMPLES = 3_000_000; BATCH = 100_000
ext = (-2.1, 1.1, -1.6, 1.6)
hz = np.zeros((Hd, Wd)); hc = np.zeros((Hd, Wd))
rng = np.random.default_rng(7)
def deposit(h, pts):
    x = ((pts.real - ext[0]) / (ext[1] - ext[0]) * Wd).astype(int)
    y = ((pts.imag - ext[2]) / (ext[3] - ext[2]) * Hd).astype(int)
    m = (x >= 0) & (x < Wd) & (y >= 0) & (y < Hd)
    np.add.at(h, (y[m], x[m]), 1)
for b in range(SAMPLES // BATCH):
    s = rng.uniform(-2.1, 1.1, BATCH) + 1j * rng.uniform(-1.6, 1.6, BATCH)
    for seed, h in ((0j, hz), (C0, hc)):
        u = np.full(BATCH, seed, complex); esc = np.zeros(BATCH, bool)
        with np.errstate(over="ignore", invalid="ignore"):
            for _ in range(MAXI):
                u = np.where(esc, u, u * u + s); esc |= np.abs(u) > 2
        ss = s[esc]; u = np.full(ss.shape, seed, complex)    # replay escaping orbits only
        alive = np.ones(ss.shape, bool)
        for _ in range(MAXI):
            u[alive] = u[alive] ** 2 + ss[alive]; alive &= np.abs(u) <= 2
            if not alive.any(): break
            deposit(h, u[alive])
def norm(h): l = np.log1p(h); return l / l.max()
nz, nc = norm(hz), norm(hc)
rgb = nz[..., None] * np.array(BLUE) + nc[..., None] * np.array(AMBER)
save(rgb / max(1, rgb.max()), "prev_dual_buddhabrot.png")
save(nc[..., None] * np.array(AMBER) / 1.0, "prev_dual_buddhabrot_c_only.png")
print("corr(z,c) =", np.corrcoef(nz.ravel(), nc.ravel())[0, 1])
