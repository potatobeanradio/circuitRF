# PAA-1 comparison harness (brief-paa-1-em-measurement.md); not a test and not in the solution. Embedded (terminated) patterns come from the product's own fields analysis, one drive per port.
import numpy as np, json, os, subprocess, sys
# The workspace holding the runs: PAA1_WS, else the fixture folder this harness sits in.
WS = os.environ.get("PAA1_WS", os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
CLI = os.environ.get("CRF_CLI", os.path.join(REPO, "src", "Cli", "bin", "Release", "net10.0", "CircuitRF.Cli"))
EXTRACT = os.path.join(os.path.dirname(__file__), "extract.py")
P = 25.86e-3; Z0 = 50.0; C0 = 299792458.0
SHAPES = {"e1": (1, 1), "e2x": (1, 2), "e2y": (2, 1), "e3x": (1, 4), "e3y": (4, 1), "e4": (2, 2), "e33": (3, 3),
          "e5": (5, 5), "e66": (6, 6), "l9x": (1, 9), "l9y": (9, 1), "l16x": (1, 16), "l16y": (16, 1), "e33aim": (3, 3), "e66aim": (6, 6)}

def positions(R, C):
    """Element reference points (patch centres), row-major from the top left seen from +z."""
    return np.array([((c - (C - 1) / 2) * P, ((R - 1) / 2 - r) * P) for r in range(R) for c in range(C)])

def load_cube(path, name):
    a = np.load(path); return a[name][0]

def extract(name, fGHz=5.9, weights=None):
    """Return dict: e[n,91,360,2] embedded pattern per unit incident wave (others in Z0), S, C (U/|E|^2), p0, grids."""
    cache = os.path.join(WS, "bench", f"{name}_{fGHz}_emb.npz")
    R, Cc = SHAPES[name]; n = R * Cc
    if os.path.exists(cache) and weights is None:
        d = np.load(cache); return {k: d[k] for k in d.files}
    w = None if weights is None else ",".join(f"{x.real}:{x.imag}" for x in weights)
    args = ["python3", EXTRACT, WS, name, str(n), str(fGHz)] + ([w] if w else [])
    bench = subprocess.check_output(args, text=True).strip()
    e = np.zeros((n, 91, 360, 2), complex); p0s = []; Cs = []
    for k in range(1, n + 1):
        out = os.path.join(WS, "bench", f"{name}_F{k}.npy")
        subprocess.run([CLI, "fields", bench, "-a", f"F{k}", "-o", out], check=True, capture_output=True)
        a = np.load(out)
        g = lambda nm: a[nm if nm in a.dtype.names else nm.split(".", 1)[1]][0]
        et = g(f"F{k}.farfield.Etheta")[0, :, :, 0]; ep = g(f"F{k}.farfield.Ephi")[0, :, :, 0]
        U = g(f"F{k}.farfield.U")[0, :, :, 0]
        S = g(f"F{k}.S")[0]
        rg = float(np.ravel(g(f"F{k}.farfield.RealizedGainDbi"))[0])
        scale = np.sqrt(Z0) * (1 + S[k - 1, k - 1])          # V_k per unit incident wave at port k
        e[k - 1, :, :, 0] = et * scale; e[k - 1, :, :, 1] = ep * scale
        E2 = abs(et) ** 2 + abs(ep) ** 2
        Cs.append(U.max() / E2.max())
        pav1 = 4 * np.pi * U.max() / 10 ** (rg / 10)        # available power per 1 V at port k
        p0s.append(pav1 * abs(scale) ** 2)                   # available power per unit |a|^2
    th = np.radians(np.arange(91)); ph = np.radians(np.arange(360))
    d = dict(e=e, S=S, C=np.array(Cs), p0=np.array(p0s), th=th, ph=ph, f=np.array(fGHz * 1e9))
    if weights is not None:
        a = np.load(os.path.join(WS, "bench", f"{name}_FW.npy")) if False else None
        out = os.path.join(WS, "bench", f"{name}_FW.npy")
        subprocess.run([CLI, "fields", bench, "-a", "FW", "-o", out], check=True, capture_output=True)
        a = np.load(out)
        g = lambda nm: a[nm if nm in a.dtype.names else nm.split(".", 1)[1]][0]
        d["FW"] = np.stack([g("FW.farfield.Etheta")[0, :, :, 0], g("FW.farfield.Ephi")[0, :, :, 0]], -1)
    else:
        np.savez(cache, **d)
    return d

def uhat(th, ph):
    T, Ph = np.meshgrid(th, ph, indexing="ij")
    return np.sin(T) * np.cos(Ph), np.sin(T) * np.sin(Ph)

def steer(pos, f, th0, ph0):
    k = 2 * np.pi * f / C0
    u0, v0 = np.sin(th0) * np.cos(ph0), np.sin(th0) * np.sin(ph0)
    return np.exp(-1j * k * (pos[:, 0] * u0 + pos[:, 1] * v0))

def shift(pos_n, d):
    """exp(+j k u.r_n) on the grid, for every element (n, 91, 360)."""
    k = 2 * np.pi * float(d["f"]) / C0
    u, v = uhat(d["th"], d["ph"])
    return np.exp(1j * k * (pos_n[:, 0, None, None] * u + pos_n[:, 1, None, None] * v))

def gain(E, a, d, p0):
    """Realized gain (linear) on the grid for field E[91,360,2] from incident waves a."""
    C = float(np.mean(d["C"]))
    U = C * (abs(E[..., 0]) ** 2 + abs(E[..., 1]) ** 2)
    return 4 * np.pi * U / (p0 * np.sum(abs(a) ** 2))

def radiated(E, d):
    C = float(np.mean(d["C"])); th = d["th"]
    U = C * (abs(E[..., 0]) ** 2 + abs(E[..., 1]) ** 2)
    dth = dph = np.radians(1)
    w = np.sin(th)[:, None] * dth * dph; w[0] *= 0.5; w[-1] *= 0.5
    return float(np.sum(U * w))
