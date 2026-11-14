import numpy as np, compare, paa1
from summary import scans
cases = [("e66", None), ("e66", "e33"), ("e66", "e5"), ("l16x", "e3x"), ("l16x", "l9x"), ("l16y", "e3y"), ("l16y", "l9y")]
print("| target ← sub-array | level | max |ΔGr peak| dB | max RMS pattern err dB | max |ΔΓact| | RMS |ΔΓact| (worst scan) | max |Prad/Pacc − L2's| |")
print("|---|---|---|---|---|---|---|")
for t, s in cases:
    rows = compare.run(t, s, scans(t))
    l2 = {(r[2], r[3]): r for r in rows if r[4] == "L2"}
    for lv in (("L0",) if s is None else ("L1c", "L1k")):
        rr = [r for r in rows if r[4] == lv]
        pb = max(abs(r[14] - l2[(r[2], r[3])][14]) for r in rr)
        print(f"| {t} ← {s or 'e1 (isolated)'} | {lv} | {max(abs(r[6]) for r in rr):.2f} | {max(r[10] for r in rr):.2f} | {max(r[11] for r in rr):.3f} | {max(r[12] for r in rr):.3f} | {pb:.3f} |")
