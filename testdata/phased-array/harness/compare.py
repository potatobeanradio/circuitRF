# PAA-1: L0 / L1-centre / L1-classes against L2 for an array, at a set of scans.
import numpy as np, paa1, sys
P0 = 0.5  # available power per unit |a|^2 (peak waves), confirmed per port by extract()

def rr(src_pattern, pos_src, d):
    """Refer a pattern to its own element: multiply by exp(-j k u.r_src)."""
    return src_pattern * np.conj(paa1.shift(pos_src[None, :], d)[0])[..., None]

def sub_index(r, R, Rs):
    if Rs == 1: return 0
    if r == 0: return 0
    if r == R - 1: return Rs - 1
    return Rs // 2 if Rs % 2 else Rs // 2 - 1

def extend_S(R, C, Rs, Cs, Ssub, diag=None):
    n = R * C; S = np.zeros((n, n), complex)
    rc = [(r, c) for r in range(R) for c in range(C)]
    src = [(r, c) for r in range(Rs) for c in range(Cs)]
    cen = ((Rs - 1) / 2, (Cs - 1) / 2)
    best = {}
    for i, (ri, ci) in enumerate(src):
        for j, (rj, cj) in enumerate(src):
            off = (rj - ri, cj - ci)
            dist = ((ri + rj) / 2 - cen[0]) ** 2 + ((ci + cj) / 2 - cen[1]) ** 2
            if off not in best or dist < best[off][0] - 1e-12:
                best[off] = (dist, Ssub[i, j])
    for a, (ra, ca) in enumerate(rc):
        for b, (rb, cb) in enumerate(rc):
            off = (rb - ra, cb - ca)
            if off in best: S[a, b] = best[off][1]
    if diag is not None: S[np.diag_indices(n)] = diag
    return S

def model(level, tgt, sub, dsub, dtgt, diso):
    """Return (patterns e[n,..,2] referred to the target positions, S) for a level."""
    R, C = paa1.SHAPES[tgt]; pos = paa1.positions(R, C); n = R * C
    sh = paa1.shift(pos, dtgt)                          # exp(+jk u.r_n)
    if level == "L2":
        return dtgt["e"], dtgt["S"]
    if level == "L0":
        e0 = diso["e"][0]                               # isolated element at the origin
        return e0[None] * sh[..., None], np.diag(np.full(n, diso["S"][0, 0]))
    Rs, Cs = paa1.SHAPES[sub]; ps = paa1.positions(Rs, Cs)
    rc = [(r, c) for r in range(R) for c in range(C)]
    if level == "L1c":
        ic = sub_index(1, 3, Rs) * Cs + sub_index(1, 3, Cs) if Rs > 1 else sub_index(1, 3, Cs)
        ec = rr(dsub["e"][ic], ps[ic], dsub)
        e = ec[None] * sh[..., None]
        S = extend_S(R, C, Rs, Cs, dsub["S"], diag=np.full(n, dsub["S"][ic, ic]))
        return e, S
    if level == "L1k":
        e = np.zeros((n,) + dsub["e"].shape[1:], complex); diag = np.zeros(n, complex)
        for k, (r, c) in enumerate(rc):
            i = sub_index(r, R, Rs) * Cs + sub_index(c, C, Cs)
            e[k] = rr(dsub["e"][i], ps[i], dsub) * sh[k][..., None]; diag[k] = dsub["S"][i, i]
        return e, extend_S(R, C, Rs, Cs, dsub["S"], diag=diag)

def cut(G, th0, ph0):
    """Principal cut through the scan plane, theta -90..90 (negative = phi0+180)."""
    pi = int(round(np.degrees(ph0))) % 360; pj = (pi + 180) % 360
    return np.concatenate([G[:0:-1, pj], G[:, pi]])     # theta = -90..90

def metrics(G, th0, ph0):
    i = np.unravel_index(np.argmax(G), G.shape)
    thp, php = np.radians(i[0]), np.radians(i[1])
    u = lambda t, p: np.array([np.sin(t) * np.cos(p), np.sin(t) * np.sin(p), np.cos(t)])
    point = np.degrees(np.arccos(np.clip(u(thp, php) @ u(th0, ph0), -1, 1)))
    c = cut(G, th0, ph0); m = int(np.argmax(c))
    lo = m
    while lo > 0 and c[lo - 1] < c[lo]: lo -= 1
    hi = m
    while hi < len(c) - 1 and c[hi + 1] < c[hi]: hi += 1
    side = np.concatenate([c[:lo], c[hi + 1:]])
    sll = 10 * np.log10(side.max() / c[m]) if side.size and side.max() > 0 else np.nan
    return 10 * np.log10(G.max()), point, sll, u(thp, php)

def run(tgt, sub, scans, levels=("L0", "L1c", "L1k"), fGHz=5.9, fsteer=5.9e9):
    dt = paa1.extract(tgt, fGHz); di = paa1.extract("e1", fGHz); ds = paa1.extract(sub, fGHz) if sub else None
    R, C = paa1.SHAPES[tgt]; pos = paa1.positions(R, C); f = fsteer
    rows = []
    for (t0, p0) in scans:
        th0, ph0 = np.radians(t0), np.radians(p0)
        a = paa1.steer(pos, f, th0, ph0)
        res = {}
        for lv in ("L2",) + tuple(l for l in levels if (sub or l == "L0")):
            e, S = model(lv, tgt, sub, ds, dt, di)
            E = np.tensordot(a, e, axes=(0, 0)); b = S @ a
            G = paa1.gain(E, a, dt, P0)
            pacc = P0 * np.sum(abs(a) ** 2 - abs(b) ** 2)
            res[lv] = dict(G=G, gam=b / a, pb=paa1.radiated(E, dt) / pacc, m=metrics(G, th0, ph0))
        ref = res["L2"]; mask = ref["G"] > ref["G"].max() * 1e-2
        for lv, r in res.items():
            rms = np.sqrt(np.mean((10 * np.log10(r["G"][mask]) - 10 * np.log10(ref["G"][mask])) ** 2))
            dg = abs(r["gam"] - ref["gam"])
            dpt = np.degrees(np.arccos(np.clip(r["m"][3] @ ref["m"][3], -1, 1)))
            rows.append((f"{tgt}@{fGHz}", sub or "-", t0, p0, lv, r["m"][0], r["m"][0] - ref["m"][0], r["m"][1], dpt, r["m"][2],
                         rms, dg.max(), np.sqrt(np.mean(dg ** 2)), np.max(abs(ref["gam"])), r["pb"]))
    return rows

HDR = "| array | from | θ0 | φ0 | level | Gr peak dBi | ΔGr dB | point err ° | Δpoint vs L2 ° | SLL dB | RMS err dB (>−20 dB) | max |ΔΓact| | RMS |ΔΓact| | max |Γact| (L2) | Prad/Pacc |"
def table(rows):
    out = [HDR, "|" + "---|" * 15]
    for r in rows:
        out.append("| " + " | ".join(str(x) if isinstance(x, str) or isinstance(x, int) else
                                      (f"{x:.3f}" if abs(x) >= 1e-3 or x == 0 else f"{x:.1e}") for x in r) + " |")
    return "\n".join(out)

if __name__ == "__main__":
    tgt, sub = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None)
    R, C = paa1.SHAPES[tgt]
    scans = [(0, 0)]
    planes = ([0] if R == 1 else [90] if C == 1 else [0, 90, 45])
    for p in planes:
        for t in (30, 45, 60): scans.append((t, p))
    print(table(run(tgt, sub, scans)))
