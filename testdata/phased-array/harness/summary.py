import numpy as np, compare, paa1
cases = [("e2x", None), ("e2y", None), ("e3x", None), ("e3y", None), ("e4", None), ("e33", None), ("e5", None),
         ("l9x", "e3x"), ("l9y", "e3y"), ("e5", "e33")]
def scans(t):
    R, C = paa1.SHAPES[t]; s = [(0, 0)]
    for p in ([0] if R == 1 else [90] if C == 1 else [0, 90, 45]):
        s += [(x, p) for x in (30, 45, 60)]
    return s
def main():
    print("| target ← sub-array | level | max |ΔGr peak| dB | max RMS pattern err dB | max |ΔΓact| | RMS |ΔΓact| (worst scan) | max |Prad/Pacc − L2's| |")
    print("|---|---|---|---|---|---|---|")
    for t, s in cases:
        rows = compare.run(t, s, scans(t))
        l2 = {(r[2], r[3]): r for r in rows if r[4] == "L2"}
        for lv in ("L0", "L1c", "L1k"):
            rr = [r for r in rows if r[4] == lv]
            if not rr: continue
            pb = max(abs(r[14] - l2[(r[2], r[3])][14]) for r in rr)
            print(f"| {t} ← {s or 'e1 (isolated)'} | {lv} | {max(abs(r[6]) for r in rr):.2f} | {max(r[10] for r in rr):.2f} | {max(r[11] for r in rr):.3f} | {max(r[12] for r in rr):.3f} | {pb:.3f} |")

if __name__ == "__main__":
    main()
