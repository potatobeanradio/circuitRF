# Per position class: how far each element's own embedded pattern (referred to its own position) is from the centre's.
import numpy as np, paa1, compare, sys
name = sys.argv[1]
d = paa1.extract(name); R, C = paa1.SHAPES[name]; pos = paa1.positions(R, C)
ic = (R // 2) * C + C // 2 if R % 2 else (R // 2 - 1) * C + (C // 2 - 1)
def own(i): return compare.rr(d["e"][i], pos[i], d)
G = lambda e: paa1.gain(e, np.array([1.0]), d, 0.5)
gc = G(own(ic))
def cls(r, c):
    er, ec = r in (0, R - 1), c in (0, C - 1)
    return "corner" if er and ec else ("edge (row end, E-plane side)" if er else ("edge (col end, H-plane side)" if ec else "interior"))
out = {}
for i in range(R * C):
    r, c = divmod(i, C); g = G(own(i)); m = gc > gc.max() * 1e-2
    rms = np.sqrt(np.mean((10 * np.log10(g[m] / gc[m])) ** 2)); dpk = 10 * np.log10(g.max() / gc.max())
    ds = abs(d["S"][i, i] - d["S"][ic, ic])
    out.setdefault(cls(r, c), []).append((rms, dpk, ds))
print(f"| {name} class | n | RMS pattern diff vs centre dB (mean/max) | peak gain diff dB (max abs) | |S_nn − S_cc| (max) |")
print("|---|---|---|---|---|")
for k, v in out.items():
    v = np.array(v); print(f"| {k} | {len(v)} | {v[:,0].mean():.2f} / {v[:,0].max():.2f} | {abs(v[:,1]).max():.2f} | {v[:,2].max():.3f} |")
