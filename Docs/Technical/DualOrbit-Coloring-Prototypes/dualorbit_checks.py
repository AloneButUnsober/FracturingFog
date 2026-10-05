# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
# Prototype for Docs/Technical/DualOrbit-Coloring-RnD.md (epic #1114) - not shipped code.
"""Numerical checks for the dual-orbit colouring R&D write-up.
Each check tests an INDEPENDENT invariant, not self-consistency."""
import math, cmath, numpy as np

ok_all = True
def report(name, ok, detail):
    global ok_all
    ok_all &= ok
    print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")

# ---------------------------------------------------------------- C1 bicomplex
# Bicomplex basis 1,i,j,k=ij; i^2=j^2=-1, k^2=+1, all commute. Product from the
# basis table ONLY (no idempotent decomposition used in the arithmetic).
def bmul(p, q):
    a1,b1,c1,d1 = p; a2,b2,c2,d2 = q
    return (a1*a2 - b1*b2 - c1*c2 + d1*d2,
            a1*b2 + b1*a2 - c1*d2 - d1*c2,
            a1*c2 + c1*a2 - b1*d2 - d1*b2,
            a1*d2 + d1*a2 + b1*c2 + c1*b2)
def badd(p, q): return tuple(x+y for x,y in zip(p,q))
def cplx(z): return (z.real, z.imag, 0.0, 0.0)
E1 = (0.5, 0, 0, 0.5); E2 = (0.5, 0, 0, -0.5)   # (1+k)/2, (1-k)/2
# basis-table sanity: e1^2=e1, e2^2=e2, e1 e2=0
assert np.allclose(bmul(E1,E1), E1) and np.allclose(bmul(E2,E2), E2) and np.allclose(bmul(E1,E2), 0)

rng = np.random.default_rng(1)
worst = 0.0; flag_mismatch = 0; trials = 400
for _ in range(trials):
    s = complex(rng.uniform(-2, .6), rng.uniform(-1.2, 1.2))
    c = complex(rng.uniform(-1.5, 1.5), rng.uniform(-1.5, 1.5))
    w = badd(bmul(cplx(0j), E1), bmul(cplx(c), E2))
    z, u = 0j, c
    esc_w = esc_pair = None
    for n in range(60):
        # extract components by projection (multiply by idempotent)
        pz = bmul(w, E1); pc = bmul(w, E2)
        zw = complex(2*pz[0], 2*pz[1]); cw = complex(2*pc[0], 2*pc[1])
        if max(abs(z), abs(u)) < 1e3:
            worst = max(worst, abs(zw - z) / (1 + abs(z)), abs(cw - u) / (1 + abs(u)))
        if esc_w is None and max(abs(zw), abs(cw)) > 1e3: esc_w = n
        if esc_pair is None and max(abs(z), abs(u)) > 1e3: esc_pair = n
        if esc_w is not None and esc_pair is not None: break
        w = badd(bmul(w, w), cplx(s))
        z = z*z + s; u = u*u + s
    flag_mismatch += (esc_w != esc_pair)
report("C1 bicomplex equivalence", worst < 1e-9 and flag_mismatch == 0,
       f"max rel err {worst:.2e} over {trials} random (s,c); escape-step mismatches {flag_mismatch}")
# split-complex (m,e) form with hyperbolic unit k: w = m + e k, m=(z+c)/2, e=(z-c)/2
s, c = complex(-0.7, 0.3), complex(0.4, -0.2)
w = badd(bmul(cplx(0j), E1), bmul(cplx(c), E2))
m, e = c/2, -c/2
err = 0.0
for n in range(8):
    w = badd(bmul(w, w), cplx(s))
    m, e = m*m + e*e + s, 2*m*e
    # w = m + e k  -> components (Re m, Im m, -Im e, Re e)  [e k = (ex + ey i)k = ex k - ey j]
    err = max(err, abs(w[0]-m.real), abs(w[1]-m.imag), abs(w[2]+e.imag), abs(w[3]-e.real))
report("C1b split-complex (m,e) step m'=m^2+e^2+s, e'=2me", err < 1e-9, f"max abs err {err:.2e}")

# ------------------------------------------------- helpers: cycle + multiplier
def critical_cycle(s, n_settle=20000, pmax=64, tol=1e-10):
    z = 0j
    for _ in range(n_settle): z = z*z + s
    w = z
    for p in range(1, pmax+1):
        w = w*w + s
        if abs(w - z) < tol:
            cyc = [z]
            for _ in range(p-1): cyc.append(cyc[-1]**2 + s)
            mult = 1
            for zz in cyc: mult *= 2*zz          # derivative of f^p along the cycle
            return p, cyc, mult
    return None

def secant_lyap(s, c, N):
    z, u, acc = 0j, c, 0.0
    for _ in range(N):
        acc += math.log(max(abs(z + u), 1e-300)); z = z*z + s; u = u*u + s
    return acc / N, z, u

def phase_lag(s, c, p, N):
    z, u = 0j, c
    for _ in range(N): z = z*z + s; u = u*u + s
    zs = [z]
    for _ in range(p-1): zs.append(zs[-1]**2 + s)
    d = [abs(u - zz) for zz in zs]
    k = int(np.argmin(d)); return k, d[k]

# -------------------------------------- C2/C3 secant Lyapunov inside M_c
# Prediction (derived in the doc): lag 0 -> lambda -> log|mult|/p ; lag != 0 -> 0
cases = [complex(0.2, 0), complex(-0.9, 0), complex(-0.12, 0.74), complex(0.28, 0.53), complex(-1.3, 0.05)]
for s in cases:
    p, cyc, mult = critical_cycle(s)
    pred0 = math.log(abs(mult)) / p
    # sample c across the filled Julia interior, bucket by lag
    by_lag = {}
    for c in (complex(x, y) for x in np.linspace(-1.6, 1.6, 41) for y in np.linspace(-1.2, 1.2, 31)):
        if abs(c) < 1e-9: continue   # c = 0: identical orbits (D == 0), excluded
        uu = c
        bounded = True
        for _ in range(3000):
            uu = uu*uu + s
            if abs(uu) > 4: bounded = False; break
        if not bounded: continue
        N = 4000 - 4000 % p
        k1, d1 = phase_lag(s, c, p, N); k2, _ = phase_lag(s, c, p, N + p)
        lam, _, _ = secant_lyap(s, c, N)
        lam2, _, _ = secant_lyap(s, c, 2*N)
        by_lag.setdefault(k1, []).append((lam, lam2, k1 == k2, d1))
    lags = sorted(by_lag)
    stable = all(st for v in by_lag.values() for _,_,st,_ in v)
    msgs = []; good = stable and len(lags) == p
    for k in lags:
        lam2s = np.array([x[1] for x in by_lag[k]])
        target = pred0 if k == 0 else 0.0
        err = float(np.max(np.abs(lam2s - target)))
        good &= err < 0.02 * max(1, abs(pred0)) + 1e-3 if k else err < 0.02*abs(pred0) + 2e-3
        msgs.append(f"lag{k}: n={len(lam2s)} max|lam-{'log|mu|/p' if k==0 else '0'}|={err:.1e}")
    report(f"C3 secant-Lyapunov/phase-lag s={s}", good,
           f"p={p} log|mu|/p={pred0:.4f}; distinct lags={len(lags)} (expect p); lag stable={stable}; " + "; ".join(msgs))

# ------------------------------------------------- C4 Boettcher ratio
def lift_level1(u0, s, R, maxit=400):
    args = []; u = u0
    for n in range(maxit):
        args.append(cmath.phase(u))
        if abs(u) > R:
            t = (args[n] / (2*math.pi)) % 1.0
            if n == 0: return (2*t) % 1.0, None
            for k in range(n-1, 0, -1):
                a = (args[k] / (2*math.pi)) % 1.0
                t0, t1 = t/2, t/2 + .5
                cd = lambda x, y: min(abs(x-y) % 1, 1 - abs(x-y) % 1)
                t = t0 if cd(t0, a) <= cd(t1, a) else t1
            # Green fn at level 1: G(u_1) = 2^-(n-1) log|u_n|
            return t, math.log(abs(u)) / 2**(n-1)
        u = u*u + s
    return None, None

worst_inv = 0.0; worst_func = 0.0; worst_ray = 0.0; cnt = 0
for _ in range(300):
    s = complex(rng.uniform(-2.5, 1), rng.uniform(-1.5, 1.5)); c = complex(rng.uniform(-1.5,1.5), rng.uniform(-1.5,1.5))
    vals = []
    for R in (128.0, 4096.0):
        tz, gz = lift_level1(0j, s, R); tc, gc = lift_level1(c, s, R)
        if gz is None or gc is None: break
        vals.append((gc - gz, (tc - tz) % 1.0))
    if len(vals) < 2: continue
    cnt += 1
    dg = abs(vals[0][0] - vals[1][0]) / (1e-12 + abs(vals[0][0]) + 1e-3)
    da = min(abs(vals[0][1]-vals[1][1]), 1-abs(vals[0][1]-vals[1][1]))
    worst_inv = max(worst_inv, dg, da)
    # functional equation: phi(f(u)) = phi(u)^2  -> level-2 angle = 2x level-1 angle, G doubles
    tc1, gc1 = lift_level1(c, s, 4096.0)
    tc2, gc2 = lift_level1(c*c + s, s, 4096.0)      # level-1 of the next point = level 2 of c
    if gc2 is not None and gc1 > 1e-6:
        worst_func = max(worst_func, abs(gc2/gc1 - 2),
                         min(abs(tc2 - (2*tc1) % 1), 1 - abs(tc2 - (2*tc1) % 1)))
for sx in (0.3, 0.6, 1.0, 2.0):     # real s > 1/4: parameter ray 0
    t, _ = lift_level1(0j, complex(sx, 0), 4096.0); worst_ray = max(worst_ray, min(t, 1-t))
for sx in (-2.2, -3.0):             # real s < -2: parameter ray 1/2
    t, _ = lift_level1(0j, complex(sx, 0), 4096.0); worst_ray = max(worst_ray, abs(t - .5))
report("C4 Boettcher ratio log(phi_c/phi_z) = (G_c-G_z) + 2*pi*i*dTheta", worst_inv < 2e-3 and worst_func < 2e-3 and worst_ray < 1e-6,
       f"{cnt} samples: bailout 128 vs 4096 worst {worst_inv:.1e}; phi(f u)=phi(u)^2 worst {worst_func:.1e}; known rays worst {worst_ray:.1e}")

print("\nALL PASS" if ok_all else "\nSOME CHECKS FAILED")
