"""16APSK bit error rate in additive white Gaussian noise, by simulation (brief PCH-5, gate 4).

An independent reference for circuitRF's single-carrier demodulator: written from the constellation's definition
alone (standard library only, no circuitRF code), it draws random symbols, adds complex Gaussian noise and decides
each received point to the nearest constellation point.

The constellation is the one circuitRF states as its 16APSK default:
  4 points on the inner ring (radius 1, phase offset 45 degrees) and 12 on the outer ring (radius 2.85, offset
  15 degrees), equally spaced; labels 0-3 the inner ring counter-clockwise from its offset, 4-15 the outer ring the
  same way (the natural ring-wise mapping); scaled to unit mean power.

Es/N0 = 4 Eb/N0; the noise per complex symbol has variance N0 = 1 / (Es/N0) (Es = 1).

Run:  python3 apsk_ber_reference.py      writes apsk_ber_reference.json beside this file.
"""

import json
import math
import os
import random

RINGS = [(4, 1.0, 45.0), (12, 2.85, 15.0)]
EBN0_DB = [8.0, 10.0]
SYMBOLS = 2_000_000
SEED = 20261115


def constellation():
    pts = []
    for n, r, ph in RINGS:
        for j in range(n):
            a = math.radians(ph) + 2 * math.pi * j / n
            pts.append(complex(r * math.cos(a), r * math.sin(a)))
    mean = sum(abs(p) ** 2 for p in pts) / len(pts)
    return [p / math.sqrt(mean) for p in pts]


def main():
    pts = constellation()
    rng = random.Random(SEED)
    out = {"constellation": "16APSK, 4+12, radii 1:2.85, phases 45/15 deg, natural ring-wise labels, unit mean power",
           "symbols": SYMBOLS, "seed": SEED, "points": []}
    for ebn0_db in EBN0_DB:
        esn0 = 4 * 10 ** (ebn0_db / 10)
        sigma = math.sqrt(1 / (2 * esn0))
        errors = 0
        for _ in range(SYMBOLS):
            s = rng.randrange(16)
            y = pts[s] + complex(rng.gauss(0, sigma), rng.gauss(0, sigma))
            d = min(range(16), key=lambda l: abs(y - pts[l]))
            errors += bin(s ^ d).count("1")
        bits = 4 * SYMBOLS
        out["points"].append({"EbN0_dB": ebn0_db, "bits": bits, "bit_errors": errors, "ber": errors / bits})
        print(f"Eb/N0 {ebn0_db} dB: {errors} errors in {bits} bits, BER {errors / bits:.6g}")
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "apsk_ber_reference.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
