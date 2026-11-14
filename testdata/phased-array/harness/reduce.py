# Reduce a planar EM result to one frequency and a coarser angular grid by SAMPLING (never re-solving).
# Usage: reduce.py <results_dir> <name> <out_results_dir> <fHz> <stepDeg>
import numpy as np, json, sys, os, shutil
src, name, dst, fsel, step = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]), int(sys.argv[5])
os.makedirs(dst, exist_ok=True)
a = np.load(os.path.join(src, f"{name}_em.npy"))
meta = json.loads(a["__meta__"][0])
fields = [n for n in a.dtype.names if n != "__meta__"]
new_cols, new_meta = {}, {"format_version": meta["format_version"], "groups": meta["groups"]}
for fn in fields:
    m = meta[fn]; arr = a[fn][0]; idx = []
    axes = []
    for ax in m["axes"]:
        v = np.array(ax["values"], float)
        if ax["name"] in ("freq", "f"):
            k = int(np.argmin(abs(v - fsel))); assert abs(v[k] - fsel) < 1, (fn, v)
            idx.append([k]); ax = dict(ax, values=[ax["values"][k]])
        elif ax["name"] in ("theta", "phi"):
            keep = [i for i, x in enumerate(v) if abs(x / step - round(x / step)) < 1e-9]
            idx.append(keep); ax = dict(ax, values=[ax["values"][i] for i in keep])
        else:
            idx.append(list(range(len(v))))
        axes.append(ax)
    sub = arr[np.ix_(*idx)] if idx else arr
    new_cols[fn] = sub; new_meta[fn] = dict(m, axes=axes)
dt = [(fn, new_cols[fn].dtype, new_cols[fn].shape) for fn in fields]
mj = json.dumps(new_meta, ensure_ascii=False).encode("utf-8")
dt.append(("__meta__", f"S{len(mj)}"))
out = np.zeros(1, dtype=dt)
for fn in fields: out[fn][0] = new_cols[fn]
out["__meta__"][0] = mj
np.save(os.path.join(dst, f"{name}_em.npy"), out)
# Touchstone: keep the header and the one frequency's block
rec = json.load(open(os.path.join(src, f"{name}.cemr")))
snp = rec["Files"]["Snp"]; lines = open(os.path.join(src, snp)).read().splitlines(); keep = []; unit = 1e9
blk = None
for l in lines:
    if l.startswith("!") or l.startswith("#"):
        keep.append(l); continue
    tok = l.split()
    if tok and len(tok) % 2 == 1 and abs(float(tok[0]) * unit - fsel) < 1:
        blk = True; keep.append(l); continue
    if tok and len(tok) % 2 == 1: blk = False; continue
    if blk: keep.append(l)
open(os.path.join(dst, snp), "w").write("\n".join(keep) + "\n")
for key in ("SweepHz", "FarFieldHz"):
    rec["Frequencies"][key] = [f for f in rec["Frequencies"][key] if abs(f - fsel) < 1]
json.dump(rec, open(os.path.join(dst, f"{name}.cemr"), "w"), indent=2)
print(name, os.path.getsize(os.path.join(dst, f"{name}_em.npy")) // 1024, "KB")
