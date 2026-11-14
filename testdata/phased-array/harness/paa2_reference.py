# PAA-2 gate 3's reference: PAA-1's L0 / L1-centre / L1-classes / L2 (compare.py's own model()) formed on the COMMITTED fixtures
# (5.9 GHz, 5 degree grid), with each port's terminated embedded pattern taken from circuitRF's fields analysis one drive per port,
# as paa1.extract does on the full results. The engine (ArrayEngineTests) must reproduce these numbers to 1e-9.
# Usage: PAA1_WS=<scratch copy of this fixture folder> python3 paa2_reference.py > ../paa2_reference.json   (then compacted: json.dump with separators)
import numpy as np, json, os, subprocess, sys
import paa1, compare

STEP = 5.0

def extract(name, fGHz=5.9):
    """paa1.extract on the fixture's own grid: e[n, theta, phi, 2] per unit incident wave (others in Z0), S, C = U/|E|^2."""
    R, Cc = paa1.SHAPES[name]; n = R * Cc
    bench = subprocess.check_output(["python3", paa1.EXTRACT, paa1.WS, name, str(n), str(fGHz)], text=True).strip()
    e = None; Cs = []
    for k in range(1, n + 1):
        out = os.path.join(paa1.WS, "bench", f"{name}_F{k}.npy")
        subprocess.run([paa1.CLI, "fields", bench, "-a", f"F{k}", "-o", out], check=True, capture_output=True)
        a = np.load(out)
        g = lambda nm: a[nm if nm in a.dtype.names else nm.split(".", 1)[1]][0]
        et = g(f"F{k}.farfield.Etheta")[0, :, :, 0]; ep = g(f"F{k}.farfield.Ephi")[0, :, :, 0]
        U = g(f"F{k}.farfield.U")[0, :, :, 0]; S = g(f"F{k}.S")[0]
        if e is None: e = np.zeros((n,) + et.shape + (2,), complex)
        scale = np.sqrt(paa1.Z0) * (1 + S[k - 1, k - 1])
        e[k - 1, ..., 0] = et * scale; e[k - 1, ..., 1] = ep * scale
        Cs.append(U.max() / (abs(et) ** 2 + abs(ep) ** 2).max())
    th = np.radians(np.arange(e.shape[1]) * STEP); ph = np.radians(np.arange(e.shape[2]) * STEP)
    return dict(e=e, S=S, C=np.array(Cs), th=th, ph=ph, f=np.array(fGHz * 1e9))

def radiated(E, d):
    C = float(np.mean(d["C"])); U = C * (abs(E[..., 0]) ** 2 + abs(E[..., 1]) ** 2)
    w = np.sin(d["th"])[:, None] * np.radians(STEP) ** 2; w[0] *= 0.5; w[-1] *= 0.5
    return float(np.sum(U * w))

def main():
    tgt, sub = "e66", "e5"
    dt, ds, di = extract(tgt), extract(sub), extract("e1")
    R, C = paa1.SHAPES[tgt]; pos = paa1.positions(R, C)
    scans = [(0, 0)] + [(t, p) for p in (0, 90, 45) for t in (30, 45, 60)]
    out = {"target": tgt, "subarray": sub, "fHz": 5.9e9, "C": float(np.mean(dt["C"])), "scans": []}
    for t0, p0 in scans:
        a = paa1.steer(pos, 5.9e9, np.radians(t0), np.radians(p0))
        row = {"theta0": t0, "phi0": p0, "levels": {}}
        ref = None
        for lv in ("L2", "L0", "L1c", "L1k"):
            e, S = compare.model(lv, tgt, sub, ds, dt, di)
            E = np.tensordot(a, e, axes=(0, 0)); b = S @ a
            G = paa1.gain(E, a, dt, compare.P0)
            pacc = compare.P0 * np.sum(abs(a) ** 2 - abs(b) ** 2)
            gam = b / a
            r = dict(peakGainDbi=float(10 * np.log10(G.max())), powerBalance=radiated(E, dt) / pacc,
                     gammaRe=[float(x) for x in gam.real], gammaIm=[float(x) for x in gam.imag])
            if lv == "L2": ref = (G, gam)
            else:
                mask = ref[0] > ref[0].max() * 1e-2
                r["rmsErrorDb"] = float(np.sqrt(np.mean((10 * np.log10(G[mask]) - 10 * np.log10(ref[0][mask])) ** 2)))
                r["maxGammaError"] = float(abs(gam - ref[1]).max())
            row["levels"][lv] = r
        out["scans"].append(row)
    json.dump(out, sys.stdout, indent=1)

if __name__ == "__main__":
    main()
