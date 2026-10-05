# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
# Prototype for Docs/Technical/DualOrbit-Coloring-RnD.md (epic #1114) - not shipped code.
"""Dual Buddhabrot prototype v2: min-escape cut + joint-outcome channels.
Channels (s sampled uniformly, c fixed):
  Z  : z-orbit (seed 0) trajectories, z escaped            -> classic Buddhabrot
  CB : c-orbit trajectories where c escaped AND z bounded  -> s in M \\ M_c (Julia-escape orbits)
  CE : c-orbit trajectories where both escaped
"""
import sys, numpy as np
from PIL import Image

C0 = complex(sys.argv[1]) if len(sys.argv) > 1 else 0.5 + 0j
W = H = 480; MAXI = 400; NMIN = 12; SAMPLES = 4_000_000; BATCH = 100_000
ext = (-2.0, 1.2, -1.6, 1.6)
hZ, hCB, hCE = (np.zeros((H, W)) for _ in range(3))
rng = np.random.default_rng(11)

def deposit(h, pts):
    x = ((pts.real - ext[0]) / (ext[1] - ext[0]) * W).astype(int)
    y = ((pts.imag - ext[2]) / (ext[3] - ext[2]) * H).astype(int)
    m = (x >= 0) & (x < W) & (y >= 0) & (y < H)
    np.add.at(h, (y[m], x[m]), 1)

def escape_n(seed, s):
    u = np.full(s.shape, seed, complex); n = np.full(s.shape, -1)
    with np.errstate(over="ignore", invalid="ignore"):
        for i in range(MAXI):
            u = np.where(n < 0, u * u + s, u); e = (n < 0) & (np.abs(u) > 2); n[e] = i
    return n

def replay(h, seed, s):
    u = np.full(s.shape, seed, complex); alive = np.ones(s.shape, bool)
    for _ in range(MAXI):
        u[alive] = u[alive] ** 2 + s[alive]; alive &= np.abs(u) <= 2
        if not alive.any(): break
        deposit(h, u[alive])

for b in range(SAMPLES // BATCH):
    r = 2.0 * np.sqrt(rng.uniform(0, 1, BATCH)); t = rng.uniform(0, 2 * np.pi, BATCH)
    s = r * np.exp(1j * t)                                   # |s| <= 2
    nz, nc = escape_n(0j, s), escape_n(C0, s)
    replay(hZ, 0j, s[nz >= NMIN])
    replay(hCB, C0, s[(nc >= NMIN) & (nz < 0)])
    replay(hCE, C0, s[(nc >= NMIN) & (nz >= 0)])

def norm(h, q=0.999):
    v = np.sqrt(h); top = np.quantile(v[v > 0], q) if (v > 0).any() else 1
    return np.clip(v / top, 0, 1)
BLUE, AMBER, WHITE = np.array((0.15, 0.35, 0.95)), np.array((1.0, 0.72, 0.1)), np.array((0.9, 0.9, 0.95))
tag = f"{C0.real:+.2f}{C0.imag:+.2f}i".replace(".", "p")
def save(a, name): Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8)).save(name); print("wrote", name)
save(norm(hZ)[..., None] * WHITE, f"db_Z.png")
save(norm(hCB)[..., None] * AMBER, f"db_CB_{tag}.png")
save(norm(hCE)[..., None] * BLUE, f"db_CE_{tag}.png")
comp = norm(hZ)[..., None] * BLUE + norm(hCB)[..., None] * AMBER + 0.6 * norm(hCE)[..., None] * WHITE
save(comp / max(1, comp.max()), f"db_composite_{tag}.png")
print("orbits:", {k: int(v.sum()) for k, v in (("Z", hZ), ("CB", hCB), ("CE", hCE))})
print("corr(Z,CB) =", np.corrcoef(norm(hZ).ravel(), norm(hCB).ravel())[0, 1],
      " corr(Z,CE) =", np.corrcoef(norm(hZ).ravel(), norm(hCE).ravel())[0, 1])
